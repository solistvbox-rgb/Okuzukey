using System.Threading.Tasks;
using Emby.Okuzukey.Helpers;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Okuzukey.Api
{
    /// <summary>
    /// Request DTO for the embedded-cover proxy, see <see cref="EmbeddedCoverApi"/> for why this
    /// endpoint exists.
    /// </summary>
    /// <remarks>
    /// <see cref="UnauthenticatedAttribute"/> is required for the same reason as
    /// <c>Api/KomgaImageApi.cs</c>'s <c>GetKomgaImage</c> (see its remarks): Emby's own internal
    /// image downloader attaches no Emby session/API credential, so a route requiring one would
    /// reject it with HTTP 401.
    /// <para>
    /// <see cref="Sig"/> is an HMAC over <see cref="Path"/> created by
    /// <see cref="ProxyUrlSigner"/>: without it, anyone reaching the Emby port could make the
    /// server open any file path, including a UNC path on a foreign SMB server.
    /// </para>
    /// </remarks>
    [Unauthenticated]
    [Route("/Okuzukey/EmbeddedCover", "GET", Summary = "Extracts and proxies the embedded cover image from a local .cbz/.cbr/.epub file")]
    public class GetEmbeddedCover : IReturn<object>
    {
        public string Path { get; set; }

        public string Sig { get; set; }
    }

    /// <summary>
    /// Emby-hosted REST endpoint (auto-discovered the same way as any <see cref="IService"/> in
    /// this assembly) that extracts and streams an embedded comic/e-book cover.
    /// </summary>
    /// <remarks>
    /// Exists for the same structural reason as <c>Api/KomgaImageApi.cs</c> (see its remarks):
    /// Emby fetches a remote image provider's <c>RemoteImageInfo.Url</c> itself during a normal scan, so
    /// <c>EmbeddedCoverImageProvider.GetImages</c> hands Emby a URL pointing here (carrying the
    /// item's own file path as a query parameter) instead of trying to serve the image any other
    /// way. When Emby's downloader fetches it, this handler re-opens that same local file and
    /// extracts the cover via <see cref="EmbeddedCoverExtractor"/>.
    /// <para>
    /// Only paths signed by <see cref="ProxyUrlSigner"/>, or paths of a book in an Emby library
    /// (for URLs Emby stored before a restart), are
    /// accepted; anything else is answered with 403 before any file is touched. Even then, only
    /// bytes of an image entry found *inside* a valid <c>.cbz</c>/<c>.cbr</c>/<c>.epub</c> archive
    /// are returned, never the raw file, and entry sizes are capped (see
    /// <see cref="EmbeddedCoverExtractor"/>). A missing cover yields a 404.
    /// </para>
    /// </remarks>
    public class EmbeddedCoverApi : IService, IRequiresRequest
    {
        public IRequest Request { get; set; }

        public Task<object> Get(GetEmbeddedCover request)
        {
            if (!ProxyUrlSigner.IsAuthorizedEmbeddedCover(request?.Path, request?.Sig))
            {
                SetStatus(403);
                return Task.FromResult<object>(null);
            }

            var logger = FilteredLogger.Wrap(Request?.Logger);
            var result = EmbeddedCoverExtractor.ExtractFromPath(request.Path, logger);

            if (result?.Bytes == null)
            {
                SetStatus(404);
                return Task.FromResult<object>(null);
            }

            return Task.FromResult<object>(new ImageStreamResult
            {
                Stream = new System.IO.MemoryStream(result.Bytes),
                MediaContentType = result.ContentType
            });
        }

        private void SetStatus(int statusCode)
        {
            if (Request?.Response != null)
            {
                Request.Response.StatusCode = statusCode;
            }
        }
    }
}
