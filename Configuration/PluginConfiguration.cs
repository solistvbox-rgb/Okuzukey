using MediaBrowser.Model.Plugins;

namespace Emby.Okuzukey.Configuration
{
    /// <summary>
    /// Persisted configuration of the Okuzukey plugin.
    /// </summary>
    /// <remarks>
    /// The local providers (NFO, Calibre OPF, embedded ComicInfo.xml, local cover images) are
    /// always active and need no configuration. The only configurable source is Komga
    /// (<c>Providers/Remote/KomgaProvider.cs</c>): a request to the user's own, self-hosted Komga
    /// server, opt-in and disabled by default.
    /// </remarks>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of <see cref="PluginConfiguration"/> with production-ready
        /// defaults: Komga is disabled until the user explicitly enables it and enters a server
        /// URL and API key.
        /// </summary>
        public PluginConfiguration()
        {
            EnableKomga = false;
            KomgaBaseUrl = string.Empty;
            KomgaApiKey = string.Empty;
            KomgaTimeoutSeconds = 30;
            KomgaLibraryPathOnEmby = string.Empty;
            KomgaLibraryPathOnKomga = string.Empty;
            KomgaMaxConcurrentRequests = Helpers.KomgaHttpClient.DefaultConcurrentRequests;
            LoggingLevel = "Normal";
        }

        /// <summary>Enables or disables the Komga remote provider globally.</summary>
        public bool EnableKomga { get; set; }

        /// <summary>
        /// Base URL of the user's Komga server, e.g. <c>https://komga.example.com:25600</c> or
        /// <c>http://192.168.1.10:25600</c> (both HTTP and HTTPS are allowed, since Komga is
        /// commonly self-hosted on a local network without a TLS certificate). Entered without a
        /// trailing slash or an <c>/api</c> suffix; both are normalized away if present.
        /// </summary>
        public string KomgaBaseUrl { get; set; }

        /// <summary>
        /// Komga API key (generated in the Komga web UI under user settings), sent as the
        /// <c>X-API-Key</c> header on every request.
        /// </summary>
        public string KomgaApiKey { get; set; }

        /// <summary>
        /// Timeout in seconds for a single request to the Komga server (headers and body). Values
        /// less than or equal to zero fall back to the default of 30 seconds; values above
        /// <c>KomgaProvider.MaxTimeoutSeconds</c> (300) are capped.
        /// </summary>
        public int KomgaTimeoutSeconds { get; set; }

        /// <summary>
        /// Optional path prefix as Emby sees the book/comic library on disk, e.g.
        /// <c>Z:\Books\</c> or <c>/data/books/</c>. Paired with <see cref="KomgaLibraryPathOnKomga"/>
        /// to translate an Emby item's file path into the path Komga would report for the same
        /// file (<c>BookDto.url</c>), when Emby and Komga see the same files under different
        /// mount points/drive letters. Leave both empty if Emby and Komga already see identical
        /// paths (e.g. both in the same Docker Compose stack with the same bind mount).
        /// </summary>
        public string KomgaLibraryPathOnEmby { get; set; }

        /// <summary>
        /// The same location as <see cref="KomgaLibraryPathOnEmby"/>, but as Komga sees it. See
        /// <see cref="KomgaLibraryPathOnEmby"/> for details.
        /// </summary>
        public string KomgaLibraryPathOnKomga { get; set; }

        /// <summary>
        /// Maximum number of Komga requests <see cref="Helpers.KomgaHttpClient"/> allows in
        /// flight at once, across all callers, via a shared <c>SemaphoreSlim</c>. Clamped to
        /// <see cref="Helpers.KomgaHttpClient.MinConcurrentRequests"/>..
        /// <see cref="Helpers.KomgaHttpClient.MaxAllowedConcurrentRequests"/>; a value outside
        /// that range (including 0 or a stray huge number from manual XML editing) falls back to
        /// <see cref="Helpers.KomgaHttpClient.DefaultConcurrentRequests"/>. Lower values are
        /// gentler on weak Komga hardware (e.g. a Raspberry Pi) at the cost of a slower library
        /// scan; higher values speed up the scan on capable hardware.
        /// </summary>
        public int KomgaMaxConcurrentRequests { get; set; }

        /// <summary>
        /// Controls how much this plugin writes to Emby's own server log
        /// (<c>embyserver.txt</c>), via <see cref="Helpers.FilteredLogger"/>. One of
        /// <c>"None"</c>, <c>"ErrorsOnly"</c>, <c>"Normal"</c> (default) or <c>"Verbose"</c>; an
        /// unrecognized value is treated as <c>"Normal"</c>. On a large library the per-item
        /// Debug/Info lines alone can add several MB to the log.
        /// </summary>
        public string LoggingLevel { get; set; }
    }
}
