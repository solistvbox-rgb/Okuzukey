using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Minimal, Komga-specific HTTP client used exclusively by <c>KomgaProvider</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Komga is a single, user-controlled server with no public rate limit; the actual concern
    /// is not overwhelming potentially weak hardware (a Raspberry Pi, say) with a burst of
    /// parallel requests during a library scan. A simple bounded concurrency limit
    /// (<see cref="Configuration.PluginConfiguration.KomgaMaxConcurrentRequests"/>, see
    /// <see cref="GetGate"/>) via a single shared <see cref="SemaphoreSlim"/> addresses that
    /// directly. It is configurable because the right value depends on the Komga hardware.
    /// </para>
    /// <para>
    /// No HTTPS enforcement: unlike the public APIs (where <c>HttpHelper.EnsureHttps</c> upgrades
    /// every URL), Komga is commonly self-hosted on a local network without a TLS certificate, so
    /// the URL is used exactly as configured (<c>PluginConfiguration.KomgaBaseUrl</c>), whether
    /// <c>http://</c> or <c>https://</c>.
    /// </para>
    /// </remarks>
    internal static class KomgaHttpClient
    {
        private const string UserAgent = "Emby.Okuzukey/1.0";

        /// <summary>Default/fallback for <see cref="Configuration.PluginConfiguration.KomgaMaxConcurrentRequests"/>.</summary>
        public const int DefaultConcurrentRequests = 4;

        /// <summary>Lowest concurrency value the dashboard setting accepts.</summary>
        public const int MinConcurrentRequests = 1;

        /// <summary>
        /// Highest concurrency value the dashboard setting accepts - a hard cap so a stray large
        /// number (e.g. someone typing "2000") cannot open an unbounded number of simultaneous
        /// connections to the user's own Komga server.
        /// </summary>
        public const int MaxAllowedConcurrentRequests = 20;

        private static readonly object GateLock = new object();
        private static SemaphoreSlim _gate;
        private static int _gateLimit = -1;

        /// <summary>Consecutive connection failures/timeouts after which requests are paused.</summary>
        private const int CircuitFailureThreshold = 3;

        /// <summary>
        /// How long all Komga requests fail fast once the circuit has opened. Short on purpose: a
        /// user who starts Komga again and immediately re-scans must not be stuck waiting. After
        /// it, only <em>one</em> probe request is let through (see <see cref="TryEnter"/>), so a
        /// dead server that silently drops packets costs at most one timeout per cooldown.
        /// </summary>
        private static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(20);

        /// <summary>Minimum time between two dialogs/warnings of the same kind, independent of the cooldown.</summary>
        private static readonly TimeSpan NotifyInterval = TimeSpan.FromMinutes(2);

        private static int _consecutiveFailures;
        private static long _circuitOpenUntilTicks;
        private static int _probeInFlight;
        private static long _unreachableNotifyUntilTicks;
        private static long _authNotifyUntilTicks;
        private static long _redirectNotifyUntilTicks;

        /// <summary>
        /// Decides whether a request must fail immediately. Without the circuit breaker an offline
        /// Komga makes every single request of a library scan wait the full timeout (e.g. 2500
        /// books x several requests x 30s) - hours instead of seconds. After
        /// <see cref="CircuitFailureThreshold"/> consecutive connection failures/timeouts the
        /// circuit is open for <see cref="CircuitCooldown"/>; afterwards ("half-open") exactly one
        /// request is admitted as a probe while all others still fail fast. The failure counter is
        /// deliberately not reset when the cooldown ends, so a failing probe re-opens the circuit at
        /// once, while any received HTTP response (even a 4xx/5xx, proving the server is up) closes
        /// it. <see cref="ResetCircuit"/> closes it on demand.
        /// </summary>
        /// <param name="isProbe"><c>true</c> if the caller is the single half-open probe and must
        /// call <see cref="EndProbe"/> when done.</param>
        private static bool TryEnter(out bool isProbe)
        {
            isProbe = false;

            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _circuitOpenUntilTicks))
            {
                return false;
            }

            if (Volatile.Read(ref _consecutiveFailures) >= CircuitFailureThreshold)
            {
                if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
                {
                    return false;
                }

                isProbe = true;
            }

            return true;
        }

        private static bool IsCircuitOpen()
        {
            return DateTime.UtcNow.Ticks < Interlocked.Read(ref _circuitOpenUntilTicks);
        }

        private static void EndProbe()
        {
            Volatile.Write(ref _probeInFlight, 0);
        }

        /// <summary>
        /// Closes the circuit and re-arms the notifications immediately. Called whenever the plugin
        /// settings are saved, so fixing the URL/API key - or simply ticking "Enable Komga provider"
        /// that had been forgotten - takes effect at once instead of after the cooldown.
        /// </summary>
        internal static void ResetCircuit()
        {
            Volatile.Write(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _circuitOpenUntilTicks, 0);
            Volatile.Write(ref _probeInFlight, 0);
            Interlocked.Exchange(ref _unreachableNotifyUntilTicks, 0);
            Interlocked.Exchange(ref _authNotifyUntilTicks, 0);
            Interlocked.Exchange(ref _redirectNotifyUntilTicks, 0);
        }

        private static void RegisterSuccess()
        {
            if (Interlocked.Exchange(ref _consecutiveFailures, 0) >= CircuitFailureThreshold)
            {
                // Komga is back: a later outage should be reported again straight away.
                Interlocked.Exchange(ref _unreachableNotifyUntilTicks, 0);
            }
        }

        private static void RegisterFailure(ILogger logger, string url, string reason)
        {
            logger.Debug("[Okuzukey] Komga request to '{0}' failed: {1}.", RedactUrl(url), reason);

            if (Interlocked.Increment(ref _consecutiveFailures) < CircuitFailureThreshold)
            {
                return;
            }

            Interlocked.Exchange(ref _circuitOpenUntilTicks, DateTime.UtcNow.Add(CircuitCooldown).Ticks);

            if (TryClaimWindow(ref _unreachableNotifyUntilTicks))
            {
                logger.Warn(
                    "[Okuzukey] Komga appears to be unreachable ({0}); pausing Komga requests so the library scan is not blocked by timeouts.",
                    reason);

                AdminNotifier.KomgaUnreachable();
            }
        }

        /// <summary>
        /// True at most once per <see cref="NotifyInterval"/> per <paramref name="untilTicks"/>
        /// field: a wrong API key answers every request with 401/403 immediately (fast, so no
        /// circuit breaker needed), but one warning and one dialog per scan is enough instead of one
        /// per request.
        /// </summary>
        private static bool TryClaimWindow(ref long untilTicks)
        {
            var now = DateTime.UtcNow.Ticks;
            var until = Interlocked.Read(ref untilTicks);
            return now >= until
                   && Interlocked.CompareExchange(ref untilTicks, now + NotifyInterval.Ticks, until) == until;
        }

        private static bool ShouldReportAuthRejection()
        {
            return TryClaimWindow(ref _authNotifyUntilTicks);
        }

        /// <summary>
        /// Returns the shared concurrency gate, sized to the currently configured
        /// <see cref="Configuration.PluginConfiguration.KomgaMaxConcurrentRequests"/> (clamped to
        /// <see cref="MinConcurrentRequests"/>..<see cref="MaxAllowedConcurrentRequests"/>,
        /// falling back to <see cref="DefaultConcurrentRequests"/> if unset/out of range).
        /// <see cref="SemaphoreSlim"/> cannot be resized in place, so changing the configured
        /// value replaces the gate for future requests; any request already waiting on the old
        /// gate is unaffected and still releases it correctly, since <see cref="SendAsync"/>
        /// captures one gate instance up front and uses it for both the wait and the release.
        /// </summary>
        private static SemaphoreSlim GetGate()
        {
            var configured = Plugin.Instance?.Configuration?.KomgaMaxConcurrentRequests ?? DefaultConcurrentRequests;
            var clamped = configured < MinConcurrentRequests || configured > MaxAllowedConcurrentRequests
                ? DefaultConcurrentRequests
                : configured;

            if (clamped != _gateLimit)
            {
                lock (GateLock)
                {
                    if (clamped != _gateLimit)
                    {
                        _gate = new SemaphoreSlim(clamped, clamped);
                        _gateLimit = clamped;
                    }
                }
            }

            return _gate;
        }

        private static readonly HttpClient Client = CreateClient();

        /// <summary>
        /// Automatic redirects are disabled: .NET only strips the <c>Authorization</c> header when
        /// following a redirect, not a custom <c>X-API-Key</c>, so a redirect (from a misconfigured
        /// reverse proxy, or an attacker on a plain-<c>http://</c> network path) would forward the
        /// Komga API key to another host. A redirect is logged as a failed request instead.
        /// </summary>
        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
            return client;
        }

        /// <summary>
        /// Removes a <c>user:password@</c> part from <paramref name="url"/> before it is written to
        /// the log (the same cleanup <c>AdminNotifier</c> does for its dialog text).
        /// </summary>
        internal static string RedactUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.IndexOf('@') < 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            {
                return url;
            }

            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
        }

        /// <summary>
        /// Sends a <c>GET</c> request to <paramref name="url"/> with the <c>X-API-Key</c> header
        /// and returns the response body as a string, or <c>null</c> on any failure (timeout,
        /// non-success status code, network error, oversized response). Never throws except on
        /// cooperative cancellation via <paramref name="cancellationToken"/>.
        /// </summary>
        public static Task<string> GetStringAsync(string url, string apiKey, int timeoutSeconds, ILogger logger, CancellationToken cancellationToken)
        {
            return SendAsync(url, apiKey, timeoutSeconds, logger, cancellationToken, async (response, token) =>
            {
                using (var memoryStream = await ReadBodyAsync(response, BoundedReader.MaxJsonBytes, token).ConfigureAwait(false))
                using (var reader = new StreamReader(memoryStream, GetEncoding(response), true))
                {
                    return reader.ReadToEnd();
                }
            });
        }

        /// <summary>
        /// Sends a <c>GET</c> request to <paramref name="url"/> with the <c>X-API-Key</c> header
        /// and returns the response body as an in-memory stream together with its content type
        /// (used for cover image downloads), or <c>null</c> on any failure. Never throws except
        /// on cooperative cancellation.
        /// </summary>
        public static Task<KomgaStreamResult> GetStreamAsync(string url, string apiKey, int timeoutSeconds, ILogger logger, CancellationToken cancellationToken)
        {
            return SendAsync(url, apiKey, timeoutSeconds, logger, cancellationToken, async (response, token) => new KomgaStreamResult
            {
                Stream = await ReadBodyAsync(response, BoundedReader.MaxImageBytes, token).ConfigureAwait(false),
                ContentType = response.Content.Headers.ContentType?.MediaType
            });
        }

        private static async Task<MemoryStream> ReadBodyAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
        {
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue)
            {
                BoundedReader.ThrowIfTooLarge(declaredLength.Value, maxBytes);
            }

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                return await BoundedReader.ReadToMemoryAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        private static Encoding GetEncoding(HttpResponseMessage response)
        {
            var charSet = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charSet))
            {
                try
                {
                    return Encoding.GetEncoding(charSet.Trim('"'));
                }
                catch (ArgumentException)
                {
                    // Unknown charset - fall back to UTF-8, Komga's own encoding.
                }
            }

            return Encoding.UTF8;
        }

        private static async Task<T> SendAsync<T>(string url, string apiKey, int timeoutSeconds, ILogger logger, CancellationToken cancellationToken, Func<HttpResponseMessage, CancellationToken, Task<T>> readBody)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(url) || !TryEnter(out var isProbe))
            {
                return null;
            }

            try
            {
                return await SendCoreAsync(url, apiKey, timeoutSeconds, logger, cancellationToken, readBody).ConfigureAwait(false);
            }
            finally
            {
                if (isProbe)
                {
                    EndProbe();
                }
            }
        }

        /// <summary>
        /// The timeout covers the whole exchange, headers <em>and</em> body: before, only the
        /// headers were covered (<see cref="HttpCompletionOption.ResponseHeadersRead"/>), so a
        /// server that stalled mid-body could hang a library scan indefinitely.
        /// </summary>
        private static async Task<T> SendCoreAsync<T>(string url, string apiKey, int timeoutSeconds, ILogger logger, CancellationToken cancellationToken, Func<HttpResponseMessage, CancellationToken, Task<T>> readBody)
            where T : class
        {
            var effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
            var gate = GetGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-checked after (possibly long) queueing at the gate: requests that were already
                // waiting when the circuit opened must not each still try and time out.
                if (IsCircuitOpen())
                {
                    return null;
                }

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeout)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                {
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
                    }

                    HttpResponseMessage response;
                    try
                    {
                        response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        RegisterFailure(logger, url, "timed out after " + effectiveTimeout + "s");
                        return null;
                    }

                    using (response)
                    {
                        // Any HTTP response at all (even 4xx/5xx) proves the server is reachable.
                        RegisterSuccess();

                        if (response.IsSuccessStatusCode)
                        {
                            try
                            {
                                return await readBody(response, linkedCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Headers arrived, so the server is up; a stalled body is not a
                                // reason to open the circuit, just a failed request.
                                logger.Warn("[Okuzukey] Komga response from '{0}' was not received completely within {1}s.", RedactUrl(url), effectiveTimeout);
                                return null;
                            }
                            catch (InvalidDataException ex)
                            {
                                logger.Warn("[Okuzukey] Komga response from '{0}' was discarded: {1}", RedactUrl(url), ExceptionText.Describe(ex));
                                return null;
                            }
                        }

                        LogUnsuccessfulResponse(response, url, logger);
                        return null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                // Connection refused / host unreachable / DNS failure: Komga is not reachable.
                RegisterFailure(logger, url, ExceptionText.Describe(ex));
                return null;
            }
            catch (Exception ex)
            {
                logger.ErrorException("[Okuzukey] Komga request to '{0}' failed.", ex, RedactUrl(url));
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        private static void LogUnsuccessfulResponse(HttpResponseMessage response, string url, ILogger logger)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode == 401 || statusCode == 403)
            {
                if (ShouldReportAuthRejection())
                {
                    logger.Warn(
                        "[Okuzukey] Komga rejected the request to '{0}' with HTTP {1} - check the configured Komga API key.",
                        RedactUrl(url),
                        statusCode);
                    AdminNotifier.KomgaApiKeyRejected();
                }
                else
                {
                    logger.Debug("[Okuzukey] Komga rejected the request to '{0}' with HTTP {1}.", RedactUrl(url), statusCode);
                }
            }
            else if (statusCode >= 300 && statusCode < 400 && TryClaimWindow(ref _redirectNotifyUntilTicks))
            {
                logger.Warn(
                    "[Okuzukey] Komga answered '{0}' with a redirect (HTTP {1}) to '{2}'; redirects are not followed so the API key is never sent elsewhere. Enter the final address as the Komga server URL.",
                    RedactUrl(url),
                    statusCode,
                    RedactUrl(response.Headers.Location?.ToString()));
            }
            else
            {
                logger.Debug("[Okuzukey] Komga request to '{0}' failed with HTTP {1}.", RedactUrl(url), statusCode);
            }
        }
    }

    /// <summary>
    /// Result of <see cref="KomgaHttpClient.GetStreamAsync"/>: the downloaded content plus its
    /// reported content type (used as-is for <c>HttpResponseInfo.ContentType</c> when available,
    /// so an image download does not have to guess/hardcode a format).
    /// </summary>
    internal sealed class KomgaStreamResult
    {
        public Stream Stream { get; set; }

        public string ContentType { get; set; }
    }
}
