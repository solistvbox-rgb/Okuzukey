using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
using SharpCompress.Archives.Rar;

namespace Emby.Okuzukey.Providers.Local
{
    /// <summary>
    /// Local metadata provider that reads the embedded <c>ComicInfo.xml</c> (the de-facto
    /// standard metadata format written by ComicRack, ComicTagger and similar tools) from
    /// <c>.cbz</c> (ZIP) and <c>.cbr</c> (RAR) comic/manga archives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>.cbz</c> is read with the .NET BCL's <see cref="ZipArchive"/> (no extra dependency).
    /// <c>.cbr</c> needs a RAR reader, which .NET has none of built in; the plugin relies on
    /// Emby Server's own bundled <c>SharpCompress</c> (<see cref="RarArchive"/>) rather than
    /// shipping a copy of its own, compiled against the exact version Emby ships to avoid a
    /// version-mismatch load failure.
    /// </para>
    /// <para>
    /// Only files with extension <c>.cbz</c>/<c>.cbr</c> are considered; this provider does not
    /// apply to any other book format. A missing <c>ComicInfo.xml</c> inside the archive, a
    /// corrupt/unsupported archive, or a malformed <c>ComicInfo.xml</c> never aborts the library
    /// scan and never discards data already gathered from elsewhere (see the next paragraph).
    /// </para>
    /// <para>
    /// <b>Sidecar OPF/NFO gap-filling merge:</b> before reading <c>ComicInfo.xml</c>, <see cref="GetMetadata"/> first applies
    /// any sidecar Calibre OPF or generic NFO found next to the item (via
    /// <see cref="Helpers.SidecarMetadataReader"/>, reusing
    /// <see cref="OpfBookProvider.ParseOpfDocument"/>/<see cref="NfoBookProvider.ParseNfoFile"/>
    /// directly). <c>ComicInfo.xml</c> is then applied on top of the same <see cref="Book"/>:
    /// it overwrites any field it also has (the more specific, comic-native source wins on
    /// overlap), but a field only the sidecar provided (e.g. <c>Overview</c>, an ISBN) survives.
    /// This matters most when an admin has disabled the OPF/NFO readers for a library (Emby's
    /// "Metadata Readers" list) - with them disabled, Emby never calls them at all, so without
    /// this a real sidecar file would sit unused right next to the comic. If neither source has a
    /// title, one is guessed from the file name as a last resort (see
    /// <see cref="TitleCleaner.GuessTitleFromFileName"/>).
    /// </para>
    /// <para>
    /// Highest order value (tried last) of the three local metadata readers - see
    /// <see cref="Order"/> - since a comic archive's own embedded <c>ComicInfo.xml</c> is tried
    /// only after a Calibre-style OPF sidecar and a generic NFO sidecar have both been checked
    /// and found nothing. (This
    /// provider's own sidecar check above only matters when one or both of those are disabled.)
    /// </para>
    /// </remarks>
    public class ComicInfoProvider : ILocalMetadataProvider<Book>, IHasOrder
    {
        /// <summary>
        /// Maps a <c>ComicInfo.xml</c> credit element name to the descriptive role stored in
        /// <see cref="PersonInfo.Role"/> (all comma-separated lists of names).
        /// </summary>
        private static readonly Dictionary<string, string> CreditElementRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Writer", "Writer" },
            { "Penciller", "Penciller" },
            { "Inker", "Inker" },
            { "Colorist", "Colorist" },
            { "Letterer", "Letterer" },
            { "CoverArtist", "Cover Artist" },
            { "Editor", "Editor" },
            { "Translator", "Translator" }
        };

        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="ComicInfoProvider"/>.
        /// </summary>
        /// <param name="fileSystem">File system access injected by Emby, used for the sidecar
        /// OPF/NFO gap-filling merge (see <see cref="GetMetadata"/>) applied before the archive's
        /// own <c>ComicInfo.xml</c>.</param>
        /// <param name="logger">Logger injected by Emby, named for this provider.</param>
        public ComicInfoProvider(IFileSystem fileSystem, ILogger logger)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = FilteredLogger.Wrap(logger ?? throw new ArgumentNullException(nameof(logger)));
        }

        /// <inheritdoc />
        public string Name => "Okuzukey ComicInfo (CBZ/CBR)";

        /// <summary>
        /// Highest order value of the three local metadata readers - below <c>OpfBookProvider</c>
        /// (0) and <c>NfoBookProvider</c> (1) - tried last since a comic archive's own embedded
        /// <c>ComicInfo.xml</c> is typically less complete than a Calibre-style OPF sidecar. Still
        /// well below all remote providers (e.g. Komga). This is only
        /// the default seed order Emby's dashboard ("Metadata Readers" under library settings)
        /// presents on first setup; an admin can always drag-reorder it per library from there,
        /// which then takes precedence.
        /// </summary>
        public int Order => 2;

        /// <inheritdoc />
        public Task<MetadataResult<Book>> GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Book> { HasMetadata = false, People = new List<PersonInfo>() };

            if (string.IsNullOrEmpty(info.Path))
            {
                return Task.FromResult(result);
            }

            var extension = Path.GetExtension(info.Path);
            var isCbz = string.Equals(extension, ".cbz", StringComparison.OrdinalIgnoreCase);
            var isCbr = string.Equals(extension, ".cbr", StringComparison.OrdinalIgnoreCase);

            if (!isCbz && !isCbr)
            {
                return Task.FromResult(result);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var book = new Book();

            // 1. A sidecar OPF/NFO (if any) is applied first, filling whatever fields it has.
            // 2. The archive's own ComicInfo.xml (below) is applied on top of the same Book:
            //    it overwrites any field it also has (ComicInfo.xml is the more specific, comic-
            //    native source), but a field only the sidecar provided (e.g. Overview, an ISBN)
            //    survives untouched.
            var hasSidecarData = SidecarMetadataReader.TryApplyFullSidecarMetadata(_fileSystem, info.Path, book, result, _logger, cancellationToken);

            var hasComicInfoData = false;
            try
            {
                var xmlText = isCbz ? ReadComicInfoFromCbz(info.Path) : ReadComicInfoFromCbr(info.Path);
                if (string.IsNullOrWhiteSpace(xmlText))
                {
                    _logger.Debug("[Okuzukey] No ComicInfo.xml found in '{0}'.", info.Path);
                }
                else
                {
                    ParseComicInfo(xmlText, info.Path, book, result);
                    hasComicInfoData = true;
                    _logger.Info("[Okuzukey] Metadata successfully read from ComicInfo.xml in '{0}'.", info.Path);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (XmlException ex)
            {
                // Deliberately does not touch book/result any further: a malformed ComicInfo.xml
                // must not discard sidecar data already applied above.
                _logger.ErrorException(
                    "[Okuzukey] Invalid ComicInfo.xml in '{0}'; ignored, any sidecar OPF/NFO data found is kept.",
                    ex,
                    info.Path);
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly: a corrupt/unsupported archive or a faulty
                // ComicInfo.xml must under no circumstances abort the library scan, nor discard
                // sidecar data already applied above.
                _logger.ErrorException(
                    "[Okuzukey] Could not read ComicInfo.xml from '{0}'; ignored, any sidecar OPF/NFO data found is kept.",
                    ex,
                    info.Path);
            }

            // 3. Last resort: guess the title from the file name if nothing above set one.
            if (string.IsNullOrWhiteSpace(book.Name))
            {
                var guessedTitle = TitleCleaner.GuessTitleFromFileName(Path.GetFileNameWithoutExtension(info.Path));
                if (!string.IsNullOrWhiteSpace(guessedTitle))
                {
                    book.Name = guessedTitle;
                    _logger.Info("[Okuzukey] No ComicInfo.xml or sidecar OPF/NFO for '{0}'; guessed the title from the file name.", info.Path);
                }
            }

            MetadataSanitizer.Sanitize(book, result.People);
            result.HasMetadata = hasSidecarData || hasComicInfoData || !string.IsNullOrWhiteSpace(book.Name);
            result.Item = result.HasMetadata ? book : null;

            return Task.FromResult(result);
        }

        /// <summary>
        /// Reads <c>ComicInfo.xml</c> out of a <c>.cbz</c> (ZIP) archive, or <c>null</c> if the
        /// archive contains no such entry.
        /// </summary>
        private static string ReadComicInfoFromCbz(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(GetArchiveEntryFileName(e.FullName), "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    return null;
                }

                BoundedReader.ThrowIfTooLarge(entry.Length, BoundedReader.MaxXmlBytes);
                using (var stream = entry.Open())
                {
                    return BoundedReader.ReadAllText(stream, BoundedReader.MaxXmlBytes);
                }
            }
        }

        /// <summary>
        /// Reads <c>ComicInfo.xml</c> out of a <c>.cbr</c> (RAR) archive via SharpCompress, or
        /// <c>null</c> if the archive contains no such entry.
        /// </summary>
        private static string ReadComicInfoFromCbr(string path)
        {
            using (var archive = RarArchive.Open(path))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory
                    && string.Equals(GetArchiveEntryFileName(e.Key), "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    return null;
                }

                BoundedReader.ThrowIfTooLarge(entry.Size, BoundedReader.MaxXmlBytes);
                using (var stream = entry.OpenEntryStream())
                {
                    return BoundedReader.ReadAllText(stream, BoundedReader.MaxXmlBytes);
                }
            }
        }

        /// <summary>
        /// Returns the file name portion of an archive entry path, splitting on both <c>/</c>
        /// (always used by ZIP) and <c>\</c> (used by some RAR writers), unlike
        /// <see cref="Path.GetFileName(string)"/>, which only recognizes <c>\</c> on Windows.
        /// </summary>
        private static string GetArchiveEntryFileName(string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath))
            {
                return entryPath;
            }

            var separatorIndex = entryPath.LastIndexOfAny(new[] { '/', '\\' });
            return separatorIndex >= 0 ? entryPath.Substring(separatorIndex + 1) : entryPath;
        }

        /// <summary>
        /// Reads the given <c>ComicInfo.xml</c> content and transfers the recognized fields to
        /// <paramref name="book"/> and <paramref name="result"/>.
        /// </summary>
        private void ParseComicInfo(string xmlText, string archivePath, Book book, MetadataResult<Book> result)
        {
            var document = XmlSanitizer.ParseWithAmpersandFallback(xmlText, archivePath + " (ComicInfo.xml)", _logger);
            var root = document.Root;

            if (root == null)
            {
                throw new XmlException($"ComicInfo.xml in '{archivePath}' contains no root element.");
            }

            // Title
            var title = TitleCleaner.StripNonFilingMarkers(GetElementValue(root, "Title"));
            if (!string.IsNullOrWhiteSpace(title))
            {
                book.Name = title;
            }

            // Description. Series is intentionally not read into a field: like the other
            // Okuzukey providers, it is only ever used for remote search text, never stored,
            // since Book has no series-name field.
            var summary = GetElementValue(root, "Summary");
            if (!string.IsNullOrWhiteSpace(summary))
            {
                book.Overview = OverviewCleaner.ToPlainText(summary);
            }

            // Release date: Year is required for a date to be set at all; Month/Day default to
            // 1 when missing or out of range, rather than dropping the whole date.
            var yearText = GetElementValue(root, "Year");
            if (!string.IsNullOrWhiteSpace(yearText)
                && int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                && year > 0)
            {
                book.ProductionYear = year;

                int.TryParse(GetElementValue(root, "Month"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var month);
                int.TryParse(GetElementValue(root, "Day"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day);

                try
                {
                    book.PremiereDate = new DateTime(
                        year,
                        month >= 1 && month <= 12 ? month : 1,
                        day >= 1 && day <= 31 ? day : 1,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // E.g. Day=31 for a 30-day month - keep just the year that was already set.
                }
            }

            // Issue number / volume -> IndexNumber. Number (the specific issue/chapter number)
            // takes precedence over Volume (used by collected editions that only carry a
            // volume number), analogous to NfoBookProvider's VolumeTags priority.
            var numberText = GetElementValue(root, "Number");
            if (!string.IsNullOrWhiteSpace(numberText)
                && double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && SafeNumbers.TryToIndexNumber(number, out var indexNumber))
            {
                book.IndexNumber = indexNumber;
            }
            else
            {
                var volumeText = GetElementValue(root, "Volume");
                if (!string.IsNullOrWhiteSpace(volumeText)
                    && int.TryParse(volumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume)
                    && volume >= 0)
                {
                    book.IndexNumber = volume;
                }
            }

            // Publisher / Imprint -> Studio
            foreach (var studio in new[] { GetElementValue(root, "Publisher"), GetElementValue(root, "Imprint") }
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                book.AddStudio(studio);
            }

            // Genre (comma-separated)
            foreach (var genre in SplitCommaSeparated(GetElementValue(root, "Genre")))
            {
                if (!book.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    book.AddGenre(genre);
                }
            }

            // Free-form tags: the explicit Tags list, Characters/Teams/Locations (Book has no
            // character field), plus a few fields turned into descriptive tags since Book has no dedicated field for them.
            var tags = new List<string>();
            tags.AddRange(SplitCommaSeparated(GetElementValue(root, "Tags")));
            tags.AddRange(SplitCommaSeparated(GetElementValue(root, "Characters")));
            tags.AddRange(SplitCommaSeparated(GetElementValue(root, "Teams")));
            tags.AddRange(SplitCommaSeparated(GetElementValue(root, "Locations")));

            var languageIso = GetElementValue(root, "LanguageISO");
            if (!string.IsNullOrWhiteSpace(languageIso))
            {
                tags.Add("Language: " + languageIso);
            }

            var format = GetElementValue(root, "Format");
            if (!string.IsNullOrWhiteSpace(format))
            {
                tags.Add("Format: " + format);
            }

            var ageRating = GetElementValue(root, "AgeRating");
            if (!string.IsNullOrWhiteSpace(ageRating) && !string.Equals(ageRating, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add("Age Rating: " + ageRating);
            }

            if (string.Equals(GetElementValue(root, "BlackAndWhite"), "Yes", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add("Black & White");
            }

            var manga = GetElementValue(root, "Manga");
            if (string.Equals(manga, "Yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(manga, "YesAndRightToLeft", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add("Manga");
                if (string.Equals(manga, "YesAndRightToLeft", StringComparison.OrdinalIgnoreCase))
                {
                    tags.Add("Right to Left");
                }
            }

            foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!book.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    book.SetTags(book.Tags.Concat(new[] { tag }));
                }
            }

            // Credits: Writer/Penciller/Inker/Colorist/Letterer/CoverArtist/Editor/Translator,
            // each a comma-separated list of names.
            foreach (var creditElementName in CreditElementRoles.Keys)
            {
                var role = CreditElementRoles[creditElementName];
                foreach (var name in SplitCommaSeparated(GetElementValue(root, creditElementName)))
                {
                    result.People.Add(new PersonInfo
                    {
                        Name = name,
                        Type = PersonType.Writer,
                        Role = role
                    });
                }
            }

            // CommunityRating: not part of the original ComicInfo schema, but a common
            // extension written by some tools on a 0-5 scale; scaled to Emby's 0-10 convention
            // by doubling it.
            var communityRatingText = GetElementValue(root, "CommunityRating");
            if (!string.IsNullOrWhiteSpace(communityRatingText)
                && float.TryParse(communityRatingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var communityRating)
                && SafeNumbers.TryToCommunityRating(communityRating, 5f, out var scaledRating))
            {
                book.CommunityRating = scaledRating;
            }

            // GTIN (v2.1 barcode extension): only trusted as an ISBN if it structurally looks
            // like one (13 digits, standard ISBN-13 EAN prefix) - a plain UPC (common for
            // single issues) must not be mistaken for an ISBN and misdirect a later
            // identifier-first remote search.
            var gtin = GetElementValue(root, "GTIN");
            if (!string.IsNullOrWhiteSpace(gtin))
            {
                var digitsOnly = new string(gtin.Where(char.IsDigit).ToArray());
                if (digitsOnly.Length == 13 && (digitsOnly.StartsWith("978", StringComparison.Ordinal) || digitsOnly.StartsWith("979", StringComparison.Ordinal)))
                {
                    book.SetProviderId("ISBN", digitsOnly);
                }
            }
        }

        /// <summary>
        /// Returns the (trimmed) value of the first child element of <paramref name="root"/>
        /// whose local name matches <paramref name="elementName"/> (case-insensitive), or
        /// <c>null</c> if none was found.
        /// </summary>
        private static string GetElementValue(XElement root, string elementName)
        {
            var element = root.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Value));

            return element?.Value.Trim();
        }

        /// <summary>
        /// Splits a comma-separated <c>ComicInfo.xml</c> list value (e.g. <c>Genre</c>,
        /// <c>Tags</c>, <c>Writer</c>) into trimmed, non-empty parts.
        /// </summary>
        private static IEnumerable<string> SplitCommaSeparated(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Enumerable.Empty<string>();
            }

            return value.Split(',')
                .Select(v => v.Trim())
                .Where(v => v.Length > 0);
        }
    }
}
