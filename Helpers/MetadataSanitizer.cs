using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Final cleanup every metadata provider applies to its result before handing it to Emby:
    /// strips HTML/markup from all short text fields and the overview.
    /// </summary>
    /// <remarks>
    /// Metadata files are untrusted input (an EPUB, CBZ or NFO can come from anywhere), so
    /// titles, genres and person names could otherwise arrive in Emby with raw markup such as
    /// <c>&lt;img src=x onerror=...&gt;</c>. Emby's web client escapes them, but it looks broken
    /// and other clients might not escape. Doing this once at the end of each
    /// provider instead of at every single assignment means a newly added field cannot be
    /// forgotten.
    /// </remarks>
    internal static class MetadataSanitizer
    {
        public static void Sanitize(BaseItem item, List<PersonInfo> people)
        {
            if (item != null)
            {
                item.Name = OverviewCleaner.ToPlainTextLine(item.Name);
                item.Overview = OverviewCleaner.ToPlainText(item.Overview);
                item.Genres = CleanList(item.Genres);
                item.Studios = CleanList(item.Studios);
                item.SetTags(CleanList(item.Tags));

                if (item.ProviderIds != null)
                {
                    foreach (var key in item.ProviderIds.Keys.ToList())
                    {
                        var value = OverviewCleaner.ToPlainTextLine(item.ProviderIds[key]);
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            item.ProviderIds.Remove(key);
                        }
                        else
                        {
                            item.ProviderIds[key] = value;
                        }
                    }
                }
            }

            if (people != null)
            {
                foreach (var person in people)
                {
                    person.Name = OverviewCleaner.ToPlainTextLine(person.Name);
                    person.Role = OverviewCleaner.ToPlainTextLine(person.Role);
                }

                people.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name));
            }
        }

        private static string[] CleanList(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(OverviewCleaner.ToPlainTextLine)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
