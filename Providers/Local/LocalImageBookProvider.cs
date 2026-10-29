using System;
using System.Collections.Generic;
using System.Threading;
using Emby.Okuzukey.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace Emby.Okuzukey.Providers.Local
{
    /// <summary>
    /// Local image provider that reads a cover image for a book from its directory and
    /// provides it as the primary image (<see cref="ImageType.Primary"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The book's directory (<see cref="BaseItem.ContainingFolderPath"/>) is searched for the
    /// following file names, in exactly this order; the first match wins:
    /// <c>cover.jpg</c>, <c>cover.png</c>, <c>cover.jpeg</c>, <c>folder.jpg</c>,
    /// <c>folder.png</c>, <c>poster.jpg</c>, <c>poster.png</c>, <c>front.jpg</c>,
    /// <c>front.png</c>.
    /// </para>
    /// <para>
    /// <b>About the implemented interfaces:</b> <see cref="ILocalImageProvider"/> is a pure
    /// marker interface without members of its own in the Emby SDK (it only inherits
    /// <c>Name</c>/<c>Supports</c> from <see cref="IImageProvider"/>). The actual
    /// <c>GetImages</c> method is only required by <see cref="ILocalImageFileProvider"/>,
    /// which in turn extends <see cref="ILocalImageProvider"/> and
    /// <see cref="IImageProvider"/>. So that Emby actually consults this provider for
    /// image lookups (and it is not just an inconsequential marker in the code),
    /// this class additionally implements <see cref="ILocalImageFileProvider"/> -
    /// which automatically satisfies <see cref="ILocalImageProvider"/> as well.
    /// </para>
    /// <para>
    /// The low <see cref="Order"/> value ensures that a locally found cover always takes
    /// precedence over cover images from online providers. Missing files, unreadable
    /// directories or permission problems never cause a crash: in
    /// these cases only an empty result list is returned, so that online providers
    /// can step in.
    /// </para>
    /// </remarks>
    public class LocalImageBookProvider : ILocalImageProvider, ILocalImageFileProvider, IHasOrder
    {
        /// <summary>
        /// Candidate file names for the book cover, in priority order. The first
        /// existing match is used, all others are ignored.
        /// </summary>
        private static readonly string[] CandidateFileNames =
        {
            "cover.jpg",
            "cover.png",
            "cover.jpeg",
            "folder.jpg",
            "folder.png",
            "poster.jpg",
            "poster.png",
            "front.jpg",
            "front.png"
        };

        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="LocalImageBookProvider"/>.
        /// </summary>
        /// <param name="logger">Logger injected by Emby, named for this provider.</param>
        public LocalImageBookProvider(ILogger logger)
        {
            _logger = FilteredLogger.Wrap(logger ?? throw new ArgumentNullException(nameof(logger)));
        }

        /// <inheritdoc />
        public string Name => "Okuzukey Local Cover";

        /// <summary>
        /// Low order value so that a locally found cover always takes precedence over cover images
        /// from online providers.
        /// </summary>
        public int Order => 0;

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Book;
        }

        /// <inheritdoc />
        public List<LocalImageInfo> GetImages(
            BaseItem item,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var results = new List<LocalImageInfo>();

            if (item == null)
            {
                return results;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directoryPath = item.ContainingFolderPath;
                if (string.IsNullOrEmpty(directoryPath))
                {
                    _logger.Debug("[Okuzukey] Cannot determine a directory for '{0}', skipping cover search.", item.Path);
                    return results;
                }

                foreach (var candidateName in CandidateFileNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileSystemMetadata candidateFile;
                    try
                    {
                        candidateFile = directoryService.GetFile(directoryPath, candidateName, false);
                    }
                    catch (Exception ex)
                    {
                        // Single file lookup failed (e.g. permission problem when
                        // accessing exactly this file) -> skip it and continue with the next
                        // candidate instead of aborting the whole search.
                        _logger.Warn(
                            "[Okuzukey] Could not check '{0}' in '{1}': {2}",
                            candidateName,
                            directoryPath,
                            ExceptionText.Describe(ex));
                        continue;
                    }

                    if (candidateFile != null && candidateFile.Exists && !candidateFile.IsDirectory)
                    {
                        results.Add(new LocalImageInfo
                        {
                            FileInfo = candidateFile,
                            Type = ImageType.Primary
                        });

                        _logger.Debug("[Okuzukey] Local cover found: '{0}'.", candidateFile.FullName);

                        // First match in priority order wins; further candidates
                        // are deliberately no longer checked.
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly: a problem during the cover search must under
                // no circumstances abort the library scan.
                _logger.ErrorException("[Okuzukey] Unexpected error during cover search for '{0}'.", ex, item.Path);
            }

            return results;
        }
    }
}
