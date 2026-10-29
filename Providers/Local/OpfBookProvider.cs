using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Emby.Okuzukey.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Okuzukey.Providers.Local
{
    /// <summary>
    /// Local metadata provider that reads book metadata from a Calibre <c>metadata.opf</c>
    /// (Open Packaging Format, OPF) in the item directory, or - if none exists - directly from
    /// the OPF package document embedded inside a <c>.epub</c> file itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>metadata.opf</c> is searched first (Calibre's default file name for a book in
    /// its own directory). <c>content.opf</c> is additionally supported as a compatibility fallback,
    /// the generic OPF file name that other tools (e.g. the EPUB container
    /// itself) also use. Unlike NFO files, OPF has no file-name-specific
    /// variant, since Calibre always creates a separate directory per book.
    /// </para>
    /// <para>
    /// <b>Embedded EPUB OPF fallback:</b>
    /// if no sidecar OPF file is found and the item is a <c>.epub</c>, its own internal package
    /// document is read instead, via <see cref="EpubHelper.LoadOpfDocument"/> (the same helper
    /// <see cref="EmbeddedCoverImageProvider"/> uses for the cover). Every EPUB already has such
    /// a document (it is how any EPUB reader finds the book's title/author/etc.) using the exact
    /// same Dublin Core schema as Calibre's sidecar file, so no separate parsing logic is
    /// needed - only Calibre-specific extras (<c>calibre:series_index</c>/<c>calibre:rating</c>)
    /// will simply be absent from a plain, non-Calibre-processed EPUB, which is expected and
    /// handled the same as any other missing field.
    /// </para>
    /// <para>
    /// OPF is XML-namespace based: bibliographic fields live in the Dublin Core namespace
    /// (prefix <c>dc:</c>), Calibre-specific extra fields (series, rating, ...) are carried in
    /// <c>&lt;meta name="calibre:..." content="..."/&gt;</c> elements in the OPF namespace.
    /// This class queries both namespaces correctly, but - for maximum
    /// robustness against sloppily generated OPF files without a (complete)
    /// namespace declaration - falls back to a namespace-independent search
    /// for the local element name when the result list is empty.
    /// </para>
    /// <para>
    /// The <see cref="Order"/> value is deliberately above that of <c>NfoBookProvider</c>, so that an
    /// existing NFO file always takes precedence over an OPF file (both are local sources,
    /// the priority among them is controlled by the Order values). A missing or
    /// malformed OPF never aborts - analogous to NfoBookProvider - the
    /// library scan.
    /// </para>
    /// </remarks>
    public class OpfBookProvider : ILocalMetadataProvider<Book>, IHasOrder
    {
        /// <summary>OPF file names in check order: Calibre default first.</summary>
        private static readonly string[] OpfFileNames = { "metadata.opf", "content.opf" };

        /// <summary>Dublin Core namespace in which the core bibliographic OPF fields live.</summary>
        private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";

        /// <summary>OPF namespace in which structural and Calibre extra elements (including &lt;meta&gt;) live.</summary>
        private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";

        /// <summary>
        /// Known MARC relator codes (<c>opf:role</c>) for <c>dc:creator</c> entries that are
        /// common in Calibre libraries, especially for manga/light novels (illustrator,
        /// translator). Unknown or missing roles are treated as "Author".
        /// </summary>
        private static readonly Dictionary<string, string> CreatorRoleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "aut", "Author" },
            { "ill", "Illustrator" },
            { "edt", "Editor" },
            { "trl", "Translator" },
            { "nrt", "Narrator" },
            { "art", "Artist" }
        };

        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="OpfBookProvider"/>.
        /// </summary>
        /// <param name="fileSystem">File system access injected by Emby.</param>
        /// <param name="logger">Logger injected by Emby, named for this provider.</param>
        public OpfBookProvider(IFileSystem fileSystem, ILogger logger)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = FilteredLogger.Wrap(logger ?? throw new ArgumentNullException(nameof(logger)));
        }

        /// <inheritdoc />
        public string Name => "Okuzukey OPF (Calibre)";

        /// <summary>
        /// Lowest order value of the three local metadata readers (below <c>NfoBookProvider</c>,
        /// 1, and <c>ComicInfoProvider</c>, 2), so a Calibre-style sidecar OPF - the richest of
        /// the three formats, e.g. the only one with a machine-readable series index - is tried
        /// first by default. Still well below all remote providers (e.g. Komga). This is only the
        /// default seed order Emby's dashboard ("Metadata Readers" under library settings)
        /// presents on first setup; an admin can always drag-reorder it per library from there,
        /// which then takes precedence.
        /// </summary>
        public int Order => 0;

        /// <inheritdoc />
        public Task<MetadataResult<Book>> GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Book> { HasMetadata = false, People = new List<PersonInfo>() };

            FileSystemMetadata opfFile;
            try
            {
                opfFile = FindOpfFile(info.Path);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Okuzukey] Error while searching for an OPF file for '{0}'.", ex, info.Path);
                return Task.FromResult(result);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string source;
            try
            {
                XDocument document;

                if (opfFile != null && opfFile.Exists)
                {
                    source = opfFile.FullName;
                    document = SafeXml.LoadFile(source);
                }
                else if (string.Equals(Path.GetExtension(info.Path), ".epub", StringComparison.OrdinalIgnoreCase))
                {
                    var epubOpf = EpubHelper.LoadOpfDocument(info.Path);
                    if (epubOpf == null)
                    {
                        _logger.Debug("[Okuzukey] No OPF file found for '{0}'.", info.Path);
                        return Task.FromResult(result);
                    }

                    source = info.Path + " (embedded OPF)";
                    document = epubOpf.OpfDocument;
                }
                else
                {
                    _logger.Debug("[Okuzukey] No OPF file found for '{0}'.", info.Path);
                    return Task.FromResult(result);
                }

                var book = new Book();
                ParseOpfDocument(document, source, book, result, _logger, cancellationToken);
                MetadataSanitizer.Sanitize(book, result.People);
                result.Item = book;
                result.HasMetadata = true;

                _logger.Info("[Okuzukey] Metadata successfully read from '{0}'.", source);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (XmlException ex)
            {
                _logger.ErrorException(
                    "[Okuzukey] Invalid XML in OPF data for '{0}'. Item is skipped, other providers can step in.",
                    ex,
                    info.Path);
                result.HasMetadata = false;
                result.Item = null;
            }
            catch (IOException ex)
            {
                _logger.ErrorException("[Okuzukey] I/O error while reading OPF data for '{0}'.", ex, info.Path);
                result.HasMetadata = false;
                result.Item = null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.ErrorException("[Okuzukey] No permission to read OPF data for '{0}'.", ex, info.Path);
                result.HasMetadata = false;
                result.Item = null;
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly: a single faulty OPF (sidecar or embedded) must
                // under no circumstances abort the library scan.
                _logger.ErrorException("[Okuzukey] Unexpected error while processing OPF data for '{0}'.", ex, info.Path);
                result.HasMetadata = false;
                result.Item = null;
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// Finds the OPF file belonging to the item in the item directory (or in the parent
        /// directory if the item itself is a file).
        /// </summary>
        /// <param name="itemPath">Path of the book file or book directory, as passed by
        /// Emby via <see cref="ItemInfo.Path"/>.</param>
        /// <returns>The OPF file found, or <c>null</c> if none of the candidates
        /// exists or the directory path could not be determined.</returns>
        private FileSystemMetadata FindOpfFile(string itemPath)
        {
            if (string.IsNullOrEmpty(itemPath))
            {
                return null;
            }

            var itemInfo = _fileSystem.GetFileSystemInfo(itemPath);

            var directoryInfo = itemInfo.IsDirectory
                ? itemInfo
                : _fileSystem.GetDirectoryInfo(Path.GetDirectoryName(itemPath));

            if (directoryInfo == null || !directoryInfo.Exists)
            {
                return null;
            }

            var directoryPath = directoryInfo.FullName;

            foreach (var candidateName in OpfFileNames)
            {
                var candidateFile = _fileSystem.GetFileInfo(Path.Combine(directoryPath, candidateName));
                if (candidateFile.Exists)
                {
                    return candidateFile;
                }
            }

            return null;
        }

        /// <summary>
        /// Transfers the recognized fields of an already-loaded OPF document (from a sidecar
        /// file, from an embedded EPUB OPF, or - see
        /// <see cref="Helpers.SidecarMetadataReader"/> - as a gap-filling source for
        /// <see cref="ComicInfoProvider"/>) to <paramref name="book"/> and
        /// <paramref name="result"/>. <c>internal static</c> (not just this class's own
        /// <see cref="GetMetadata"/>) so that reuse; unconditionally overwrites whatever field it
        /// finds a value for, so a caller wanting "fill gaps only" must call this
        /// <em>before</em> a higher-priority source, not after.
        /// </summary>
        /// <param name="source">Human-readable description of where <paramref name="document"/>
        /// came from, used only for the date-parse warning log message below.</param>
        /// <param name="logger">Logger for the date-parse warning below; the caller's own,
        /// since this method has no provider instance of its own to draw one from.</param>
        internal static void ParseOpfDocument(XDocument document, string source, Book book, MetadataResult<Book> result, ILogger logger, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (document.Root == null)
            {
                throw new XmlException($"OPF data from '{source}' contains no root element.");
            }

            // Title
            var title = TitleCleaner.StripNonFilingMarkers(GetFirstDcValue(document, "title"));
            if (!string.IsNullOrWhiteSpace(title))
            {
                book.Name = title;
            }

            // Description
            var description = GetFirstDcValue(document, "description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                book.Overview = OverviewCleaner.ToPlainText(description);
            }

            // Publisher (may occur multiple times in rare cases)
            foreach (var publisher in GetDcValues(document, "publisher"))
            {
                book.AddStudio(publisher);
            }

            // Release date / year
            var dateText = GetFirstDcValue(document, "date");
            if (!string.IsNullOrWhiteSpace(dateText))
            {
                if (DateTime.TryParse(
                        dateText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var premiereDate))
                {
                    book.PremiereDate = premiereDate;
                    book.ProductionYear = premiereDate.Year;
                }
                else if (int.TryParse(dateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearOnly)
                         && yearOnly > 0)
                {
                    // Some OPF files contain only a plain year in <dc:date>.
                    book.ProductionYear = yearOnly;
                }
                else
                {
                    logger.Warn("[Okuzukey] Could not interpret date '{0}' in '{1}'.", dateText, source);
                }
            }

            // Subjects / Genres
            foreach (var subject in GetDcValues(document, "subject"))
            {
                if (!book.Genres.Contains(subject, StringComparer.OrdinalIgnoreCase))
                {
                    book.AddGenre(subject);
                }
            }

            // Authors and other contributors (dc:creator, possibly with opf:role)
            foreach (var creatorElement in GetDcElements(document, "creator"))
            {
                var creatorName = creatorElement.Value?.Trim();
                if (string.IsNullOrWhiteSpace(creatorName))
                {
                    continue;
                }

                var roleCode = GetOpfAttributeValue(creatorElement, "role");
                var personType = "Author";

                if (!string.IsNullOrWhiteSpace(roleCode) && CreatorRoleMap.TryGetValue(roleCode.Trim(), out var mappedType))
                {
                    personType = mappedType;
                }

                result.People.Add(new PersonInfo
                {
                    Name = creatorName,
                    Type = PersonType.Writer,
                    Role = personType
                });
            }

            // Identifiers: dc:identifier with opf:scheme attribute (ISBN, AMAZON, GOOGLE, GOODREADS, ...)
            foreach (var identifierElement in GetDcElements(document, "identifier"))
            {
                var value = identifierElement.Value?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var scheme = GetOpfAttributeValue(identifierElement, "scheme");

                // EPUB3 drops opf:scheme and writes the ISBN as a URN instead
                // (<dc:identifier id="uid">urn:isbn:978...</dc:identifier>).
                var providerKey = string.IsNullOrWhiteSpace(scheme) && ProviderIdFilter.IsIsbnUrn(value)
                    ? "ISBN"
                    : NormalizeIdentifierScheme(scheme);

                if (ProviderIdFilter.IsAllowedKey(providerKey))
                {
                    var normalizedValue = string.Equals(providerKey, "ISBN", StringComparison.OrdinalIgnoreCase)
                        ? ProviderIdFilter.NormalizeIsbn(value)
                        : value;

                    book.SetProviderId(providerKey, normalizedValue);
                }
            }

            // Calibre-specific extra fields: series, series index, rating.

            var seriesIndexText = GetCalibreMetaContent(document, "calibre:series_index");
            if (!string.IsNullOrWhiteSpace(seriesIndexText)
                && double.TryParse(seriesIndexText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seriesIndex)
                && SafeNumbers.TryToIndexNumber(seriesIndex, out var indexNumber))
            {
                // Emby.IndexNumber is int?; a fractional Calibre series index (e.g. "1.5" for
                // a bonus/in-between volume, common with light novels) is rounded
                // commercially (away from zero), since no lossless representation is possible.
                book.IndexNumber = indexNumber;
            }

            // calibre:rating is already on a 0-10 scale (stars x 2).
            var ratingText = GetCalibreMetaContent(document, "calibre:rating");
            if (!string.IsNullOrWhiteSpace(ratingText)
                && float.TryParse(ratingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                && SafeNumbers.TryToCommunityRating(rating, 10f, out var communityRating))
            {
                book.CommunityRating = communityRating;
            }
        }

        /// <summary>
        /// Maps an <c>opf:scheme</c> attribute of a <c>dc:identifier</c> element to a
        /// uniform provider ID key. Known synonyms (e.g. <c>MOBI-ASIN</c>)
        /// are normalized; unknown schemes are taken over unchanged (trimmed)
        /// so that even exotic identifiers are not lost.
        /// </summary>
        private static string NormalizeIdentifierScheme(string scheme)
        {
            if (string.IsNullOrWhiteSpace(scheme))
            {
                return null;
            }

            var trimmed = scheme.Trim();

            if (string.Equals(trimmed, "ISBN", StringComparison.OrdinalIgnoreCase))
            {
                return "ISBN";
            }

            if (string.Equals(trimmed, "MOBI-ASIN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "ASIN", StringComparison.OrdinalIgnoreCase))
            {
                return "Asin";
            }

            if (string.Equals(trimmed, "GOOGLE", StringComparison.OrdinalIgnoreCase))
            {
                return "GoogleBooks";
            }

            return trimmed;
        }

        /// <summary>
        /// Returns the content of the first &lt;meta name="<paramref name="metaName"/>"
        /// content="..."/&gt; element (Calibre extra fields), or <c>null</c> if none
        /// was found.
        /// </summary>
        private static string GetCalibreMetaContent(XDocument document, string metaName)
        {
            foreach (var metaElement in GetMetaElements(document))
            {
                var nameAttribute = metaElement.Attribute("name");
                if (nameAttribute == null || !string.Equals(nameAttribute.Value, metaName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var contentAttribute = metaElement.Attribute("content");
                if (contentAttribute != null && !string.IsNullOrWhiteSpace(contentAttribute.Value))
                {
                    return contentAttribute.Value.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Returns all &lt;meta&gt; elements of the document. Searches the correct
        /// OPF namespace first; if the document contains no (complete) namespace declaration,
        /// falls back to a namespace-independent search for the local element name.
        /// </summary>
        private static IEnumerable<XElement> GetMetaElements(XDocument document)
        {
            var namespaced = document.Descendants(OpfNamespace + "meta").ToList();
            if (namespaced.Count > 0)
            {
                return namespaced;
            }

            return document.Descendants().Where(e => string.Equals(e.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the (trimmed) value of the first Dublin Core element with the given
        /// local name, or <c>null</c> if none was found.
        /// </summary>
        private static string GetFirstDcValue(XDocument document, string localName)
        {
            var element = GetDcElements(document, localName).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Value));
            return element?.Value.Trim();
        }

        /// <summary>
        /// Returns the (trimmed, duplicate-free) values of all Dublin Core elements with the
        /// given local name. Used for repeatable fields such as subjects/genres or
        /// publisher.
        /// </summary>
        private static IEnumerable<string> GetDcValues(XDocument document, string localName)
        {
            return GetDcElements(document, localName)
                .Where(e => !string.IsNullOrWhiteSpace(e.Value))
                .Select(e => e.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns all Dublin Core elements with the given local name. Searches the
        /// correct Dublin Core namespace (<c>dc:</c>) first; if the document contains no
        /// (complete) namespace declaration, falls back to a namespace-independent
        /// search for the local element name so that even sloppily generated OPF files can
        /// still be evaluated.
        /// </summary>
        private static IEnumerable<XElement> GetDcElements(XDocument document, string localName)
        {
            var namespaced = document.Descendants(DcNamespace + localName).ToList();
            if (namespaced.Count > 0)
            {
                return namespaced;
            }

            return document.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Reads the value of an attribute that may be declared in the OPF namespace (e.g.
        /// <c>opf:scheme</c>, <c>opf:role</c>), with a fallback to a namespace-independent
        /// search for the local attribute name for sloppily generated OPF files.
        /// </summary>
        private static string GetOpfAttributeValue(XElement element, string localName)
        {
            var namespacedAttribute = element.Attribute(OpfNamespace + localName);
            if (namespacedAttribute != null)
            {
                return namespacedAttribute.Value;
            }

            var fallbackAttribute = element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

            return fallbackAttribute?.Value;
        }
    }
}
