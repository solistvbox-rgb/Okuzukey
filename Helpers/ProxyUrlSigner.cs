using System;
using System.Security.Cryptography;
using System.Text;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Signs and verifies the parameters of this plugin's own unauthenticated image-proxy URLs
    /// (<c>Api/EmbeddedCoverApi.cs</c>, <c>Api/KomgaImageApi.cs</c>), so those endpoints only
    /// ever serve requests for URLs the plugin itself generated.
    /// </summary>
    /// <remarks>
    /// Both endpoints must stay <c>[Unauthenticated]</c>, because Emby's own internal image
    /// downloader attaches no credential. Without a signature, anyone able to reach the Emby port
    /// could pass an arbitrary file path (including a UNC path, which makes a Windows host authenticate to a foreign SMB server) or
    /// an arbitrary Komga book id. With an HMAC-SHA256 signature over the parameter, a URL can
    /// only come from <c>GetImages</c>/<c>GetSearchResults</c> inside this process. The key is
    /// random per server process and never persisted.
    /// <para>
    /// The signature alone is not enough, though: Emby does not
    /// always download a remote image right away - it may store the URL on the item and download
    /// it on first display (<c>ConvertImageToLocal</c>), possibly after a restart, when the key
    /// has changed; and URLs stored by older plugin versions carry no signature at all. Such
    /// covers would come back 403 and stay broken. <see cref="IsAuthorizedEmbeddedCover"/>/
    /// <see cref="IsAuthorizedKomgaBook"/> therefore accept a request without a valid signature if
    /// the path (or Komga id) belongs to a book in an Emby library. That is still safe: a foreign
    /// path such as <c>\\attacker\share\x.cbz</c> or <c>C:\Windows\...</c> is in no library, and
    /// the lookup only queries Emby's database, it never touches the path itself.
    /// </para>
    /// </remarks>
    internal static class ProxyUrlSigner
    {
        /// <summary>
        /// <c>true</c> if an embedded-cover request for <paramref name="path"/> may be served:
        /// either the signature is valid, or the path is that of a book in an Emby library.
        /// </summary>
        public static bool IsAuthorizedEmbeddedCover(string path, string signature)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (Verify(EmbeddedCoverExtractor.SignaturePurpose, path, signature))
            {
                return true;
            }

            try
            {
                return Plugin.Instance?.LibraryManager?.FindByPath(path, false) is MediaBrowser.Controller.Entities.Book;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// <c>true</c> if a Komga cover request for <paramref name="bookId"/> may be served: either
        /// the signature is valid, or a book in an Emby library carries that Komga id.
        /// </summary>
        public static bool IsAuthorizedKomgaBook(string bookId, string signature)
        {
            if (string.IsNullOrWhiteSpace(bookId))
            {
                return false;
            }

            if (Verify(Providers.Remote.KomgaProvider.SignaturePurpose, bookId, signature))
            {
                return true;
            }

            try
            {
                var libraryManager = Plugin.Instance?.LibraryManager;
                if (libraryManager == null)
                {
                    return false;
                }

                var matches = libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Book" },
                    Recursive = true,
                    Limit = 1,
                    AnyProviderIdEquals = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
                    {
                        new System.Collections.Generic.KeyValuePair<string, string>("Komga", bookId)
                    }
                });

                return matches != null && matches.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
        private static readonly byte[] Key = CreateKey();

        private static byte[] CreateKey()
        {
            var key = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }

            return key;
        }

        /// <summary>Returns a URL-safe signature for <paramref name="purpose"/> + <paramref name="value"/>.</summary>
        public static string Sign(string purpose, string value)
        {
            using (var hmac = new HMACSHA256(Key))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose + "\n" + (value ?? string.Empty)));
                return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        /// <summary>Constant-time check that <paramref name="signature"/> matches <paramref name="value"/>.</summary>
        public static bool Verify(string purpose, string value, string signature)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(signature))
            {
                return false;
            }

            var expected = Sign(purpose, value);
            if (expected.Length != signature.Length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                difference |= expected[i] ^ signature[i];
            }

            return difference == 0;
        }

        /// <summary>
        /// Returns the unescaped value of query parameter <paramref name="name"/> in
        /// <paramref name="url"/>, or <c>null</c> if the URL is invalid or has no such parameter.
        /// </summary>
        public static string GetQueryValue(string url, string name)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves Emby's own API base URL on the loopback interface (the downloader that fetches
        /// the proxy URL runs in this same process), falling back to Emby's default local URL if
        /// the loopback variant is unavailable. Returns <c>null</c> without a plugin instance.
        /// </summary>
        public static async System.Threading.Tasks.Task<string> GetLocalApiBaseUrlAsync(System.Threading.CancellationToken cancellationToken)
        {
            var applicationHost = Plugin.Instance?.ApplicationHost;
            if (applicationHost == null)
            {
                return null;
            }

            string url = null;
            try
            {
                url = applicationHost.GetLocalApiUrl("127.0.0.1");
            }
            catch (Exception)
            {
                // Fall through to the default local URL below.
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                url = await applicationHost.GetLocalApiUrl(cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        }
    }
}
