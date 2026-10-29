using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Converts an overview/description that arrives as HTML (or with Markdown heading leftovers)
    /// into plain text before it is handed to Emby, which displays <c>Overview</c> as plain text.
    /// </summary>
    /// <remarks>
    /// A Calibre-managed EPUB stores its description as HTML in <c>dc:description</c>
    /// (XML-escaped in the OPF, so it arrives as literal
    /// <c>&lt;div&gt;&lt;p class="description"&gt;...&lt;/p&gt;&lt;/div&gt;</c> after parsing), and
    /// Emby's book overview list would show those raw tags. Deliberately conservative and dependency-free:
    /// only well-formed-looking tags (<c>&lt;name ...&gt;</c>/<c>&lt;/name&gt;</c>) are removed, so a
    /// stray "a &lt; b" in real text survives.
    /// </remarks>
    internal static class OverviewCleaner
    {
        private static readonly Regex LineBreakTagPattern = new Regex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BlockEndTagPattern = new Regex(@"<\s*/\s*(p|div|li|h[1-6]|tr|blockquote)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnyTagPattern = new Regex(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

        /// <summary>Comments, <c>&lt;!DOCTYPE&gt;</c>/<c>&lt;![CDATA[</c> markers and processing instructions.</summary>
        private static readonly Regex SpecialMarkupPattern = new Regex(@"<!--.*?-->|<![^>]*>|<\?.*?\?>", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AnyWhitespacePattern = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Upper bound for strip/decode rounds. Each round can only unwrap one level of entity
        /// encoding, so this covers text encoded up to this many times; anything deeper stays as
        /// visible entity text (<c>&amp;lt;...</c>), which is harmless.
        /// </summary>
        private const int MaxStripRounds = 5;

        private static readonly Regex MarkdownHeadingPattern = new Regex(@"^[ \t]*#{1,6}[ \t]+", RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex HorizontalWhitespacePattern = new Regex(@"[ \t ]+", RegexOptions.Compiled);

        private static readonly Regex ExcessNewlinesPattern = new Regex(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>
        /// Returns <paramref name="text"/> as plain text: block/line-break tags become newlines,
        /// all other tags are dropped, HTML entities are decoded, leading Markdown heading markers
        /// (<c>## </c>) are removed, and whitespace is normalized. <c>null</c>/blank input is
        /// returned unchanged.
        /// </summary>
        public static string ToPlainText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var result = StripMarkup(text.Replace("\r\n", "\n").Replace('\r', '\n'));

            result = MarkdownHeadingPattern.Replace(result, string.Empty);
            result = HorizontalWhitespacePattern.Replace(result, " ");

            var lines = result.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].Trim();
            }

            result = string.Join("\n", lines);
            result = ExcessNewlinesPattern.Replace(result, "\n\n").Trim();

            return result;
        }

        /// <summary>
        /// Plain text for short, single-line fields (title, genre, studio, tag, person name):
        /// same markup removal as <see cref="ToPlainText"/>, then all whitespace (including line
        /// breaks) collapsed to single spaces. <c>null</c>/blank input is returned unchanged.
        /// </summary>
        public static string ToPlainTextLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            return AnyWhitespacePattern.Replace(StripMarkup(text), " ").Trim();
        }

        /// <summary>
        /// Removes tags and decodes entities, repeated until nothing changes any more. Before,
        /// tags were removed once and entities decoded afterwards, so double-encoded markup such as
        /// <c>&amp;lt;img src=x onerror=...&amp;gt;</c> (from a crafted EPUB/NFO/ComicInfo.xml or a
        /// Komga summary) would come out as a real <c>&lt;img onerror&gt;</c> tag in the overview.
        /// Emby's web client filters it, but not every client necessarily does.
        /// </summary>
        private static string StripMarkup(string text)
        {
            var result = text;
            for (var round = 0; round < MaxStripRounds; round++)
            {
                var before = result;
                result = SpecialMarkupPattern.Replace(result, string.Empty);
                result = LineBreakTagPattern.Replace(result, "\n");
                result = BlockEndTagPattern.Replace(result, "\n");
                result = AnyTagPattern.Replace(result, string.Empty);
                result = WebUtility.HtmlDecode(result);

                if (string.Equals(before, result, StringComparison.Ordinal))
                {
                    break;
                }
            }

            // Anything that still looks like a tag after the last round (only possible for text
            // encoded more often than MaxStripRounds) is removed once more without decoding.
            return AnyTagPattern.Replace(result, string.Empty);
        }
    }
}
