using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Emby.Okuzukey.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Okuzukey
{
    /// <summary>
    /// Main entry point of the Okuzukey plugin for Emby Server.
    /// </summary>
    /// <remarks>
    /// This class only handles the plugin registration itself:
    /// persistence of the <see cref="PluginConfiguration"/> (inherited from
    /// <see cref="BasePlugin{TConfigurationType}"/>) and the registration of the
    /// dashboard configuration page via <see cref="IHasWebPages"/>.
    /// <para>
    /// The actual metadata/image providers - five local ones (NFO, Calibre OPF, embedded
    /// ComicInfo.xml, local cover images, embedded cover images) plus one optional remote one for
    /// the user's own self-hosted Komga server - are implemented as standalone classes that Emby
    /// discovers through the respective provider interfaces
    /// (<c>ILocalMetadataProvider&lt;Book&gt;</c>, <c>ILocalImageProvider</c>,
    /// <c>IRemoteMetadataProvider&lt;Book, BookInfo&gt;</c>, <c>IRemoteImageProvider</c>) and
    /// wires up automatically via MEF/DI. They are deliberately not part of this class.
    /// </para>
    /// <para>
    /// Komga is the only remote source: it is the user's own server, not a public database,
    /// opt-in and disabled by default.
    /// </para>
    /// </remarks>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        /// <summary>
        /// Unique, fixed plugin ID.
        /// </summary>
        /// <remarks>
        /// IMPORTANT: This GUID must not be changed after the first productive use, because Emby
        /// uses it to find the stored <see cref="PluginConfiguration"/>
        /// and provider assignments in the library cache again. Changing it
        /// would make this a completely new plugin from Emby's point of view.
        /// </remarks>
        public override Guid Id => new Guid("8f3b6a2e-8e1d-4a2b-9c3a-1f7e6d5c4b3a");

        /// <inheritdoc />
        public override string Name => "Okuzukey";

        /// <inheritdoc />
        public override string Description =>
            "Metadata plugin for books, comics, manga and light novels. Reads local metadata " +
            "(NFO, Calibre OPF, ComicInfo.xml embedded in CBZ/CBR, local/embedded cover images) " +
            "and, optionally, your own self-hosted Komga server. See README.md for details.";

        /// <summary>
        /// Static reference to the active plugin instance.
        /// </summary>
        /// <remarks>
        /// Instantiated by Emby at application start through the DI container.
        /// Providers and helper classes (e.g. <c>Helpers.KomgaHttpClient</c>) use it to access
        /// the currently valid <see cref="Plugin.Configuration"/> without having to be
        /// handed their own injected reference.
        /// </remarks>
        public static Plugin Instance { get; private set; }

        /// <summary>
        /// Used by <c>Api/KomgaImageApi.cs</c> to build the plugin's own image-proxy URL
        /// (<see cref="IServerApplicationHost.GetLocalApiUrl(System.Threading.CancellationToken)"/>).
        /// </summary>
        public IServerApplicationHost ApplicationHost { get; }

        /// <summary>
        /// Used by <c>Helpers/AdminNotifier.cs</c> to show a dialog to logged-in administrators
        /// when Komga is unreachable or rejects the API key.
        /// </summary>
        public ISessionManager SessionManager { get; }

        /// <summary>Used together with <see cref="SessionManager"/> to find administrator sessions.</summary>
        public IUserManager UserManager { get; }

        /// <summary>
        /// Used by <c>Helpers/ProxyUrlSigner.cs</c> to accept a cover-proxy request without a valid
        /// signature if its path/Komga id belongs to a book in an Emby library (URLs Emby stored
        /// before a restart, when the signing key was different).
        /// </summary>
        public ILibraryManager LibraryManager { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="Plugin"/>.
        /// </summary>
        /// <param name="applicationPaths">Application paths provided by Emby
        /// (including the plugin configuration directory).</param>
        /// <param name="xmlSerializer">XML serializer provided by Emby,
        /// used to load/save the <see cref="PluginConfiguration"/>.</param>
        /// <param name="applicationHost">Injected by Emby's DI (an additional constructor
        /// parameter beyond the two above is resolved the same way); used only to build the
        /// plugin's own local API URL for the Komga image proxy.</param>
        /// <param name="sessionManager">Injected by Emby's DI; used only to send the
        /// "Komga not reachable" dialog to administrator sessions.</param>
        /// <param name="userManager">Injected by Emby's DI; used only to tell which sessions
        /// belong to administrators.</param>
        /// <param name="libraryManager">Injected by Emby's DI; used only to check that a cover-proxy
        /// request refers to a book in an Emby library.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerApplicationHost applicationHost, ISessionManager sessionManager, IUserManager userManager, ILibraryManager libraryManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            ApplicationHost = applicationHost;
            SessionManager = sessionManager;
            UserManager = userManager;
            LibraryManager = libraryManager;
        }

        /// <summary>
        /// Saving the settings (new URL/API key, or just ticking "Enable Komga provider") closes the
        /// Komga circuit breaker at once instead of leaving requests paused until its cooldown ends,
        /// and drops the cached Komga book listing, which a new URL/API key/path mapping makes stale.
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            Helpers.KomgaHttpClient.ResetCircuit();
            Helpers.AdminNotifier.ResetNotEnabledNotice();
            Providers.Remote.KomgaProvider.ClearBookListCache();
        }

        /// <summary>
        /// Registers the plugin's dashboard configuration page(s) with Emby.
        /// </summary>
        /// <returns>The configuration page (configPage.html) together with its controller script (configPage.js), as Emby 4.10 expects it.</returns>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "OkuzukeySettings",
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace),
                    EnableInMainMenu = true,
                    // Material Icons ligature name shown next to the entry in the dashboard's
                    // main menu, replacing the generic default folder icon.
                    MenuIcon = "auto_stories"
                },
                new PluginPageInfo
                {
                    Name = "okuzukeysettingsjs",
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.js", GetType().Namespace)
                }
            };
        }

        /// <inheritdoc />
        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(string.Format("{0}.thumb.png", type.Namespace));
        }

        /// <inheritdoc />
        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        /// <summary>
        /// Emby only deletes the plugin DLL itself on uninstall, not its saved configuration
        /// XML (<c>plugins/configurations/Emby.Okuzukey.xml</c>, with the Komga settings and logging
        /// level, would be left behind as an orphan). This plugin writes no other files of its own (no on-disk cache/storage), so deleting the
        /// configuration XML is all that is needed here.
        /// </summary>
        public override void OnUninstalling()
        {
            base.OnUninstalling();

            try
            {
                if (File.Exists(ConfigurationFilePath))
                {
                    File.Delete(ConfigurationFilePath);
                }
            }
            catch
            {
                // Best-effort - must never block the actual uninstall.
            }
        }
    }
}
