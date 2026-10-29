using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Okuzukey.Configuration;
using Emby.Okuzukey.Helpers;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Emby.Okuzukey.Providers.Remote
{
    /// <summary>
    /// Remote metadata provider that reads book/comic metadata and covers from the user's own,
    /// self-hosted Komga server (<c>https://komga.org</c>) via its REST API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Komga is a server the user controls themselves, not a public database - opt-in and
    /// disabled by default (<see cref="PluginConfiguration.EnableKomga"/>). This implementation is
    /// based on the Komga OpenAPI spec
    /// (<c>https://raw.githubusercontent.com/gotson/komga/refs/heads/master/komga/docs/openapi.json</c>,
    /// version 1.27.1).
    /// </para>
    /// <para>
    /// <b>Matching strategy</b> (in <see cref="ResolveKomgaBookIdAsync"/>), most to least
    /// reliable: (1) an already known Komga book ID in <c>ProviderIds["Komga"]</c> from a
    /// previous run; (2) a cheap, targeted <c>search=</c> query by file name, verified by an
    /// exact match of Komga's <c>BookDto.url</c> (the file's path on the Komga host) against the
    /// Emby item's own path (optionally translated via
    /// <see cref="PluginConfiguration.KomgaLibraryPathOnEmby"/>/<see cref="PluginConfiguration.KomgaLibraryPathOnKomga"/>
    /// when Emby and Komga see the same files under different mount points); (3) the same exact
    /// path match against a full, paged book listing, cached in memory for
    /// <see cref="BookListCacheTtl"/> so a large library scan does not re-fetch the entire Komga
    /// library once per item; (4) only as a last resort, a free-text title search - accepted only
    /// if a result's title matches the item's title exactly (ignoring case/whitespace), since
    /// (unlike (2)/(3)) nothing here confirms it is actually the same file, and the resulting id
    /// is stored as <c>ProviderIds["Komga"]</c> and reused by (1) on every later refresh.
    /// </para>
    /// <para>
    /// If the full listing for (3) could not be fetched completely (Komga unreachable, a page
    /// failed), it is neither cached nor used, and the item is skipped instead of falling back
    /// to (4): otherwise a short Komga outage turned into up to 10 minutes of low-confidence
    /// title matches that were then persisted.
    /// </para>
    /// <para>
    /// Book-level metadata (title/summary/number/releaseDate/authors/tags/isbn) comes from
    /// Komga's <c>BookMetadataDto</c>; genres and publisher only exist on the parent series
    /// (<c>SeriesMetadataDto</c>), fetched as a second call via <c>BookDto.seriesId</c>.
    /// </para>
    /// <para>
    /// Every network/parse error is caught and logged; a single failed Komga call never aborts
    /// the library scan (via <see cref="KomgaHttpClient"/>, which itself never throws except on
    /// cooperative cancellation).
    /// </para>
    /// </remarks>
    public class KomgaProvider : IRemoteMetadataProvider<Book, BookInfo>, IRemoteSearchProvider<BookInfo>, IRemoteImageProvider, IHasOrder
    {
        private const int SearchPageSize = 40;
        private const int FullListPageSize = 500;

        /// <summary>
        /// Komga makes every folder a series, so a folder of loose, unrelated books becomes one
        /// "series" whose genres/tags are the union of all its books (e.g. a folder "Books" with
        /// hundreds of genres, all of which would end up on every single book, plus one publisher
        /// for all). The number of books is no usable signal
        /// (a real series can have 1000 volumes), but a real series has only a handful of distinct
        /// genres/tags however long it is. Series-level fields (genres, tags, publisher, summary
        /// fallback) are therefore only applied from a series with at most this many distinct
        /// genres and tags combined.
        /// </summary>
        internal const int MaxSeriesGenresAndTags = 30;

        /// <summary>Upper limit for the configured per-request timeout.</summary>
        internal const int MaxTimeoutSeconds = 300;

        /// <summary>Purpose string for <see cref="ProxyUrlSigner"/> signatures over a Komga book id.</summary>
        internal const string SignaturePurpose = "komga-image";

        /// <summary>
        /// Komga book ids are short alphanumeric strings (TSIDs); anything else (e.g. <c>..</c>,
        /// which <see cref="Uri.EscapeDataString"/> leaves unescaped and <see cref="Uri"/> then
        /// resolves as a path segment) is rejected before it is put into a Komga URL.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex BookIdPattern =
            new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// How long the full, paged book listing (used for tier-3 path matching, see the class
        /// remarks) is cached in memory before being re-fetched. Bounds staleness (a book added
        /// to Komga after the cache was built will not be found via path matching until the
        /// cache expires) while still protecting a weak Komga host from a full-library re-fetch
        /// on every single item during a large Emby library scan.
        /// </summary>
        private static readonly TimeSpan BookListCacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Known Komga author roles (matching ComicInfo.xml's convention, see
        /// <c>ComicInfoProvider.CreditElementRoles</c>) mapped to a nicer display string for
        /// <see cref="PersonInfo.Role"/>. An unrecognized role falls back to a simple
        /// first-letter capitalization rather than being dropped.
        /// </summary>
        private static readonly Dictionary<string, string> RoleDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "writer", "Writer" },
            { "penciller", "Penciller" },
            { "inker", "Inker" },
            { "colorist", "Colorist" },
            { "letterer", "Letterer" },
            { "coverartist", "Cover Artist" },
            { "editor", "Editor" },
            { "translator", "Translator" }
        };

        // Serializes population of the shared book-list cache so a burst of concurrent
        // GetMetadata calls at the start of a scan triggers at most one full-list fetch, not one
        // per item. Reads after population are lock-free (only an expiry check).
        private static readonly SemaphoreSlim CacheRefreshGate = new SemaphoreSlim(1, 1);
        private static volatile BookListCache _bookListCache;

        /// <summary>How long a failed full-listing fetch is remembered before it is tried again.</summary>
        private static readonly TimeSpan BookListFailureBackoff = TimeSpan.FromSeconds(60);

        /// <summary>Safety cap for the paging loop (500 x 2000 = one million books).</summary>
        private const int MaxFullListPages = 2000;

        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;

        /// <summary>
        /// Initializes a new instance of <see cref="KomgaProvider"/>.
        /// </summary>
        /// <param name="logger">Logger injected by Emby, named for this provider.</param>
        /// <param name="jsonSerializer">JSON serializer injected by Emby for the Komga API
        /// responses (a clean, strongly-typed REST schema - nothing here needs manual raw-text
        /// JSON extraction).</param>
        public KomgaProvider(ILogger logger, IJsonSerializer jsonSerializer)
        {
            _logger = FilteredLogger.Wrap(logger ?? throw new ArgumentNullException(nameof(logger)));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        /// <summary>
        /// Emby only calls this provider when Komga is selected in a library, so reaching a
        /// disabled/unconfigured plugin here is a likely mistake: warn once and tell the
        /// administrators with a dialog (see <see cref="AdminNotifier.KomgaNotEnabled"/>). Silent for the opposite case, which never reaches the provider.
        /// </summary>
        private void ReportKomgaNotEnabled()
        {
            if (AdminNotifier.KomgaNotEnabled())
            {
                _logger.Warn("[Okuzukey] Komga is selected as a provider in a library, but the Komga provider is disabled or not fully configured (URL/API key) in the plugin settings; Komga data is skipped.");
            }
        }

        /// <inheritdoc />
        public string Name => "Komga";

        /// <summary>
        /// After the local providers (order 0-2), so local data always takes precedence, but before
        /// typical public-database providers of other plugins - the user's own server is preferred
        /// over a public database.
        /// </summary>
        public int Order => 15;

        /// <inheritdoc />
        public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Book> { HasMetadata = false, People = new List<PersonInfo>() };

            if (!TryGetSettings(out var baseUrl, out var apiKey, out var timeoutSeconds))
            {
                ReportKomgaNotEnabled();
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var bookId = await ResolveKomgaBookIdAsync(info, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(bookId))
                {
                    _logger.Debug("[Okuzukey] Komga: no matching book found for '{0}'.", info.Path);
                    return result;
                }

                var book = await GetBookByIdAsync(bookId, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (book == null)
                {
                    return result;
                }

                KomgaSeriesDto series = null;
                if (!string.IsNullOrWhiteSpace(book.seriesId))
                {
                    series = await GetSeriesByIdAsync(book.seriesId, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                }

                var seriesGenresAndTags = CountDistinctGenresAndTags(series);
                if (seriesGenresAndTags > MaxSeriesGenresAndTags)
                {
                    _logger.Debug(
                        "[Okuzukey] Komga: series '{0}' ({1} books) has {2} distinct genres/tags - treated as a folder of unrelated books, its genres/tags/publisher/summary are not applied to '{3}'.",
                        series.name,
                        series.booksCount,
                        seriesGenresAndTags,
                        info.Path);
                    series = null;
                }

                MapToBook(book, series, result);
                MetadataSanitizer.Sanitize(result.Item, result.People);
                result.HasMetadata = true;

                _logger.Info("[Okuzukey] Komga metadata successfully read for '{0}' (book id {1}).", info.Path, bookId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly: a single failed/unexpected Komga response must
                // under no circumstances abort the library scan.
                _logger.ErrorException("[Okuzukey] Unexpected error while fetching Komga metadata for '{0}'.", ex, info.Path);
                result.HasMetadata = false;
                result.Item = null;
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();

            if (!TryGetSettings(out var baseUrl, out var apiKey, out var timeoutSeconds))
            {
                ReportKomgaNotEnabled();
                return results;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var query = BuildSearchQuery(searchInfo);
                if (string.IsNullOrWhiteSpace(query))
                {
                    _logger.Debug("[Okuzukey] Komga: no search terms available, skipping search.");
                    return results;
                }

                var books = await SearchBooksAsync(query, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                foreach (var book in books)
                {
                    var proxyImageUrl = await BuildProxyImageUrlAsync(book.id, cancellationToken).ConfigureAwait(false);
                    var searchResult = MapToSearchResult(book, proxyImageUrl);
                    if (searchResult != null)
                    {
                        results.Add(searchResult);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Okuzukey] Unexpected error during Komga search.", ex);
            }

            return results;
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Book;
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary };
        }

        /// <summary>
        /// Delivers the Komga cover for a book that already carries a matching provider ID (set
        /// during the metadata step). Items without one are skipped, to avoid a free-text search
        /// that could return a different book's cover than the one the metadata came from.
        /// </summary>
        /// <remarks>
        /// The URL handed to Emby here points at this plugin's own image-proxy endpoint
        /// (<c>Api/KomgaImageApi.cs</c>), **not** directly at Komga: during a normal library scan,
        /// Emby downloads a <see cref="RemoteImageInfo.Url"/> itself via its own generic,
        /// unauthenticated HTTP client (internally, <c>ConvertImageToLocal</c>) - it does
        /// <b>not</b> route the request back through <see cref="GetImageResponse"/> for this. A
        /// direct Komga thumbnail URL would therefore always come back HTTP 401 (missing
        /// <c>X-API-Key</c>), which <see cref="GetImageResponse"/> alone could never fix, since
        /// Emby's own downloader was the one making the request, not this provider. Routing
        /// through the plugin's own endpoint instead means Emby fetches an unauthenticated URL
        /// on itself, and the actual authenticated Komga request happens server-side inside the
        /// plugin, exactly like the metadata requests already do - no credential is ever sent
        /// toward Emby's frontend/downloader.
        /// </remarks>
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();

            if (!TryGetSettings(out _, out _, out _))
            {
                ReportKomgaNotEnabled();
            }
            else
            {
                var komgaId = item?.GetProviderId("Komga");
                if (IsValidBookId(komgaId))
                {
                    var proxyUrl = await BuildProxyImageUrlAsync(komgaId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(proxyUrl))
                    {
                        images.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Url = proxyUrl,
                            Type = ImageType.Primary
                        });
                    }
                }
            }

            return images;
        }

        /// <summary>
        /// Builds this plugin's own image-proxy URL (<c>Api/KomgaImageApi.cs</c>) for a given
        /// Komga book id plus a <see cref="ProxyUrlSigner"/> signature over it, on Emby's own
        /// loopback API URL (<see cref="ProxyUrlSigner.GetLocalApiBaseUrlAsync"/>, since Emby's
        /// downloader runs in the same process). Returns <c>null</c> for an invalid id or if
        /// <see cref="Plugin.Instance"/>/<see cref="Plugin.ApplicationHost"/> is unexpectedly
        /// unavailable (never during normal operation) rather than throwing.
        /// </summary>
        internal static async Task<string> BuildProxyImageUrlAsync(string komgaId, CancellationToken cancellationToken)
        {
            if (!IsValidBookId(komgaId))
            {
                return null;
            }

            var localApiUrl = await ProxyUrlSigner.GetLocalApiBaseUrlAsync(cancellationToken).ConfigureAwait(false);
            if (localApiUrl == null)
            {
                return null;
            }

            var signature = ProxyUrlSigner.Sign(SignaturePurpose, komgaId);
            return $"{localApiUrl}{ProxyRoutePrefix}{Uri.EscapeDataString(komgaId)}?sig={Uri.EscapeDataString(signature)}";
        }

        private const string ProxyRoutePrefix = "/Okuzukey/KomgaImage/";

        /// <summary><c>true</c> if <paramref name="bookId"/> has the shape of a Komga book id.</summary>
        internal static bool IsValidBookId(string bookId)
        {
            return !string.IsNullOrEmpty(bookId) && BookIdPattern.IsMatch(bookId);
        }

        /// <summary>
        /// Kept as a spec-compliant <see cref="IRemoteImageProvider"/> implementation and as a
        /// fallback (e.g. if a future Emby version, or a different code path such as the manual
        /// Identify dialog, calls back into this method) - but per the <see cref="GetImages"/>
        /// remarks, the normal per-scan cover download does not rely on this being invoked.
        /// </summary>
        /// <remarks>
        /// Only two kinds of URL are served, so the Komga API key is never sent anywhere else
        ///: this plugin's own signed proxy URL (resolved directly, without an HTTP
        /// round trip through Emby) and a URL on the configured Komga server itself.
        /// </remarks>
        public async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url) || !TryGetSettings(out var baseUrl, out var apiKey, out var timeoutSeconds))
            {
                return null;
            }

            try
            {
                KomgaStreamResult streamResult;
                var proxyIndex = url.IndexOf(ProxyRoutePrefix, StringComparison.OrdinalIgnoreCase);
                if (proxyIndex >= 0)
                {
                    var idPart = url.Substring(proxyIndex + ProxyRoutePrefix.Length);
                    var queryIndex = idPart.IndexOf('?');
                    var bookId = Uri.UnescapeDataString(queryIndex >= 0 ? idPart.Substring(0, queryIndex) : idPart);
                    if (!IsValidBookId(bookId) || !ProxyUrlSigner.IsAuthorizedKomgaBook(bookId, ProxyUrlSigner.GetQueryValue(url, "sig")))
                    {
                        return null;
                    }

                    streamResult = await FetchThumbnailAsync(bookId, _logger, cancellationToken).ConfigureAwait(false);
                }
                else if (url.StartsWith(baseUrl + "/", StringComparison.OrdinalIgnoreCase))
                {
                    streamResult = await KomgaHttpClient.GetStreamAsync(url, apiKey, timeoutSeconds, _logger, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.Debug("[Okuzukey] Komga: refusing to download '{0}', it is neither this plugin's proxy URL nor on the configured Komga server.", KomgaHttpClient.RedactUrl(url));
                    return null;
                }

                var contentType = ImageSniffer.DetectContentType(streamResult?.Stream);
                if (contentType == null)
                {
                    streamResult?.Stream?.Dispose();
                    return null;
                }

                return new HttpResponseInfo
                {
                    Content = streamResult.Stream,
                    ContentType = contentType,
                    StatusCode = HttpStatusCode.OK
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Okuzukey] Unexpected error downloading a Komga cover from '{0}'.", ex, KomgaHttpClient.RedactUrl(url));
                return null;
            }
        }

        /// <summary>
        /// Fetches a Komga book's thumbnail bytes directly (authenticated, server-side). Used by
        /// <c>Api/KomgaImageApi.cs</c> - the actual authenticated Komga request for the per-scan
        /// cover download now happens here, not in <see cref="GetImageResponse"/>, see the
        /// <see cref="GetImages"/> remarks. Returns <c>null</c> if Komga is disabled/unconfigured
        /// or the request fails for any reason; never throws except on cooperative cancellation.
        /// </summary>
        internal static async Task<KomgaStreamResult> FetchThumbnailAsync(string komgaId, ILogger logger, CancellationToken cancellationToken)
        {
            if (!IsValidBookId(komgaId) || !TryGetSettings(out var baseUrl, out var apiKey, out var timeoutSeconds))
            {
                return null;
            }

            var url = BuildBookThumbnailUrl(baseUrl, komgaId);
            return await KomgaHttpClient.GetStreamAsync(url, apiKey, timeoutSeconds, logger, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads and validates the Komga settings from the plugin configuration. Returns
        /// <c>false</c> (only logged at debug level, since this is a normal configuration state)
        /// if Komga is disabled or the URL/API key are not both set.
        /// </summary>
        internal static bool TryGetSettings(out string baseUrl, out string apiKey, out int timeoutSeconds)
        {
            baseUrl = null;
            apiKey = null;
            timeoutSeconds = 30;

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableKomga)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.KomgaBaseUrl) || string.IsNullOrWhiteSpace(config.KomgaApiKey))
            {
                return false;
            }

            baseUrl = NormalizeBaseUrl(config.KomgaBaseUrl);
            apiKey = config.KomgaApiKey.Trim();
            timeoutSeconds = config.KomgaTimeoutSeconds > 0 ? Math.Min(config.KomgaTimeoutSeconds, MaxTimeoutSeconds) : 30;
            return true;
        }

        /// <summary>
        /// Removes a trailing slash and, if present, a trailing <c>/api</c> the user may have
        /// pasted in by mistake (the endpoints built elsewhere already include <c>/api/v1/...</c>).
        /// </summary>
        internal static string NormalizeBaseUrl(string url)
        {
            var trimmed = url.Trim().TrimEnd('/');

            if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - "/api".Length);
            }

            return trimmed;
        }

        /// <summary>
        /// Resolves the Komga book ID for <paramref name="info"/> using the four-tier strategy
        /// described in the class remarks.
        /// </summary>
        private async Task<string> ResolveKomgaBookIdAsync(BookInfo info, string baseUrl, string apiKey, int timeoutSeconds, CancellationToken cancellationToken)
        {
            // 1. Identifier-first: an already known Komga book ID from a previous run.
            var knownId = info.GetProviderId("Komga");
            if (IsValidBookId(knownId))
            {
                return knownId;
            }

            var expectedKomgaPath = ToKomgaPath(info.Path);
            if (string.IsNullOrEmpty(expectedKomgaPath))
            {
                return await SearchByTitleAsync(info, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            }

            var normalizedExpected = NormalizePathForComparison(expectedKomgaPath);

            // 2. Cheap, targeted pre-filter search by file name, verified by exact path match.
            var fileNameGuess = SafeGetFileNameWithoutExtension(info.Path);
            if (!string.IsNullOrWhiteSpace(fileNameGuess))
            {
                var candidates = await SearchBooksAsync(fileNameGuess, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                var match = candidates.FirstOrDefault(b => NormalizePathForComparison(b.url) == normalizedExpected);
                if (match != null)
                {
                    _logger.Debug("[Okuzukey] Komga: matched '{0}' by path via targeted search (book id {1}).", info.Path, match.id);
                    return match.id;
                }
            }

            // 3. Full, cached book listing, matched by exact path.
            var allBooks = await GetOrFetchFullBookListAsync(baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (allBooks == null)
            {
                // Listing incomplete (Komga unreachable or a page failed): a missing path match
                // proves nothing, so do not fall back to a title guess that would be persisted.
                _logger.Debug("[Okuzukey] Komga: book listing unavailable, skipping '{0}' for now.", info.Path);
                return null;
            }

            var pathMatch = allBooks.FirstOrDefault(b => NormalizePathForComparison(b.url) == normalizedExpected);
            if (pathMatch != null)
            {
                _logger.Debug("[Okuzukey] Komga: matched '{0}' by path via full listing (book id {1}).", info.Path, pathMatch.id);
                return pathMatch.id;
            }

            // 4. Last resort: free-text title search, lowest confidence (no path confirmation).
            _logger.Debug("[Okuzukey] Komga: no path match for '{0}', falling back to free-text title search.", info.Path);
            return await SearchByTitleAsync(info, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> SearchByTitleAsync(BookInfo info, string baseUrl, string apiKey, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var query = BuildSearchQuery(info);
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var results = await SearchBooksAsync(query, baseUrl, apiKey, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            // Only an unambiguous exact title match is accepted: the id is persisted and reused
            // on every later refresh, so a merely similar first search result would stick.
            var normalizedQuery = NormalizeTitleForComparison(query);
            var exactMatches = results
                .Where(b => IsValidBookId(b.id)
                            && (NormalizeTitleForComparison(b.metadata?.title) == normalizedQuery
                                || NormalizeTitleForComparison(b.name) == normalizedQuery))
                .ToList();

            if (exactMatches.Count != 1)
            {
                _logger.Debug(
                    "[Okuzukey] Komga: title search for '{0}' gave {1} exact title matches (of {2} results); not matched, since no path confirms it.",
                    query,
                    exactMatches.Count,
                    results.Count);
                return null;
            }

            var best = exactMatches[0];
            _logger.Debug(
                "[Okuzukey] Komga: matched '{0}' by exact title -> book id {1} (no path was confirmed).",
                query,
                best.id);
            return best.id;
        }

        private static string NormalizeTitleForComparison(string title)
        {
            var stripped = TitleCleaner.StripNonFilingMarkers(title);
            return string.IsNullOrWhiteSpace(stripped)
                ? string.Empty
                : string.Join(" ", stripped.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        }

        private static string BuildSearchQuery(BookInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.Name))
            {
                return info.Name;
            }

            return SafeGetFileNameWithoutExtension(info.Path);
        }

        private async Task<List<KomgaBookDto>> SearchBooksAsync(string searchText, string baseUrl, string apiKey, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var url = $"{baseUrl}/api/v1/books?search={Uri.EscapeDataString(searchText)}&size={SearchPageSize}";
            var json = await KomgaHttpClient.GetStringAsync(url, apiKey, timeoutSeconds, _logger, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<KomgaBookDto>();
            }

            var page = _jsonSerializer.DeserializeFromString<KomgaPageBookDto>(json);
            return page?.content ?? new List<KomgaBookDto>();
        }

        private async Task<KomgaBookDto> GetBookByIdAsync(string bookId, string baseUrl, string apiKey, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var url = $"{baseUrl}/api/v1/books/{Uri.EscapeDataString(bookId)}";
            var json = await KomgaHttpClient.GetStringAsync(url, apiKey, timeoutSeconds, _logger, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? null : _jsonSerializer.DeserializeFromString<KomgaBookDto>(json);
        }

        private async Task<KomgaSeriesDto> GetSeriesByIdAsync(string seriesId, string baseUrl, string apiKey, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var url = $"{baseUrl}/api/v1/series/{Uri.EscapeDataString(seriesId)}";
            var json = await KomgaHttpClient.GetStringAsync(url, apiKey, timeoutSeconds, _logger, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? null : _jsonSerializer.DeserializeFromString<KomgaSeriesDto>(json);
        }

        /// <summary>
        /// Returns the full, paged Komga book listing, served from an in-memory cache
        /// (<see cref="BookListCacheTtl"/>) shared by all items in the same and nearby scans. A
        /// <see cref="SemaphoreSlim"/> ensures a burst of concurrent callers at the start of a
        /// scan triggers at most one full re-fetch, not one per item.
        /// </summary>
        /// <returns>
        /// The complete listing, or <c>null</c> if it could not be fetched completely. An
        /// incomplete listing is never cached (it would make every book on a missing page look
        /// "not in Komga" for the whole TTL); instead the failure itself is remembered for
        /// <see cref="BookListFailureBackoff"/>, so each item of a running scan does not re-fetch
        /// the pages that did work.
        /// </returns>
        private async Task<List<KomgaBookDto>> GetOrFetchFullBookListAsync(string baseUrl, string apiKey, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var cached = _bookListCache;
            if (cached != null && cached.IsValidFor(baseUrl))
            {
                return cached.Books;
            }

            await CacheRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = _bookListCache;
                if (cached != null && cached.IsValidFor(baseUrl))
                {
                    return cached.Books;
                }

                var allBooks = new List<KomgaBookDto>();
                var complete = false;

                for (var pageIndex = 0; pageIndex < MaxFullListPages; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var url = $"{baseUrl}/api/v1/books?size={FullListPageSize}&page={pageIndex}";
                    var json = await KomgaHttpClient.GetStringAsync(url, apiKey, timeoutSeconds, _logger, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        // Request failed: the listing is incomplete.
                        break;
                    }

                    var page = _jsonSerializer.DeserializeFromString<KomgaPageBookDto>(json);
                    if (page == null)
                    {
                        break;
                    }

                    if (page.content == null || page.content.Count == 0)
                    {
                        // Empty page (empty library, or past the end): nothing is missing.
                        complete = true;
                        break;
                    }

                    allBooks.AddRange(page.content);

                    if (page.last)
                    {
                        complete = true;
                        break;
                    }
                }

                if (!complete)
                {
                    _bookListCache = new BookListCache(null, baseUrl, DateTime.UtcNow.Add(BookListFailureBackoff));
                    _logger.Warn(
                        "[Okuzukey] Komga: the book listing could not be fetched completely ({0} books received); path matching is paused for {1} seconds.",
                        allBooks.Count,
                        BookListFailureBackoff.TotalSeconds);
                    return null;
                }

                _bookListCache = new BookListCache(allBooks, baseUrl, DateTime.UtcNow.Add(BookListCacheTtl));

                _logger.Debug(
                    "[Okuzukey] Komga: cached {0} books for path-based matching (TTL {1} minutes).",
                    allBooks.Count,
                    BookListCacheTtl.TotalMinutes);

                return allBooks;
            }
            finally
            {
                CacheRefreshGate.Release();
            }
        }

        /// <summary>
        /// Drops the cached book listing. Called when the plugin settings are saved, since a new
        /// URL, API key (possibly of a Komga user with access to other libraries) or path mapping
        /// makes it stale.
        /// </summary>
        internal static void ClearBookListCache()
        {
            _bookListCache = null;
        }

        /// <summary>
        /// One immutable snapshot of the listing cache, replaced as a whole, so concurrent readers
        /// never see the list of one fetch combined with the URL/expiry of another.
        /// <see cref="Books"/> is <c>null</c> for a remembered failure.
        /// </summary>
        private sealed class BookListCache
        {
            public BookListCache(List<KomgaBookDto> books, string baseUrl, DateTime expiresAtUtc)
            {
                Books = books;
                BaseUrl = baseUrl;
                ExpiresAtUtc = expiresAtUtc;
            }

            public List<KomgaBookDto> Books { get; }

            public string BaseUrl { get; }

            public DateTime ExpiresAtUtc { get; }

            public bool IsValidFor(string baseUrl)
            {
                return string.Equals(BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < ExpiresAtUtc;
            }
        }

        /// <summary>
        /// Translates an Emby item's file path into the path Komga would report for the same
        /// file (<c>BookDto.url</c>), via the optional
        /// <see cref="PluginConfiguration.KomgaLibraryPathOnEmby"/>/
        /// <see cref="PluginConfiguration.KomgaLibraryPathOnKomga"/> prefix substitution. Returns
        /// the path unchanged if either field is not configured (Emby and Komga are assumed to
        /// already see identical paths).
        /// </summary>
        private static string ToKomgaPath(string embyPath)
        {
            if (string.IsNullOrEmpty(embyPath))
            {
                return embyPath;
            }

            var config = Plugin.Instance?.Configuration;
            var embyPrefix = config?.KomgaLibraryPathOnEmby;
            var komgaPrefix = config?.KomgaLibraryPathOnKomga;

            if (string.IsNullOrWhiteSpace(embyPrefix) || string.IsNullOrWhiteSpace(komgaPrefix))
            {
                return embyPath;
            }

            var trimmedEmbyPrefix = embyPrefix.Trim();
            if (!embyPath.StartsWith(trimmedEmbyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return embyPath;
            }

            var remainder = embyPath.Substring(trimmedEmbyPrefix.Length).TrimStart('/', '\\').Replace('\\', '/');
            return komgaPrefix.Trim().TrimEnd('/', '\\') + "/" + remainder;
        }

        /// <summary>
        /// Normalizes a file path for comparison: unifies path separators to <c>/</c>, trims a
        /// trailing separator, and upper-invariant-cases the result (Komga paths are Linux-style
        /// case-sensitive in practice, but the Emby side may come from a case-insensitive Windows
        /// filesystem - comparing case-insensitively avoids false negatives at the cost of a
        /// theoretical false positive between two paths that differ only in case, which is not a
        /// realistic scenario for real library layouts).
        /// </summary>
        private static string NormalizePathForComparison(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        }

        private static string SafeGetFileNameWithoutExtension(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                return Path.GetFileNameWithoutExtension(path);
            }
            catch (ArgumentException)
            {
                // Path contains characters invalid for Path.GetFileNameWithoutExtension on this
                // OS (rare, e.g. a raw Linux-style path with unusual characters inspected on a
                // Windows Emby host) - fall back to no filename-based pre-filter instead of
                // failing the whole lookup.
                return null;
            }
        }

        private static string BuildBookThumbnailUrl(string baseUrl, string bookId)
        {
            return $"{baseUrl}/api/v1/books/{Uri.EscapeDataString(bookId)}/thumbnail";
        }

        /// <summary>
        /// Maps a Komga book (plus its parent series, for the fields only the series carries) to
        /// a <see cref="Book"/> and its <see cref="PersonInfo"/> list.
        /// </summary>
        private static int CountDistinctGenresAndTags(KomgaSeriesDto series)
        {
            var metadata = series?.metadata;
            if (metadata == null)
            {
                return 0;
            }

            return (metadata.genres ?? new List<string>())
                .Concat(metadata.tags ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static void MapToBook(KomgaBookDto book, KomgaSeriesDto series, MetadataResult<Book> result)
        {
            var bookItem = new Book();

            var title = TitleCleaner.StripNonFilingMarkers(book.metadata?.title);
            bookItem.Name = !string.IsNullOrWhiteSpace(title) ? title : book.name;

            var summary = book.metadata?.summary;
            bookItem.Overview = OverviewCleaner.ToPlainText(!string.IsNullOrWhiteSpace(summary) ? summary : series?.metadata?.summary);

            // numberSort is Komga's own canonical, always-populated sortable number (the raw
            // "number" string field may be non-numeric, e.g. "Annual"); IndexNumber is an int, so
            // it is only set for whole/near-whole numbers - fine for issue numbers, and consistent
            // with the rounding ComicInfoProvider already does for its own Number field.
            if (book.metadata != null && book.metadata.numberSort > 0 && SafeNumbers.TryToIndexNumber(book.metadata.numberSort, out var indexNumber))
            {
                bookItem.IndexNumber = indexNumber;
            }

            var releaseDateText = book.metadata?.releaseDate;
            if (!string.IsNullOrWhiteSpace(releaseDateText)
                && DateTime.TryParse(
                    releaseDateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var releaseDate))
            {
                bookItem.PremiereDate = releaseDate;
                bookItem.ProductionYear = releaseDate.Year;
            }

            // Genres only exist on the series (Komga's BookMetadataDto has no genre field).
            if (series?.metadata?.genres != null)
            {
                foreach (var genre in series.metadata.genres)
                {
                    if (!string.IsNullOrWhiteSpace(genre) && !bookItem.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    {
                        bookItem.AddGenre(genre);
                    }
                }
            }

            // Tags can exist on both the book and the series; merge and de-duplicate.
            var tags = new List<string>();
            if (book.metadata?.tags != null)
            {
                tags.AddRange(book.metadata.tags);
            }

            if (series?.metadata?.tags != null)
            {
                tags.AddRange(series.metadata.tags);
            }

            foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!bookItem.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    bookItem.SetTags(bookItem.Tags.Concat(new[] { tag }));
                }
            }

            // Publisher only exists on the series.
            var publisher = series?.metadata?.publisher;
            if (!string.IsNullOrWhiteSpace(publisher))
            {
                bookItem.AddStudio(publisher);
            }

            if (book.metadata?.authors != null)
            {
                foreach (var author in book.metadata.authors)
                {
                    if (string.IsNullOrWhiteSpace(author?.name))
                    {
                        continue;
                    }

                    result.People.Add(new PersonInfo
                    {
                        Name = author.name,
                        Type = PersonType.Writer,
                        Role = FormatRole(author.role)
                    });
                }
            }

            var isbn = book.metadata?.isbn;
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                bookItem.SetProviderId("ISBN", isbn.Replace("-", string.Empty).Trim());
            }

            bookItem.SetProviderId("Komga", book.id);

            result.Item = bookItem;
        }

        private static string FormatRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return "Author";
            }

            var trimmed = role.Trim();
            if (RoleDisplayNames.TryGetValue(trimmed, out var display))
            {
                return display;
            }

            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
        }

        /// <summary>
        /// Translates a Komga book into a <see cref="RemoteSearchResult"/> for the manual search
        /// in the Emby dashboard. <see cref="RemoteSearchResult.ImageUrl"/> is set to this
        /// plugin's own image-proxy URL (see the <see cref="GetImages"/> remarks) rather than a
        /// direct, authenticated Komga URL, so the Identify dialog's cover preview - fetched by
        /// Emby the same unauthenticated way as the per-scan cover - also renders correctly.
        /// </summary>
        private static RemoteSearchResult MapToSearchResult(KomgaBookDto book, string proxyImageUrl)
        {
            var title = book.metadata?.title;
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(book.name))
            {
                return null;
            }

            var searchResult = new RemoteSearchResult
            {
                Name = OverviewCleaner.ToPlainTextLine(!string.IsNullOrWhiteSpace(title) ? title : book.name),
                Overview = OverviewCleaner.ToPlainText(book.metadata?.summary),
                SearchProviderName = "Komga",
                ImageUrl = proxyImageUrl
            };

            var releaseDateText = book.metadata?.releaseDate;
            if (!string.IsNullOrWhiteSpace(releaseDateText)
                && DateTime.TryParse(
                    releaseDateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var releaseDate))
            {
                searchResult.PremiereDate = releaseDate;
                searchResult.ProductionYear = releaseDate.Year;
            }

            searchResult.SetProviderId("Komga", book.id);
            return searchResult;
        }

        // -- DTOs -------------------------------------------------------------------------
        // Property names intentionally match the Komga JSON response field names exactly
        // (lowercase-initial camelCase), so deserialization works regardless of the serializer's
        // internal case handling. Based on the Komga OpenAPI spec v1.27.1 (see the
        // class remarks); only the fields this provider actually uses are declared.

        private sealed class KomgaPageBookDto
        {
            public List<KomgaBookDto> content { get; set; }

            public bool last { get; set; }
        }

        private sealed class KomgaBookDto
        {
            public string id { get; set; }

            public string seriesId { get; set; }

            public string name { get; set; }

            public string url { get; set; }

            public KomgaBookMetadataDto metadata { get; set; }
        }

        private sealed class KomgaBookMetadataDto
        {
            public string title { get; set; }

            public string summary { get; set; }

            public double numberSort { get; set; }

            public string releaseDate { get; set; }

            public string isbn { get; set; }

            public List<KomgaAuthorDto> authors { get; set; }

            public List<string> tags { get; set; }
        }

        private sealed class KomgaAuthorDto
        {
            public string name { get; set; }

            public string role { get; set; }
        }

        private sealed class KomgaSeriesDto
        {
            public string id { get; set; }

            public string name { get; set; }

            public int booksCount { get; set; }

            public KomgaSeriesMetadataDto metadata { get; set; }
        }

        private sealed class KomgaSeriesMetadataDto
        {
            public string summary { get; set; }

            public string publisher { get; set; }

            public List<string> genres { get; set; }

            public List<string> tags { get; set; }
        }
    }
}
