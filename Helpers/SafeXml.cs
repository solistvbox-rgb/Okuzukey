using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// The only way this plugin parses XML (OPF, NFO, ComicInfo.xml, EPUB container): DTDs are
    /// ignored, no external resource is ever resolved, and files on disk are size-limited.
    /// </summary>
    /// <remarks>
    /// .NET's default <c>XDocument</c> parsing does process an inline DTD: external entities are
    /// not resolved and an entity bomb is stopped by .NET's built-in 10-million-character limit,
    /// but internal entities are expanded. Real OPF, NFO and
    /// ComicInfo.xml files never need a DTD, so it is now ignored altogether
    /// (<see cref="DtdProcessing.Ignore"/> rather than <c>Prohibit</c>, so a file that merely carries
    /// a harmless <c>&lt;!DOCTYPE&gt;</c> line still parses; any entity it would declare is then
    /// undefined and its use is an <see cref="XmlException"/>, i.e. the file is skipped).
    /// </remarks>
    internal static class SafeXml
    {
        private static XmlReaderSettings CreateSettings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                CloseInput = false
            };
        }

        public static XDocument Parse(string text)
        {
            using (var stringReader = new StringReader(text))
            using (var reader = XmlReader.Create(stringReader, CreateSettings()))
            {
                return XDocument.Load(reader, LoadOptions.None);
            }
        }

        public static XDocument Load(Stream stream)
        {
            using (var reader = XmlReader.Create(stream, CreateSettings()))
            {
                return XDocument.Load(reader, LoadOptions.None);
            }
        }

        /// <summary>
        /// Loads an XML file from disk, refusing files larger than
        /// <see cref="BoundedReader.MaxXmlBytes"/> (a sidecar <c>metadata.opf</c>/<c>.nfo</c> of
        /// several hundred MB would otherwise be read into memory completely).
        /// </summary>
        public static XDocument LoadFile(string path)
        {
            using (var stream = OpenBoundedFile(path))
            {
                return Load(stream);
            }
        }

        /// <summary>Reads a text file from disk with the same size limit as <see cref="LoadFile"/>.</summary>
        public static string ReadAllTextBounded(string path)
        {
            using (var stream = OpenBoundedFile(path))
            using (var reader = new StreamReader(stream, true))
            {
                return reader.ReadToEnd();
            }
        }

        private static FileStream OpenBoundedFile(string path)
        {
            BoundedReader.ThrowIfTooLarge(new FileInfo(path).Length, BoundedReader.MaxXmlBytes);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }
}
