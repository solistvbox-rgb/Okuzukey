using System;
using System.Text.RegularExpressions;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Guards provider IDs taken from free-form file content (NFO <c>&lt;uniqueid type="..."&gt;</c>,
    /// OPF <c>opf:scheme</c>).
    /// </summary>
    internal static class ProviderIdFilter
    {
        private static readonly Regex KeyPattern = new Regex("^[A-Za-z0-9_.:-]{1,40}$", RegexOptions.Compiled);

        /// <summary>
        /// <c>true</c> if <paramref name="key"/> may be stored as a provider ID key: a short,
        /// plain identifier, and not <c>"Komga"</c>, which only <c>KomgaProvider</c> itself sets
        /// after a verified match (a file-supplied Komga id would otherwise be trusted blindly as
        /// "already matched" on every later refresh).
        /// </summary>
        public static bool IsAllowedKey(string key)
        {
            return !string.IsNullOrWhiteSpace(key)
                   && KeyPattern.IsMatch(key)
                   && !string.Equals(key, "Komga", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Strips a <c>urn:isbn:</c>/<c>isbn:</c> prefix (EPUB3 style), hyphens and spaces from an
        /// ISBN value.
        /// </summary>
        public static string NormalizeIsbn(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            foreach (var prefix in new[] { "urn:isbn:", "isbn:" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(prefix.Length);
                    break;
                }
            }

            return trimmed.Replace("-", string.Empty).Replace(" ", string.Empty).Trim();
        }

        /// <summary><c>true</c> if <paramref name="value"/> is an EPUB3-style <c>urn:isbn:</c> identifier.</summary>
        public static bool IsIsbnUrn(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Trim().StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
