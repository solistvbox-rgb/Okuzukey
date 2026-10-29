using System;
using System.IO;
using System.Threading;
using System.Xml.Linq;
using Emby.Okuzukey.Providers.Local;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Reads a sidecar Calibre OPF or generic NFO file next to an item and applies its full field
    /// set (not just the title) to a <see cref="Book"/>/<see cref="MetadataResult{Book}"/>, for
    /// <see cref="Providers.Local.ComicInfoProvider"/>'s gap-filling merge with a <c>.cbz</c>/
    /// <c>.cbr</c>'s embedded <c>ComicInfo.xml</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Providers.Local.OpfBookProvider"/>/<see cref="Providers.Local.NfoBookProvider"/>
    /// normally handle these files themselves and run at a lower <c>Order</c> than
    /// <see cref="Providers.Local.ComicInfoProvider"/>, so in the common case
    /// <c>ComicInfoProvider</c> is never even asked once one of them has already supplied
    /// metadata. This exists for two cases: (1) an admin has disabled the OPF/NFO readers for a
    /// library (Emby's "Metadata Readers" list under library settings) - with them disabled, Emby
    /// never calls them at all, so without this, a real sidecar file would sit unused right next
    /// to the comic; (2) a comic's embedded <c>ComicInfo.xml</c> is missing fields (e.g. no
    /// <c>Summary</c>/no ISBN) that a sidecar OPF/NFO does have.
    /// <para>
    /// Reuses <see cref="OpfBookProvider.ParseOpfDocument"/>/
    /// <see cref="NfoBookProvider.ParseNfoFile"/> directly (both made <c>internal static</c> for
    /// this) rather than re-implementing field parsing here - both unconditionally overwrite
    /// whatever field they find a value for, which is exactly the "sidecar first, ComicInfo.xml
    /// wins on overlap" merge order <c>ComicInfoProvider</c> calls this with: this method must run
    /// <em>before</em> the archive's own <c>ComicInfo.xml</c> is parsed onto the same
    /// <see cref="Book"/>, never after.
    /// </para>
    /// </remarks>
    internal static class SidecarMetadataReader
    {
        /// <summary>Same file names/order <see cref="Providers.Local.OpfBookProvider"/> checks.</summary>
        private static readonly string[] OpfFileNames = { "metadata.opf", "content.opf" };

        /// <summary>Same generic file names <see cref="Providers.Local.NfoBookProvider"/> checks.</summary>
        private static readonly string[] GenericNfoFileNames = { "book.nfo", "metadata.nfo" };

        /// <summary>
        /// Tries a sidecar OPF first (matching <see cref="Providers.Local.OpfBookProvider"/>'s own
        /// priority over <see cref="Providers.Local.NfoBookProvider"/>), then a sidecar NFO,
        /// applying whichever is found onto <paramref name="book"/>/<paramref name="result"/>.
        /// Never throws (except on cooperative cancellation); a malformed sidecar is treated the
        /// same as "not found" - <c>OpfBookProvider</c>/<c>NfoBookProvider</c> already report a
        /// malformed file themselves if they are enabled.
        /// </summary>
        /// <returns><c>true</c> if a sidecar file was found and applied.</returns>
        public static bool TryApplyFullSidecarMetadata(IFileSystem fileSystem, string itemPath, Book book, MetadataResult<Book> result, ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                var directoryPath = GetDirectoryPath(fileSystem, itemPath);
                if (directoryPath == null)
                {
                    return false;
                }

                return TryApplyOpf(directoryPath, fileSystem, book, result, logger, cancellationToken)
                       || TryApplyNfo(directoryPath, fileSystem, itemPath, book, result, logger, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The directory/file lookups themselves can throw (network share gone, no
                // permission); that must not cost the item its ComicInfo.xml metadata.
                logger.Warn("[Okuzukey] Could not look for a sidecar OPF/NFO next to '{0}': {1}", itemPath, ExceptionText.Describe(ex));
                return false;
            }
        }

        private static string GetDirectoryPath(IFileSystem fileSystem, string itemPath)
        {
            if (string.IsNullOrEmpty(itemPath))
            {
                return null;
            }

            var itemInfo = fileSystem.GetFileSystemInfo(itemPath);
            var directoryInfo = itemInfo.IsDirectory
                ? itemInfo
                : fileSystem.GetDirectoryInfo(Path.GetDirectoryName(itemPath));

            return directoryInfo != null && directoryInfo.Exists ? directoryInfo.FullName : null;
        }

        private static bool TryApplyOpf(string directoryPath, IFileSystem fileSystem, Book book, MetadataResult<Book> result, ILogger logger, CancellationToken cancellationToken)
        {
            foreach (var candidateName in OpfFileNames)
            {
                var candidateFile = fileSystem.GetFileInfo(Path.Combine(directoryPath, candidateName));
                if (!candidateFile.Exists)
                {
                    continue;
                }

                try
                {
                    var document = SafeXml.LoadFile(candidateFile.FullName);
                    OpfBookProvider.ParseOpfDocument(document, candidateFile.FullName, book, result, logger, cancellationToken);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Malformed sidecar - OpfBookProvider (if enabled) already reports this;
                    // here it is simply treated as "not found".
                }
            }

            return false;
        }

        private static bool TryApplyNfo(string directoryPath, IFileSystem fileSystem, string itemPath, Book book, MetadataResult<Book> result, ILogger logger, CancellationToken cancellationToken)
        {
            var nfoFile = FindNfoFile(fileSystem, directoryPath, itemPath);
            if (nfoFile == null)
            {
                return false;
            }

            try
            {
                NfoBookProvider.ParseNfoFile(nfoFile, book, result, logger, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Malformed sidecar - NfoBookProvider (if enabled) already reports this; here it
                // is simply treated as "not found".
                return false;
            }
        }

        private static string FindNfoFile(IFileSystem fileSystem, string directoryPath, string itemPath)
        {
            var itemInfo = fileSystem.GetFileSystemInfo(itemPath);
            if (!itemInfo.IsDirectory)
            {
                var specificFile = fileSystem.GetFileInfo(
                    Path.Combine(directoryPath, Path.GetFileNameWithoutExtension(itemPath) + ".nfo"));
                if (specificFile.Exists)
                {
                    return specificFile.FullName;
                }
            }

            foreach (var candidateName in GenericNfoFileNames)
            {
                var candidateFile = fileSystem.GetFileInfo(Path.Combine(directoryPath, candidateName));
                if (candidateFile.Exists)
                {
                    return candidateFile.FullName;
                }
            }

            return null;
        }
    }
}
