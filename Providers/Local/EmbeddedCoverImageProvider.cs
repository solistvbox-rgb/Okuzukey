using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Okuzukey.Helpers;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Okuzukey.Providers.Local
{
    /// <summary>
    /// Extracts the cover image embedded directly inside a <c>.cbz</c>/<c>.cbr</c> comic archive
    /// or a <c>.epub</c> e-book, for items that have neither a sidecar cover file
    /// (<see cref="LocalImageBookProvider"/>) nor - if enabled - a Komga-provided cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented as an <see cref="IRemoteImageProvider"/>, not <c>ILocalImageFileProvider"</c>
    /// (the interface <see cref="LocalImageBookProvider"/> uses): an embedded image is not a
    /// standalone file on disk, so there is no <c>FileSystemMetadata"</c> to hand Emby the way a
    /// sidecar <c>cover.jpg</c> has. <see cref="IRemoteImageProvider"/>'s URL-based contract fits
    /// instead - but, as with the very similar Komga cover proxy, Emby fetches a
    /// <see cref="RemoteImageInfo.Url"/> itself, unauthenticated, during a normal scan, rather
    /// than routing back through <see cref="GetImageResponse"/>. This provider therefore hands
    /// Emby a URL pointing at this plugin's own <c>Api/EmbeddedCoverApi.cs</c> endpoint (same
    /// pattern as <c>KomgaProvider</c>/<c>Api/KomgaImageApi.cs</c>), which re-opens the file and
    /// extracts the cover server-side when Emby's downloader fetches it.
    /// </para>
    /// <para>
    /// <b>Order 1</b>, one tier below <see cref="LocalImageBookProvider"/> (order 0): an existing
    /// sidecar <c>cover.jpg</c>/<c>folder.jpg</c>/<c>poster.jpg</c>/<c>front.jpg</c> always wins,
    /// this provider only contributes a candidate when none exists - the same "local before
    /// remote"/precedence-by-Order principle already used throughout this plugin, requiring no
    /// special-case logic here. This also outranks a network-fetched Komga cover (order 15, see
    /// <c>KomgaProvider</c>) when Komga is enabled, consistent with local sources always taking
    /// precedence over remote ones.
    /// </para>
    /// <para>
    /// <see cref="GetImages"/> only checks the file extension (no archive I/O) before offering a
    /// candidate; the actual extraction (and thus whether a cover is truly found) happens lazily
    /// in <c>Api/EmbeddedCoverApi.cs</c> when/if Emby's downloader actually requests it - avoiding
    /// opening the archive twice for the common case where the candidate is used.
    /// </para>
    /// </remarks>
    public class EmbeddedCoverImageProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="EmbeddedCoverImageProvider"/>.
        /// </summary>
        /// <param name="logger">Logger injected by Emby, named for this provider.</param>
        public EmbeddedCoverImageProvider(ILogger logger)
        {
            _logger = FilteredLogger.Wrap(logger ?? throw new ArgumentNullException(nameof(logger)));
        }

        /// <inheritdoc />
        public string Name => "Okuzukey Embedded Cover";

        /// <summary>
        /// One tier below <see cref="LocalImageBookProvider"/> (0) - see the class remarks.
        /// </summary>
        public int Order => 1;

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

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();

            var path = item?.Path;
            if (string.IsNullOrEmpty(path) || !EmbeddedCoverExtractor.SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                return images;
            }

            var proxyUrl = await BuildProxyUrlAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                images.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = proxyUrl,
                    Type = ImageType.Primary
                });
            }

            return images;
        }

        /// <summary>
        /// Builds this plugin's own image-proxy URL (<c>Api/EmbeddedCoverApi.cs</c>) carrying the
        /// item's own file path plus a <see cref="ProxyUrlSigner"/> signature over it, on Emby's
        /// own loopback API URL (<see cref="ProxyUrlSigner.GetLocalApiBaseUrlAsync"/>) - same
        /// technique as <see cref="Remote.KomgaProvider.BuildProxyImageUrlAsync"/>. Returns
        /// <c>null</c> if <see cref="Plugin.Instance"/>/<see cref="Plugin.ApplicationHost"/> is
        /// unexpectedly unavailable rather than throwing.
        /// </summary>
        internal static async Task<string> BuildProxyUrlAsync(string path, CancellationToken cancellationToken)
        {
            var localApiUrl = await ProxyUrlSigner.GetLocalApiBaseUrlAsync(cancellationToken).ConfigureAwait(false);
            if (localApiUrl == null)
            {
                return null;
            }

            var signature = ProxyUrlSigner.Sign(EmbeddedCoverExtractor.SignaturePurpose, path);
            return $"{localApiUrl}/Okuzukey/EmbeddedCover?path={Uri.EscapeDataString(path)}&sig={Uri.EscapeDataString(signature)}";
        }

        /// <summary>
        /// Kept as a spec-compliant <see cref="IRemoteImageProvider"/> implementation and as a
        /// fallback (same reasoning as <c>KomgaProvider.GetImageResponse</c>) - the normal
        /// per-scan cover download goes through <c>Api/EmbeddedCoverApi.cs</c> instead, per the
        /// class remarks.
        /// </summary>
        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var path = ProxyUrlSigner.GetQueryValue(url, "path");
            if (!ProxyUrlSigner.IsAuthorizedEmbeddedCover(path, ProxyUrlSigner.GetQueryValue(url, "sig")))
            {
                return Task.FromResult<HttpResponseInfo>(null);
            }

            var result = EmbeddedCoverExtractor.ExtractFromPath(path, _logger);
            if (result?.Bytes == null)
            {
                return Task.FromResult<HttpResponseInfo>(null);
            }

            return Task.FromResult(new HttpResponseInfo
            {
                Content = new MemoryStream(result.Bytes),
                ContentType = result.ContentType,
                StatusCode = HttpStatusCode.OK
            });
        }
    }
}
