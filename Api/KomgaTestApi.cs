using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Okuzukey.Helpers;
using Emby.Okuzukey.Providers.Remote;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Okuzukey.Api
{
    /// <summary>Request DTO for the dashboard's "Test connection" button, see <see cref="KomgaTestApi"/>.</summary>
    [Authenticated(Roles = "Admin")]
    [Route("/Okuzukey/KomgaTest", "POST", Summary = "Tests a Komga URL/API key entered on the plugin settings page")]
    public class PostKomgaTest : IReturn<KomgaTestResult>
    {
        public string BaseUrl { get; set; }

        public string ApiKey { get; set; }

        public int TimeoutSeconds { get; set; }
    }

    /// <summary>Outcome shown next to the "Test connection" button.</summary>
    public class KomgaTestResult
    {
        public bool Success { get; set; }

        public string Message { get; set; }
    }

    /// <summary>
    /// Emby-hosted endpoint behind the plugin settings page's "Test connection" button. Tests the
    /// values currently typed into the form (not the saved configuration), so a URL/API key can be
    /// verified <em>before</em> saving. Admin-only. Deliberately independent of
    /// <c>KomgaHttpClient</c> (no circuit breaker, no concurrency gate, no admin dialog): a manual
    /// test must always really try, and must not trip or reset the scan-time protections.
    /// </summary>
    public class KomgaTestApi : IService, IRequiresRequest
    {
        /// <summary>Redirects are not followed, same as <c>KomgaHttpClient</c>: the API key must not travel to another host.</summary>
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        public IRequest Request { get; set; }

        public async Task<object> Post(PostKomgaTest request)
        {
            var baseUrl = KomgaProvider.NormalizeBaseUrl(request?.BaseUrl ?? string.Empty);
            var apiKey = (request?.ApiKey ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Fail("Please enter the Komga server URL first.");
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Fail("The URL must start with http:// or https://, e.g. http://192.168.1.10:25600");
            }

            if (apiKey.Length == 0)
            {
                return Fail("Please enter the Komga API key first.");
            }

            var timeoutSeconds = request.TimeoutSeconds > 0 ? Math.Min(request.TimeoutSeconds, 30) : 10;

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, Request?.CancellationToken ?? CancellationToken.None))
                {
                    // "users/me" moved to /api/v2 in current Komga (v1 answers 404 there, while books/libraries
                    // still live under /api/v1); older versions only have v1, hence the fallback.
                    var (status, body) = await GetAsync(baseUrl + "/api/v2/users/me", apiKey, linkedCts.Token).ConfigureAwait(false);
                    if (status == 404)
                    {
                        (status, body) = await GetAsync(baseUrl + "/api/v1/users/me", apiKey, linkedCts.Token).ConfigureAwait(false);
                    }

                    if (status >= 300 && status < 400)
                    {
                        return Fail("The server answered with a redirect (HTTP " + status + ")"
                                    + (string.IsNullOrWhiteSpace(body) ? string.Empty : " to " + body)
                                    + ". Redirects are not followed, so the API key is never sent to another address - enter the final address as the Komga server URL.");
                    }

                    if (status == 401 || status == 403)
                    {
                        return Fail("The server is reachable, but the API key was rejected (HTTP " + status + "). Create a key in the Komga web UI under My Account > API Keys.");
                    }

                    if (status == 404)
                    {
                        return Fail("The server answered, but Komga's API was not found (HTTP 404). Check the URL: host, port, and any path prefix if Komga runs behind a reverse proxy.");
                    }

                    if (status < 200 || status >= 300)
                    {
                        return Fail("The server answered with HTTP " + status + ", not the expected Komga response.");
                    }

                    if (body.IndexOf("\"roles\"", StringComparison.Ordinal) < 0)
                    {
                        return Fail("A server answered, but the response does not look like Komga. Check the URL and port.");
                    }

                    var email = Regex.Match(body, @"""email""\s*:\s*""([^""]*)""");
                    var message = "Connection OK - signed in to Komga" + (email.Success ? " as " + email.Groups[1].Value : string.Empty);

                    var (booksStatus, booksBody) = await GetAsync(baseUrl + "/api/v1/books?size=1", apiKey, linkedCts.Token).ConfigureAwait(false);
                    var total = Regex.Match(booksBody ?? string.Empty, @"""totalElements""\s*:\s*(\d+)");
                    if (booksStatus >= 200 && booksStatus < 300 && total.Success)
                    {
                        message += ", " + total.Groups[1].Value + " books found.";
                    }
                    else
                    {
                        message += ".";
                    }

                    return new KomgaTestResult { Success = true, Message = message };
                }
            }
            catch (OperationCanceledException)
            {
                return Fail("No answer within " + timeoutSeconds + " seconds - the server is not reachable. Check host, port, that Komga is running, and any firewall.");
            }
            catch (HttpRequestException ex)
            {
                return Fail("Could not connect: " + ExceptionText.Describe(ex) + ".");
            }
            catch (Exception ex)
            {
                return Fail("Unexpected error: " + ExceptionText.Describe(ex));
            }
        }

        /// <summary>
        /// Returns the status code and the body (read up to 1 MB - "users/me" and a one-book
        /// listing are tiny); for a redirect, the <c>Location</c> header instead of the body.
        /// </summary>
        private static async Task<(int Status, string Body)> GetAsync(string url, string apiKey, CancellationToken cancellationToken)
        {
            using (var message = new HttpRequestMessage(HttpMethod.Get, url))
            {
                message.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
                using (var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    var status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        return (status, KomgaHttpClient.RedactUrl(response.Headers.Location?.ToString()));
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var memoryStream = await BoundedReader.ReadToMemoryAsync(stream, 1024 * 1024, cancellationToken).ConfigureAwait(false))
                    using (var reader = new System.IO.StreamReader(memoryStream))
                    {
                        return (status, reader.ReadToEnd());
                    }
                }
            }
        }

        private static KomgaTestResult Fail(string message)
        {
            return new KomgaTestResult { Success = false, Message = message };
        }
    }
}
