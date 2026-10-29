using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Reads a stream into memory with a hard size limit, so a crafted archive entry (a "zip
    /// bomb" that inflates to gigabytes) or an oversized HTTP response cannot exhaust the Emby
    /// server's memory. The declared size of an archive entry or a <c>Content-Length</c> header
    /// can be forged, so the limit is enforced while reading, not only up front.
    /// </summary>
    internal static class BoundedReader
    {
        /// <summary>Upper limit for a single cover image (embedded or from Komga).</summary>
        public const long MaxImageBytes = 25L * 1024 * 1024;

        /// <summary>Upper limit for a single XML document inside an archive (ComicInfo.xml, EPUB container/OPF).</summary>
        public const long MaxXmlBytes = 4L * 1024 * 1024;

        /// <summary>Upper limit for a single Komga JSON response (a 500-book listing page is a few MB).</summary>
        public const long MaxJsonBytes = 64L * 1024 * 1024;

        public static byte[] ReadAllBytes(Stream stream, long maxBytes)
        {
            using (var memoryStream = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ThrowIfTooLarge(memoryStream.Length + read, maxBytes);
                    memoryStream.Write(buffer, 0, read);
                }

                return memoryStream.ToArray();
            }
        }

        public static string ReadAllText(Stream stream, long maxBytes)
        {
            var bytes = ReadAllBytes(stream, maxBytes);
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        public static async Task<MemoryStream> ReadToMemoryAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
        {
            var memoryStream = new MemoryStream();
            try
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    ThrowIfTooLarge(memoryStream.Length + read, maxBytes);
                    memoryStream.Write(buffer, 0, read);
                }

                memoryStream.Position = 0;
                return memoryStream;
            }
            catch
            {
                memoryStream.Dispose();
                throw;
            }
        }

        /// <summary>Throws <see cref="InvalidDataException"/> if <paramref name="size"/> exceeds <paramref name="maxBytes"/>.</summary>
        public static void ThrowIfTooLarge(long size, long maxBytes)
        {
            if (size > maxBytes)
            {
                throw new InvalidDataException($"Content exceeds the size limit of {maxBytes / (1024 * 1024)} MB.");
            }
        }
    }
}
