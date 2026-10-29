using System.Threading;
using System.Threading.Tasks;
using Emby.Okuzukey.Helpers;
using Emby.Okuzukey.Providers.Remote;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Okuzukey.Api
{
    /// <summary>
    /// Request DTO for the Komga cover image proxy, see <see cref="KomgaImageApi"/> for why this
    /// endpoint exists.
    /// </summary>
    /// <remarks>
    /// <see cref="UnauthenticatedAttribute"/> is required, not just the absence of
    /// <c>[Authenticated]</c>: Emby's service framework rejects every route with HTTP 401
    /// ("Access token is invalid or expired") by default unless a route is explicitly marked
    /// unauthenticated, and Emby's own internal image downloader attaches no Emby credential
    /// itself when it fetches this endpoint.
    /// </remarks>
    [Unauthenticated]
    [Route("/Okuzukey/KomgaImage/{BookId}", "GET", Summary = "Proxies a Komga book cover thumbnail")]
    public class GetKomgaImage : IReturn<object>
    {
        public string BookId { get; set; }

        /// <summary>HMAC over <see cref="BookId"/> from <see cref="ProxyUrlSigner"/>.</summary>
        public string Sig { get; set; }
    }

    /// <summary>
    /// Emby-hosted REST endpoint (auto-discovered the same way as any <see cref="IService"/> in
    /// this assembly - no explicit registration needed) that proxies a single Komga book's cover thumbnail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// During a normal library scan, Emby downloads a remote image provider's
    /// <c>RemoteImageInfo.Url</c> itself, via its own generic, unauthenticated internal HTTP
    /// client - it does not call back into <c>KomgaProvider.GetImageResponse</c> for this. Komga
    /// requires the <c>X-API-Key</c> header on (almost) every endpoint including thumbnails, so a
    /// direct Komga URL there would always come back HTTP 401.
    /// </para>
    /// <para>
    /// <c>KomgaProvider.GetImages</c> therefore hands Emby a URL pointing at this endpoint instead
    /// (same host/port as Emby's own dashboard/API, resolved via
    /// <see cref="Plugin.ApplicationHost"/>'s <c>GetLocalApiUrl</c>). When Emby's downloader
    /// fetches it, the request lands here, inside the plugin's own process - the actual
    /// authenticated request to Komga happens from here, server-side
    /// (<see cref="KomgaProvider.FetchThumbnailAsync"/>), exactly like the metadata JSON requests
    /// already work. The Komga API key is therefore never sent toward Emby's frontend/downloader,
    /// and no Komga credential ever needs to appear in a URL Emby fetches on its own.
    /// </para>
    /// <para>
    /// Marked <see cref="UnauthenticatedAttribute"/> (see <see cref="GetKomgaImage"/>'s own
    /// remarks) rather than requiring Emby authentication: Emby's own internal downloader does
    /// not attach any Emby session/API credential when fetching a <c>RemoteImageInfo.Url</c>
    /// - requiring Emby authentication here would fail with 401. Instead, the
    /// book id must be well-formed (<see cref="KomgaProvider.IsValidBookId"/>) and carry a
    /// signature from <see cref="ProxyUrlSigner"/>, so only URLs this plugin generated are served
    /// and anonymous callers cannot use the endpoint to occupy the shared Komga request gate.
    /// The returned content type is restricted to <c>image/*</c>.
    /// </para>
    /// </remarks>
    public class KomgaImageApi : IService, IRequiresRequest
    {
        public IRequest Request { get; set; }

        public async Task<object> Get(GetKomgaImage request)
        {
            if (!KomgaProvider.IsValidBookId(request?.BookId)
                || !ProxyUrlSigner.IsAuthorizedKomgaBook(request.BookId, request.Sig))
            {
                SetStatus(403);
                return null;
            }

            var logger = FilteredLogger.Wrap(Request?.Logger);
            var streamResult = await KomgaProvider.FetchThumbnailAsync(request.BookId, logger, Request?.CancellationToken ?? CancellationToken.None)
                .ConfigureAwait(false);

            // Only a real raster image is passed on, whatever Komga (or a proxy in front of it)
            // claims in its Content-Type header.
            var contentType = ImageSniffer.DetectContentType(streamResult?.Stream);
            if (contentType == null)
            {
                streamResult?.Stream?.Dispose();
                SetStatus(404);
                return null;
            }

            return new ImageStreamResult
            {
                Stream = streamResult.Stream,
                MediaContentType = contentType
            };
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
