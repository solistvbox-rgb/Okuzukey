using System.Text.RegularExpressions;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Strips the "Nichtsortierzeichen" (non-filing character) some German-language library
    /// catalog exports embed directly in a title's plain text, e.g. <c>¬Die¬ Welt von oben</c>
    /// instead of <c>Die Welt von oben</c>.
    /// </summary>
    /// <remarks>
    /// This is the RAK/MAB convention (still seen in Calibre/OPF and NFO exports sourced from a
    /// German library catalog) for marking a leading article that should be ignored when sorting
    /// titles alphabetically. Unlike MARC21 (which uses a separate numeric indicator field, never
    /// embedded in the title text itself), formats with a plain-text-only title field have no
    /// other place to put this marker, so the character <c>¬</c> (U+00AC, NOT SIGN) ends up
    /// directly in the displayed title unless stripped.
    /// </remarks>
    internal static class TitleCleaner
    {
        private const char NonFilingMarker = '¬';

        /// <summary>
        /// Removes every <c>¬</c> from <paramref name="title"/> and trims the result, or returns
        /// <paramref name="title"/> unchanged if it is <c>null</c>/empty or contains no such
        /// character.
        /// </summary>
        public static string StripNonFilingMarkers(string title)
        {
            if (string.IsNullOrEmpty(title) || title.IndexOf(NonFilingMarker) < 0)
            {
                return title;
            }

            return title.Replace(NonFilingMarker.ToString(), string.Empty).Trim();
        }

        /// <summary>
        /// Matches a single non-nested <c>(...)</c>/<c>[...]</c>/<c>{...}</c> group, used by
        /// <see cref="GuessTitleFromFileName"/> to strip scene/scanlation-style tags (publisher,
        /// year, release group) from a comic/manga file name.
        /// </summary>
        private static readonly Regex BracketGroupPattern = new Regex(@"[\(\[\{][^\(\)\[\]\{\}]*[\)\]\}]", RegexOptions.Compiled);

        private static readonly Regex WhitespacePattern = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Best-effort "clean" title guessed directly from a comic/manga archive's file name, for
        /// <see cref="Providers.Local.ComicInfoProvider"/>'s last-resort fallback when there is no
        /// embedded <c>ComicInfo.xml</c> and no sidecar OPF/NFO either.
        /// </summary>
        /// <remarks>
        /// Scene/scanlation-style comic file names commonly look like
        /// <c>Title_NN_(Publisher,_Year)_(group)(tag).cbz</c>: underscores standing in for
        /// spaces, with one or more trailing parenthesized/bracketed tags that are not part of
        /// the title. Emby's own generic file-name parser (tuned for movie/TV naming, not this
        /// convention) can badly mis-parse such names, producing a garbled title
        /// like <c>"_(c"</c> for
        /// <c>Some_Title_01_(Publisher,_2008)_(c2c)(group).cbz</c>. This heuristic
        /// only replaces underscores with spaces and strips every bracketed/parenthesized group
        /// (wherever it occurs, not just at the end) - it deliberately does not touch periods
        /// (an abbreviation like <c>S.H.I.E.L.D.</c> must survive intact) or try to separately extract
        /// an issue/volume number.
        /// </remarks>
        /// <param name="fileNameWithoutExtension">The archive's file name, without its
        /// extension.</param>
        /// <returns>The cleaned title, or <c>null</c> if nothing meaningful remains.</returns>
        public static string GuessTitleFromFileName(string fileNameWithoutExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            {
                return null;
            }

            var cleaned = fileNameWithoutExtension.Replace('_', ' ');

            string previous;
            do
            {
                previous = cleaned;
                cleaned = BracketGroupPattern.Replace(cleaned, " ");
            }
            while (cleaned != previous);

            cleaned = WhitespacePattern.Replace(cleaned, " ").Trim();

            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }
    }
}
