using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Services;

namespace Emby.Okuzukey.Api
{
    /// <summary>
    /// Writes raw image bytes directly to the HTTP response instead of being JSON-serialized -
    /// the SDK's documented mechanism for a non-JSON service response (an <see cref="IService"/>
    /// method returning a type that implements <see cref="IAsyncStreamWriter"/> is written via
    /// <see cref="WriteToAsync"/> instead of the normal JSON serialization path). Shared by every
    /// image-proxy endpoint in this plugin (<c>KomgaImageApi</c>, <c>EmbeddedCoverApi</c>) so the
    /// <see cref="MediaBrowser.Model.Services.IResponse.OutputWriter"/>-is-a-<c>PipeWriter</c>-not-
    /// a-<c>Stream</c> handling exists in exactly one place.
    /// </summary>
    public sealed class ImageStreamResult : IAsyncStreamWriter
    {
        public Stream Stream { get; set; }

        /// <summary>
        /// Always a type determined by <c>Helpers.ImageSniffer</c> from the actual bytes, never one
        /// taken from a file extension or an upstream HTTP header.
        /// </summary>
        public string MediaContentType { get; set; }

        public async Task WriteToAsync(IResponse response, CancellationToken cancellationToken)
        {
            response.ContentType = MediaContentType;

            if (Stream == null)
            {
                return;
            }

            // IResponse has no plain Stream property in this SDK version - only OutputWriter
            // (a System.IO.Pipelines.PipeWriter), wrapped here via AsStream() so an ordinary
            // Stream can be copied across normally.
            using (Stream)
            using (var outputStream = response.OutputWriter.AsStream(leaveOpen: true))
            {
                await Stream.CopyToAsync(outputStream, 81920, cancellationToken).ConfigureAwait(false);
                await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
