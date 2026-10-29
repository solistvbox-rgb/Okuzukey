using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Session;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Shows a modal dialog (with an OK button, not a self-dismissing toast) to every logged-in
    /// administrator session when Komga is unreachable or rejects the API key.
    /// </summary>
    /// <remarks>
    /// Uses Emby's own "send message to device" mechanism (<c>ISessionManager.SendMessageCommand</c>
    /// with a <see cref="MessageCommand"/>): the web client's <c>DisplayMessage</c> handler shows a
    /// toast only when <c>TimeoutMs</c> is set and an OK dialog otherwise, so it is deliberately
    /// left unset. Added because a metadata refresh with an offline Komga "succeeds" but silently
    /// yields no Komga data, and most people never read the log. The scan provider has no context
    /// of who started the refresh, so the message goes to all administrator sessions. Fire-and-forget
    /// and fully exception-safe: it must never block or fail a library scan. Only sessions that
    /// advertise the <c>DisplayMessage</c> command receive it (the web client does), so a scan
    /// running with no admin logged in simply shows nothing.
    /// </remarks>
    internal static class AdminNotifier
    {
        private static int _notEnabledReported;

        /// <summary>
        /// Komga is selected as a provider in a library but switched off (or left without URL/API key)
        /// in this plugin, so Emby calls the provider and every call is silently skipped. Reported
        /// once until the settings are saved again or the server restarts; the opposite case (enabled
        /// here, not selected in the library) never reaches the provider and is deliberately silent.
        /// </summary>
        /// <returns><c>true</c> if this call sent the notification (first time since the last reset).</returns>
        public static bool KomgaNotEnabled()
        {
            if (System.Threading.Interlocked.Exchange(ref _notEnabledReported, 1) != 0)
            {
                return false;
            }

            Send(
                "Okuzukey - Komga is not enabled",
                "Komga is selected as a metadata/image provider in a library, but the Komga provider is disabled (or the URL/API key is missing) in the Okuzukey plugin settings. "
                + "Komga metadata/covers are skipped. Please enable it and enter URL and API key in the plugin settings, or untick Komga in the library settings.");
            return true;
        }

        /// <summary>Re-arms <see cref="KomgaNotEnabled"/> after the plugin settings were saved.</summary>
        public static void ResetNotEnabledNotice()
        {
            System.Threading.Volatile.Write(ref _notEnabledReported, 0);
        }

        public static void KomgaUnreachable()
        {
            Send(
                "Okuzukey - Komga not reachable",
                "Komga server '" + GetDisplayUrl() + "' is not reachable. Komga metadata/covers are skipped until it is back online. "
                + "Please check that the server is running and the URL in the plugin settings is correct, then run the refresh again.");
        }

        public static void KomgaApiKeyRejected()
        {
            Send(
                "Okuzukey - Komga API key rejected",
                "Komga server '" + GetDisplayUrl() + "' rejected the API key (HTTP 401/403). Komga metadata/covers are skipped. "
                + "Please check the API key in the plugin settings (Komga web UI: My Account > API Keys), then run the refresh again.");
        }

        /// <summary>The configured Komga URL without any embedded <c>user:password@</c> part.</summary>
        private static string GetDisplayUrl()
        {
            var configured = Plugin.Instance?.Configuration?.KomgaBaseUrl;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return "(not configured)";
            }

            try
            {
                var builder = new UriBuilder(configured.Trim()) { UserName = string.Empty, Password = string.Empty };
                return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/');
            }
            catch (UriFormatException)
            {
                return configured.Trim();
            }
        }

        private static void Send(string header, string text)
        {
            Task.Run(() =>
            {
                try
                {
                    var plugin = Plugin.Instance;
                    var sessionManager = plugin?.SessionManager;
                    var userManager = plugin?.UserManager;
                    if (sessionManager == null || userManager == null)
                    {
                        return;
                    }

                    var command = new MessageCommand { Header = header, Text = text };

                    foreach (var session in sessionManager.Sessions.ToList())
                    {
                        try
                        {
                            if (!session.SupportsRemoteControl
                                || string.IsNullOrEmpty(session.UserId)
                                || session.SupportedCommands == null
                                || !session.SupportedCommands.Contains("DisplayMessage"))
                            {
                                continue;
                            }

                            var user = userManager.GetUserById(session.UserId);
                            if (user?.Policy == null || !user.Policy.IsAdministrator)
                            {
                                continue;
                            }

                            sessionManager.SendMessageCommand(session.Id, session.Id, command, CancellationToken.None)
                                .GetAwaiter().GetResult();
                        }
                        catch
                        {
                            // One unreachable/odd session must not stop the others.
                        }
                    }
                }
                catch
                {
                    // Best-effort - never let a notification problem affect a library scan.
                }
            });
        }
    }
}
