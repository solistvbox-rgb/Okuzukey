using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MediaBrowser.Model.Logging;
using SharpCompress.Archives.Rar;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Result of <see cref="EmbeddedCoverExtractor.ExtractFromPath"/>: the extracted cover image
    /// bytes plus a best-guess content type.
    /// </summary>
    internal sealed class EmbeddedCoverResult
    {
        public byte[] Bytes { get; set; }

        public string ContentType { get; set; }
    }

    /// <summary>
    /// Extracts the cover image embedded directly inside a <c>.cbz</c>/<c>.cbr</c> comic archive
    /// or a <c>.epub</c> e-book, used by <c>Providers/Local/EmbeddedCoverImageProvider.cs</c> (see
    /// its remarks for why this needs its own plugin-hosted proxy endpoint rather than a plain local image file).
    /// </summary>
    internal static class EmbeddedCoverExtractor
    {
        /// <summary>Purpose string for <see cref="ProxyUrlSigner"/> signatures over a file path.</summary>
        public const string SignaturePurpose = "embedded-cover";

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
        };

        /// <summary>
        /// File extensions this extractor supports, used by
        /// <see cref="Providers.Local.EmbeddedCoverImageProvider"/> to decide whether to offer a
        /// candidate image at all, without doing any archive I/O yet.
        /// </summary>
        public static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cbz", ".cbr", ".epub"
        };

        /// <summary>
        /// Extracts the cover image from <paramref name="path"/> based on its extension. Returns
        /// <c>null</c> (never throws, except on cooperative cancellation - there is none here
        /// since this is pure local file I/O) if the extension is unsupported, the file could not
        /// be opened, or no cover image could be identified inside it.
        /// </summary>
        public static EmbeddedCoverResult ExtractFromPath(string path, ILogger logger)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                var extension = Path.GetExtension(path);
                EmbeddedCoverResult result = null;
                if (string.Equals(extension, ".cbz", StringComparison.OrdinalIgnoreCase))
                {
                    result = ExtractFromCbz(path);
                }
                else if (string.Equals(extension, ".cbr", StringComparison.OrdinalIgnoreCase))
                {
                    result = ExtractFromCbr(path);
                }
                else if (string.Equals(extension, ".epub", StringComparison.OrdinalIgnoreCase))
                {
                    result = ExtractFromEpub(path);
                }

                return AcceptOnlyRasterImage(result, path, logger);
            }
            catch (InvalidDataException ex)
            {
                // Corrupt archive or an entry above BoundedReader's size limit - an expected,
                // file-specific condition, not a plugin error.
                logger?.Warn("[Okuzukey] No embedded cover read from '{0}': {1}", path, ExceptionText.Describe(ex));
                return null;
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly: a corrupt/unsupported archive, or a malformed
                // EPUB container/OPF, must never propagate as an error - just no cover found.
                logger?.ErrorException("[Okuzukey] Unexpected error extracting an embedded cover from '{0}'.", ex, path);
                return null;
            }
        }

        /// <summary>
        /// Delivers the extracted bytes only if they really are a JPEG/PNG/GIF/WebP/BMP image
        /// (checked by <see cref="ImageSniffer"/>, which also sets the content type), so an EPUB
        /// whose manifest declares an SVG/XHTML "cover" with a script cannot have it served by the
        /// unauthenticated cover proxy.
        /// </summary>
        private static EmbeddedCoverResult AcceptOnlyRasterImage(EmbeddedCoverResult result, string path, ILogger logger)
        {
            if (result?.Bytes == null)
            {
                return null;
            }

            var contentType = ImageSniffer.DetectContentType(result.Bytes);
            if (contentType == null)
            {
                logger?.Warn("[Okuzukey] The embedded cover of '{0}' is not a JPEG/PNG/GIF/WebP/BMP image; ignored.", path);
                return null;
            }

            result.ContentType = contentType;
            return result;
        }

        /// <summary>
        /// First image entry, sorted by name (the same "lowest-sorting page is the cover"
        /// convention comic readers/Komga itself use), excluding <c>ComicInfo.xml</c>.
        /// </summary>
        private static EmbeddedCoverResult ExtractFromCbz(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                var entry = archive.Entries
                    .Where(e => IsImageFile(e.FullName) && !IsComicInfoXml(e.FullName))
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (entry == null)
                {
                    return null;
                }

                BoundedReader.ThrowIfTooLarge(entry.Length, BoundedReader.MaxImageBytes);
                using (var stream = entry.Open())
                {
                    return new EmbeddedCoverResult
                    {
                        Bytes = BoundedReader.ReadAllBytes(stream, BoundedReader.MaxImageBytes),
                        ContentType = GetContentType(entry.FullName)
                    };
                }
            }
        }

        /// <summary>Same "first image, sorted by name" convention as <see cref="ExtractFromCbz"/>.</summary>
        private static EmbeddedCoverResult ExtractFromCbr(string path)
        {
            using (var archive = RarArchive.Open(path))
            {
                var entry = archive.Entries
                    .Where(e => !e.IsDirectory && IsImageFile(e.Key) && !IsComicInfoXml(e.Key))
                    .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (entry == null)
                {
                    return null;
                }

                BoundedReader.ThrowIfTooLarge(entry.Size, BoundedReader.MaxImageBytes);
                using (var stream = entry.OpenEntryStream())
                {
                    return new EmbeddedCoverResult
                    {
                        Bytes = BoundedReader.ReadAllBytes(stream, BoundedReader.MaxImageBytes),
                        ContentType = GetContentType(entry.Key)
                    };
                }
            }
        }

        /// <summary>
        /// Proper EPUB cover resolution via the package's own manifest, not a naming guess:
        /// <c>META-INF/container.xml</c> points to the OPF file, whose <c>&lt;manifest&gt;</c>
        /// declares the cover either as an EPUB3 <c>properties="cover-image"</c> item or (EPUB2)
        /// via a <c>&lt;meta name="cover" content="ID"/&gt;</c> pointing at a manifest item id.
        /// </summary>
        private static EmbeddedCoverResult ExtractFromEpub(string path)
        {
            var opf = EpubHelper.LoadOpfDocument(path);
            if (opf == null)
            {
                return null;
            }

            var coverHref = FindCoverHref(opf.OpfDocument);
            if (string.IsNullOrWhiteSpace(coverHref))
            {
                return null;
            }

            // Manifest hrefs are relative to the OPF file's own directory, not the EPUB root,
            // and may be percent-encoded (e.g. spaces as %20) per the OPF spec.
            var coverEntryPath = EpubHelper.CombineZipPath(EpubHelper.GetZipDirectory(opf.OpfPath), Uri.UnescapeDataString(coverHref));

            using (var archive = ZipFile.OpenRead(path))
            {
                var coverEntry = archive.GetEntry(coverEntryPath);
                if (coverEntry == null)
                {
                    return null;
                }

                BoundedReader.ThrowIfTooLarge(coverEntry.Length, BoundedReader.MaxImageBytes);
                using (var stream = coverEntry.Open())
                {
                    return new EmbeddedCoverResult
                    {
                        Bytes = BoundedReader.ReadAllBytes(stream, BoundedReader.MaxImageBytes),
                        ContentType = GetContentType(coverEntryPath)
                    };
                }
            }
        }

        private static string FindCoverHref(XDocument opfDoc)
        {
            var manifestItems = opfDoc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // EPUB3: <item properties="cover-image" href="..."/> (properties is a space-separated
            // list, so "cover-image" could appear alongside other property tokens).
            var epub3Cover = manifestItems.FirstOrDefault(e =>
                (e.Attribute("properties")?.Value ?? string.Empty)
                    .Split(' ')
                    .Contains("cover-image", StringComparer.OrdinalIgnoreCase));
            if (epub3Cover != null)
            {
                return epub3Cover.Attribute("href")?.Value;
            }

            // EPUB2 fallback: <meta name="cover" content="ID"/> in <metadata>, ID resolved
            // against the manifest item with a matching id.
            var coverId = opfDoc.Descendants()
                .FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("content")?.Value;

            var metaCoverHref = string.IsNullOrWhiteSpace(coverId)
                ? null
                : manifestItems
                    .FirstOrDefault(e => string.Equals(e.Attribute("id")?.Value, coverId, StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("href")?.Value;

            return metaCoverHref ?? FindCoverByNamingConvention(manifestItems);
        }

        /// <summary>
        /// Third, non-spec-mandated fallback for an EPUB that declares its cover via neither
        /// EPUB3's <c>properties="cover-image"</c> nor EPUB2's <c>&lt;meta name="cover"&gt;</c> -
        /// just an image manifest item whose <c>id</c> or file name happens to contain "cover", a
        /// convention several EPUB readers also fall back to. Needed e.g. for a Sigil-edited EPUB with
        /// <c>&lt;item href="Images/cover.jpeg" id="cover.jpeg" media-type="image/jpeg"/&gt;</c>
        /// and no cover metadata anywhere in the OPF (not even an empty <c>&lt;guide&gt;</c>
        /// reference).
        /// </summary>
        private static string FindCoverByNamingConvention(List<XElement> manifestItems)
        {
            return manifestItems
                .FirstOrDefault(e =>
                    (e.Attribute("media-type")?.Value ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && (ContainsCover(e.Attribute("id")?.Value) || ContainsCover(e.Attribute("href")?.Value)))
                ?.Attribute("href")?.Value;
        }

        private static bool ContainsCover(string value)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsImageFile(string entryPath)
        {
            return ImageExtensions.Contains(Path.GetExtension(entryPath));
        }

        private static bool IsComicInfoXml(string entryPath)
        {
            var separatorIndex = entryPath.LastIndexOfAny(new[] { '/', '\\' });
            var fileName = separatorIndex >= 0 ? entryPath.Substring(separatorIndex + 1) : entryPath;
            return string.Equals(fileName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetContentType(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                case ".bmp":
                    return "image/bmp";
                default:
                    return "application/octet-stream";
            }
        }
    }
}
