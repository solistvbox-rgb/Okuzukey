<p align="center">
  <img src="thumb.png" alt="Okuzukey" width="300">
</p>

# Okuzukey

**Okuzukey** is a metadata plugin for [Emby Server](https://emby.media/) for books, comics,
manga and light novels. It reads **local metadata first** and, **optionally**, your own
self-hosted [Komga](https://komga.org/) server:

- Calibre `metadata.opf` next to a book - or, if there is none, the OPF inside an EPUB
- `.nfo` files next to a book
- `ComicInfo.xml` inside `.cbz`/`.cbr` comic archives
- local cover images next to a book
- covers embedded *inside* `.cbz`/`.cbr`/`.epub` files, when no cover image file exists
- your own Komga server (metadata and covers), opt-in and disabled by default

No public online database is contacted - the only network requests go to your own Komga server,
and only if you enable it.

> **Status: beta.** Tested with Emby Server 4.10 and 4.11, a Komga server and a library of
> about 2500 books.

## Installation

1. Download `Emby.Okuzukey.dll` from the [Releases](../../releases) page, or build it yourself
   (see below).
2. **Stop** Emby Server.
3. Copy `Emby.Okuzukey.dll` to the plugin directory of Emby Server:
   - **Windows:** `%ProgramData%\Emby-Server\plugins\` (or `programdata\plugins` of your
     server installation)
   - **Linux:** `/var/lib/emby/plugins/` (or the path shown under
     *Dashboard → Advanced → Paths*)
   - **Docker:** the volume mapped to `/config/plugins`
4. **Start** Emby Server and check under **Dashboard → Advanced → Plugins** that **Okuzukey**
   is listed.
5. In your Books library settings, enable the Okuzukey metadata readers and image fetchers you
   want.
6. Optional: open the **Okuzukey** settings page to set up Komga.

## Providers

| Provider                     | Type   | Source |
|------------------------------|--------|--------|
| Okuzukey OPF (Calibre)       | local  | Calibre `metadata.opf`, or the OPF inside an EPUB |
| Okuzukey NFO                 | local  | `.nfo` file next to the book |
| Okuzukey ComicInfo (CBZ/CBR) | local  | `ComicInfo.xml` inside `.cbz`/`.cbr`, completed by a sidecar OPF/NFO if present; the file name is the last resort for the title |
| Okuzukey Local Cover         | local  | cover image file next to the book |
| Okuzukey Embedded Cover      | local  | cover inside `.cbz`/`.cbr`/`.epub` (only if no cover image file exists) |
| Komga                        | remote | your own Komga server |

Local providers always come before Komga. The order of the three metadata readers (OPF, NFO,
ComicInfo) is only Emby's default for new libraries; you can drag-reorder them per library under
*Library → Metadata Readers*.

Sidecar files (OPF, NFO, cover image) work for **any** book format (PDF, MOBI, AZW3, EPUB, CBZ,
CBR, ...). Reading data *inside* the book file is limited to EPUB, CBZ and CBR.

> **Note:** Emby has no author or series field for books. Authors and series are read but cannot
> be displayed by Emby.

## Komga

On the Okuzukey settings page:

| Setting                 | Description |
|-------------------------|-------------|
| Enable Komga provider   | Off by default |
| Komga server URL        | e.g. `http://192.168.1.10:25600` or `https://komga.example.com` (HTTP and HTTPS) |
| Komga API key           | An API key created in Komga under your account settings, sent as `X-API-Key` header |
| Timeout (seconds)       | Per request (1-300, default 30) |
| Max. concurrent requests| 1-20, default 4 - lower it for weak hardware (e.g. a Raspberry Pi) |
| Library path as Emby / as Komga sees it | Optional: if Emby and Komga see the same files under different paths (e.g. `Z:\Books\` vs. `/data/books/`), enter both prefixes so books can be matched by path |

The **Test connection** button checks the URL and API key before you save.

Books are matched by their Komga ID, then by file path, and only as a last resort by an exact
title match. If Komga is unreachable or rejects the API key, logged-in administrators get a
notification in Emby.

## Logging

The settings page has a log level: *Nothing*, *Errors only*, *Normal* (errors and warnings, the
default) or *Verbose* (one line per book and provider - this can add several MB to the log on a
large library).

## Security

- Archives are never extracted to disk; only `ComicInfo.xml`, the OPF and the cover image are
  read into memory, with size limits. Book pages (HTML, scripts) are never opened or executed.
- XML is parsed without DTD processing and without resolving external entities.
- Markup is stripped from all text fields (title, description, genres, tags, people).
- Covers are only delivered if they really are JPEG, PNG, GIF, WebP or BMP images (no SVG).
- Cover proxy URLs are signed, so they cannot be used to open arbitrary files or Komga IDs.
- The Komga API key is only sent to the configured server; redirects are not followed.

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download) 8.0 or newer.

```bash
dotnet build -c Release
```

The output is `bin/Release/netstandard2.0/Emby.Okuzukey.dll`. Only this DLL (and optionally the
`.pdb`) is installed. The plugin bundles no third-party DLLs: `.cbr` support uses the
SharpCompress library that Emby Server itself ships.

## Uninstalling

Uninstall via *Dashboard → Advanced → Plugins*. The plugin's saved settings are removed as well.

## License

[MIT](LICENSE) © SoliFlix
