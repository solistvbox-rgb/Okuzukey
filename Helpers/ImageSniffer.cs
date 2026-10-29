using System;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Identifies an image by its first bytes ("magic number") instead of trusting a file
    /// extension, a manifest entry or an HTTP header.
    /// </summary>
    /// <remarks>
    /// An EPUB manifest can declare any file as its cover, e.g. an SVG with an embedded
    /// <c>&lt;script&gt;</c>, and the cover proxy is unauthenticated. Only the raster formats below
    /// are delivered; anything else (SVG, XHTML, HTML error pages, executables) is refused.
    /// </remarks>
    internal static class ImageSniffer
    {
        /// <summary>
        /// Returns the MIME type for JPEG, PNG, GIF, WebP or BMP data, or <c>null</c> if
        /// <paramref name="data"/> is none of them.
        /// </summary>
        public static string DetectContentType(byte[] data, int length)
        {
            if (data == null || length < 4)
            {
                return null;
            }

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            {
                return "image/png";
            }

            if (length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8'
                && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
            {
                return "image/gif";
            }

            if (length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            {
                return "image/webp";
            }

            if (data[0] == (byte)'B' && data[1] == (byte)'M')
            {
                return "image/bmp";
            }

            return null;
        }

        public static string DetectContentType(byte[] data)
        {
            return DetectContentType(data, data?.Length ?? 0);
        }

        /// <summary>
        /// Same check for an in-memory stream (Komga downloads), without consuming it: the stream
        /// position is restored.
        /// </summary>
        public static string DetectContentType(System.IO.Stream stream)
        {
            if (stream == null || !stream.CanSeek)
            {
                return null;
            }

            var position = stream.Position;
            var header = new byte[12];
            var read = 0;
            int n;
            while (read < header.Length && (n = stream.Read(header, read, header.Length - read)) > 0)
            {
                read += n;
            }

            stream.Position = position;
            return DetectContentType(header, read);
        }
    }
}
