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
    /// Local metadata provider that reads book metadata from an NFO file (XML) in the
    /// item directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The following file names are searched in the book's directory, in this
    /// order:
    /// <list type="number">
    /// <item><description><c>[filename].nfo</c> - e.g. <c>The Lord of the Rings.nfo</c> next to
    /// <c>The Lord of the Rings.epub</c>. Preferred because it unambiguously belongs to a single file
    /// (important when several books share the same directory).</description></item>
    /// <item><description><c>book.nfo</c> - default file name for a book in its own
    /// directory.</description></item>
    /// <item><description><c>metadata.nfo</c> - alternative, generic file name.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// As a local provider, this class always takes precedence over all online providers;
    /// this is ensured by a low <see cref="Order"/> value.
    /// A missing or malformed NFO never aborts the library scan: all read and parse errors are
    /// caught and logged, and the method returns a result without metadata
    /// (<c>HasMetadata = false</c>) instead, so that downstream (remote) providers
    /// can take over.
    /// </para>
    /// <para>
    /// One specific, very common malformation is auto-corrected instead of just being skipped:
    /// a bare, unescaped <c>&amp;</c> in free text (e.g. <c>&lt;publisher&gt;Secker &amp; Warburg
    /// &lt;/publisher&gt;</c>) is detected and escaped to <c>&amp;amp;</c> before a second parse
    /// attempt, see <see cref="LoadXmlDocument"/>. Other kinds of malformed XML (e.g. unescaped
    /// <c>&lt;</c>/<c>&gt;</c>) are deliberately not auto-corrected, since guessing a fix for
    /// those is unsafe and could silently produce wrong data instead of just skipping the file.
    /// </para>
    /// </remarks>
    public class NfoBookProvider : ILocalMetadataProvider<Book>, IHasOrder
    {
        /// <summary>Generic NFO file names, checked after the file-name-specific variant.</summary>
        private static readonly string[] GenericNfoFileNames = { "book.nfo", "metadata.nfo" };

        /// <summary>Possible element names for the book title.</summary>
        private static readonly string[] TitleTags = { "title", "localtitle", "originaltitle", "name" };

        /// <summary>Possible element names for the description.</summary>
        private static readonly string[] OverviewTags = { "plot", "overview", "description", "summary" };

        /// <summary>Possible element names for the release year.</summary>
        private static readonly string[] YearTags = { "year", "productionyear" };

        /// <summary>Possible element names for the exact release date.</summary>
        private static readonly string[] PremiereDateTags = { "premiered", "releasedate", "publishdate", "firstaired", "aired" };

        /// <summary>Possible element names for genres (may occur multiple times).</summary>
        private static readonly string[] GenreTags = { "genre", "genres" };

        /// <summary>Possible element names for free-form tags (may occur multiple times).</summary>
        private static readonly string[] TagTags = { "tag", "tags" };

        /// <summary>Possible element names for publisher/studio (may occur multiple times).</summary>
        private static readonly string[] StudioTags = { "publisher", "studio", "studios" };

        /// <summary>Possible element names for authors/people (may occur multiple times).</summary>
        private static readonly string[] AuthorTags = { "author", "authors", "writer", "writers", "creator" };

        /// <summary>Possible element names for the ISBN.</summary>
        private static readonly string[] IsbnTags = { "isbn", "isbn13", "isbn10" };

        /// <summary>Possible element names for the community rating.</summary>
        private static readonly string[] RatingTags = { "rating", "communityrating" };

        /// <summary>Possible element names for the series name.</summary>
        private static readonly string[] SeriesTags = { "series", "seriesname" };

        /// <summary>Possible element names for the position within a series.</summary>
        private static readonly string[] VolumeTags = { "volume", "indexnumber", "issue", "number" };

        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="NfoBookProvider"/>.
        /// </summary>
        /// <param name="fileSystem">File system access injected by Emby.</param>
        /// <param name="logger">Logger injected by Emby, named for this provider.</param>
        public NfoBookProvider(IFileSystem fileSystem, ILogger logger)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = FilteredLogger.Wrap(logger ?? throw new ArgumentNullException(nameof(logger)));
        }

        /// <inheritdoc />
        public string Name => "Okuzukey NFO";

        /// <summary>
        /// Middle order value of the three local metadata readers - above <c>OpfBookProvider</c>
        /// (0), below <c>ComicInfoProvider</c> (2) - still well below all remote providers
        /// (e.g. Komga; local data always takes precedence). This is
        /// only the default seed order Emby's dashboard ("Metadata Readers" under library
        /// settings) presents on first setup; an admin can always drag-reorder it per library
        /// from there, which then takes precedence.
        /// </summary>
        public int Order => 1;

        /// <inheritdoc />
        public Task<MetadataResult<Book>> GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Book> { HasMetadata = false, People = new List<PersonInfo>() };

            FileSystemMetadata nfoFile;
            try
            {
                nfoFile = FindNfoFile(info.Path);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Okuzukey] Error while searching for an NFO file for '{0}'.", ex, info.Path);
                return Task.FromResult(result);
            }

            if (nfoFile == null || !nfoFile.Exists)
            {
                _logger.Debug("[Okuzukey] No NFO file found for '{0}'.", info.Path);
                return Task.FromResult(result);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var book = new Book();
                ParseNfoFile(nfoFile.FullName, book, result, _logger, cancellationToken);
                MetadataSanitizer.Sanitize(book, result.People);
                result.Item = book;
                result.HasMetadata = true;

                _logger.Info("[Okuzukey] Metadata successfully read from '{0}'.", nfoFile.FullName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (XmlException ex)
            {
                _logger.ErrorException(
                    "[Okuzukey] Invalid XML in NFO file '{0}'. File is skipped, online providers can step in.",
                    ex,
                    nfoFile.FullName);
                result.HasMetadata = false;
                result.Item = null;
            }
            catch (IOException ex)
            {
                _logger.ErrorException("[Okuzukey] I/O error while reading NFO file '{0}'.", ex, nfoFile.FullName);
                result.HasMetadata = false;
                result.Item = null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.ErrorException("[Okuzukey] No permission to read NFO file '{0}'.", ex, nfoFile.FullName);
                result.HasMetadata = false;
                result.Item = null;
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly: a single faulty NFO must under
                // no circumstances abort the library scan.
                _logger.ErrorException("[Okuzukey] Unexpected error while processing NFO file '{0}'.", ex, nfoFile.FullName);
                result.HasMetadata = false;
                result.Item = null;
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// Finds the NFO file belonging to the item according to the priority described in
        /// the class documentation.
        /// </summary>
        /// <param name="itemPath">Path of the book file or book directory, as passed by
        /// Emby via <see cref="ItemInfo.Path"/>.</param>
        /// <returns>The NFO file found, or <c>null</c> if none of the candidates
        /// exists or the parent directory path could not be determined.</returns>
        private FileSystemMetadata FindNfoFile(string itemPath)
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

            // 1. [filename].nfo - only relevant if the item itself is a file
            //    (for directory items there is no meaningful "file name").
            if (!itemInfo.IsDirectory)
            {
                var specificFile = _fileSystem.GetFileInfo(
                    Path.Combine(directoryPath, Path.GetFileNameWithoutExtension(itemPath) + ".nfo"));

                if (specificFile.Exists)
                {
                    return specificFile;
                }
            }

            // 2. book.nfo, then metadata.nfo.
            foreach (var candidateName in GenericNfoFileNames)
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
        /// Loads the NFO file as an <see cref="XDocument"/>, tolerating a bare, unescaped
        /// <c>&amp;</c> in free text (see <see cref="XmlSanitizer"/>).
        /// </summary>
        private static XDocument LoadXmlDocument(string path, ILogger logger)
        {
            var rawText = SafeXml.ReadAllTextBounded(path);
            return XmlSanitizer.ParseWithAmpersandFallback(rawText, path, logger);
        }

        /// <summary>
        /// Reads the given NFO file and transfers the recognized fields to
        /// <paramref name="book"/> and <paramref name="result"/>. <c>internal static</c> so
        /// <see cref="Helpers.SidecarMetadataReader"/> can reuse it as a gap-filling source for
        /// <see cref="ComicInfoProvider"/>; unconditionally overwrites whatever field it finds a
        /// value for, so a caller wanting "fill gaps only" must call this <em>before</em> a
        /// higher-priority source, not after.
        /// </summary>
        /// <param name="logger">Logger for the auto-correction/date-parse warnings below; the
        /// caller's own, since this method has no provider instance of its own to draw one
        /// from.</param>
        internal static void ParseNfoFile(string path, Book book, MetadataResult<Book> result, ILogger logger, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = LoadXmlDocument(path, logger);
            var root = document.Root;

            if (root == null)
            {
                throw new XmlException($"NFO file '{path}' contains no root element.");
            }

            // Title
            var title = TitleCleaner.StripNonFilingMarkers(GetFirstElementValue(root, TitleTags));
            if (!string.IsNullOrWhiteSpace(title))
            {
                book.Name = title;
            }

            // Description
            var overview = GetFirstElementValue(root, OverviewTags);
            if (!string.IsNullOrWhiteSpace(overview))
            {
                book.Overview = OverviewCleaner.ToPlainText(overview);
            }

            // Release date (try first, possibly derive the year from it)
            var premiereDateText = GetFirstElementValue(root, PremiereDateTags);
            if (!string.IsNullOrWhiteSpace(premiereDateText))
            {
                if (DateTime.TryParse(
                        premiereDateText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var premiereDate))
                {
                    book.PremiereDate = premiereDate;
                    book.ProductionYear = premiereDate.Year;
                }
                else
                {
                    logger.Warn("[Okuzukey] Could not interpret date '{0}' in '{1}'.", premiereDateText, path);
                }
            }

            // An explicit release year overrides a year possibly derived from the date.
            var yearText = GetFirstElementValue(root, YearTags);
            if (!string.IsNullOrWhiteSpace(yearText)
                && int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                && year > 0)
            {
                book.ProductionYear = year;
            }

            // Genres
            foreach (var genre in GetElementValues(root, GenreTags))
            {
                if (!book.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    book.AddGenre(genre);
                }
            }

            // Free-form tags
            foreach (var tag in GetElementValues(root, TagTags))
            {
                if (!book.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    book.SetTags(book.Tags.Concat(new[] { tag }));
                }
            }

            // Publisher / studio
            foreach (var studio in GetElementValues(root, StudioTags))
            {
                book.AddStudio(studio);
            }

            // Authors / other people
            foreach (var authorName in GetElementValues(root, AuthorTags))
            {
                result.People.Add(new PersonInfo
                {
                    Name = authorName,
                    Type = PersonType.Writer,
                    Role = "Author"
                });
            }

            // ISBN
            var isbn = GetFirstElementValue(root, IsbnTags);
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                book.SetProviderId("ISBN", ProviderIdFilter.NormalizeIsbn(isbn));
            }

            // Generic provider IDs in Kodi/XBMC style: <uniqueid type="...">value</uniqueid>
            foreach (var uniqueIdElement in root.Elements()
                         .Where(e => string.Equals(e.Name.LocalName, "uniqueid", StringComparison.OrdinalIgnoreCase)))
            {
                var typeAttribute = uniqueIdElement.Attribute("type");
                var value = uniqueIdElement.Value;

                if (typeAttribute != null
                    && ProviderIdFilter.IsAllowedKey(typeAttribute.Value?.Trim())
                    && !string.IsNullOrWhiteSpace(value))
                {
                    book.SetProviderId(typeAttribute.Value.Trim(), value.Trim());
                }
            }

            // Optional, additional known identifier elements.
            var asin = GetFirstElementValue(root, "asin");
            if (!string.IsNullOrWhiteSpace(asin))
            {
                book.SetProviderId("Asin", NormalizeIdentifier(asin));
            }

            // Community rating
            var ratingText = GetFirstElementValue(root, RatingTags);
            if (!string.IsNullOrWhiteSpace(ratingText)
                && float.TryParse(ratingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                && SafeNumbers.TryToCommunityRating(rating, 10f, out var communityRating))
            {
                book.CommunityRating = communityRating;
            }

            // Series and position within the series

            var volumeText = GetFirstElementValue(root, VolumeTags);
            if (!string.IsNullOrWhiteSpace(volumeText)
                && int.TryParse(volumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume)
                && volume >= 0)
            {
                book.IndexNumber = volume;
            }
        }

        /// <summary>
        /// Removes hyphens and spaces from an identifier (e.g. ISBN, ASIN)
        /// so that later identifier-first searches against remote APIs can work with a normalized
        /// string.
        /// </summary>
        private static string NormalizeIdentifier(string value)
        {
            return value.Replace("-", string.Empty).Replace(" ", string.Empty).Trim();
        }

        /// <summary>
        /// Returns the (trimmed) value of the first matching, non-empty child element of
        /// <paramref name="root"/> whose local name (without namespace prefix) matches one of the
        /// supplied candidate names (case is ignored).
        /// </summary>
        private static string GetFirstElementValue(XElement root, params string[] elementNames)
        {
            foreach (var candidateName in elementNames)
            {
                var element = root.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, candidateName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(e.Value));

                if (element != null)
                {
                    return element.Value.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the (trimmed, duplicate-free) values of all child elements of
        /// <paramref name="root"/> whose local name matches one of the supplied candidate names.
        /// Used for repeatable fields such as genres, tags or authors.
        /// </summary>
        private static IEnumerable<string> GetElementValues(XElement root, params string[] elementNames)
        {
            return root.Elements()
                .Where(e => elementNames.Any(n => string.Equals(e.Name.LocalName, n, StringComparison.OrdinalIgnoreCase))
                            && !string.IsNullOrWhiteSpace(e.Value))
                .Select(e => e.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
