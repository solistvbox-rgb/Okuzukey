using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Result of <see cref="EpubHelper.LoadOpfDocument"/>: the EPUB's internal package (OPF)
    /// document, plus the path it was found at inside the EPUB (needed to resolve any
    /// manifest-relative reference, e.g. a cover image href, against the OPF's own directory
    /// rather than the EPUB root).
    /// </summary>
    internal sealed class EpubOpfResult
    {
        public XDocument OpfDocument { get; set; }

        public string OpfPath { get; set; }
    }

    /// <summary>
    /// Shared EPUB package-document (OPF) locating logic, used by both
    /// <see cref="EmbeddedCoverExtractor"/> (cover image) and
    /// <c>Providers/Local/OpfBookProvider.cs</c> (full metadata) - both need the exact same first
    /// step (find the OPF via the EPUB's container, per the EPUB spec), only what they do with
    /// the resulting document differs.
    /// </summary>
    internal static class EpubHelper
    {
        /// <summary>
        /// Locates and loads the EPUB's package document: reads
        /// <c>META-INF/container.xml</c> for the <c>&lt;rootfile full-path="..."/&gt;</c>
        /// pointing at the OPF, then loads that OPF. Returns <c>null</c> if the EPUB has no
        /// container or the referenced OPF is missing; throws (<see cref="InvalidDataException"/>,
        /// <see cref="System.Xml.XmlException"/>, I/O exceptions) if the file is not a valid zip,
        /// the XML is malformed or an entry exceeds the size limit - callers catch these.
        /// </summary>
        public static EpubOpfResult LoadOpfDocument(string epubPath)
        {
            using (var archive = ZipFile.OpenRead(epubPath))
            {
                var containerEntry = archive.GetEntry("META-INF/container.xml");
                if (containerEntry == null)
                {
                    return null;
                }

                string opfPath;
                using (var stream = OpenBounded(containerEntry))
                {
                    var containerDoc = SafeXml.Load(stream);
                    opfPath = containerDoc.Descendants()
                        .FirstOrDefault(e => string.Equals(e.Name.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))
                        ?.Attribute("full-path")?.Value;
                }

                if (string.IsNullOrWhiteSpace(opfPath))
                {
                    return null;
                }

                var opfEntry = archive.GetEntry(opfPath);
                if (opfEntry == null)
                {
                    return null;
                }

                using (var stream = OpenBounded(opfEntry))
                {
                    return new EpubOpfResult
                    {
                        OpfDocument = SafeXml.Load(stream),
                        OpfPath = opfPath
                    };
                }
            }
        }

        /// <summary>Reads a zip entry into memory, capped at <see cref="BoundedReader.MaxXmlBytes"/>.</summary>
        private static MemoryStream OpenBounded(ZipArchiveEntry entry)
        {
            BoundedReader.ThrowIfTooLarge(entry.Length, BoundedReader.MaxXmlBytes);
            using (var stream = entry.Open())
            {
                return new MemoryStream(BoundedReader.ReadAllBytes(stream, BoundedReader.MaxXmlBytes));
            }
        }

        /// <summary>Directory portion of a zip entry path (e.g. <c>"OEBPS"</c> for <c>"OEBPS/content.opf"</c>).</summary>
        public static string GetZipDirectory(string zipEntryPath)
        {
            var lastSlash = zipEntryPath.LastIndexOf('/');
            return lastSlash >= 0 ? zipEntryPath.Substring(0, lastSlash) : string.Empty;
        }

        /// <summary>Combines a zip directory with a manifest-relative path into a full zip entry path.</summary>
        public static string CombineZipPath(string directory, string relativePath)
        {
            return string.IsNullOrEmpty(directory) ? relativePath : directory + "/" + relativePath;
        }
    }
}
