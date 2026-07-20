# Vista Mia

A lightweight Windows image viewer built with WPF (.NET 8), inspired by classic viewers like
BandiView. It handles everyday image formats plus two formats most viewers skip: **PSD**
(Photoshop) and **CLIP** (Clip Studio Paint).

## Features

- **Folder explorer view**: drive/folder tree, thumbnail grid, and a large preview pane side by
  side. The tree can be collapsed to a slim toolbar toggle for a more focused layout.
- **Single-image viewer**: double-click any thumbnail (or the preview) to open a borderless,
  BandiView-style window with its own menu row (File / EXIF / HDR / Image / Slideshow / Library),
  a floating settings button, and prev/next navigation overlays.
- **File-association aware**: launching the app via "Open with" loads the file's folder and jumps
  straight to that image.
- **Zoom & pan**: `Ctrl` + mouse wheel zooms; a plain wheel scroll pans the image; drag to pan;
  `0` resets to fit-to-window.
- **Slideshow** with an adjustable interval, **90° rotation**, and a basic **EXIF** info panel
  (falls back gracefully for formats with no EXIF data).

## Supported formats

| Format | How it's rendered |
|---|---|
| PNG, JPEG, BMP, GIF, TIFF | Decoded natively via WPF |
| PSD, WEBP, HEIC, ICO | Composited via [Magick.NET](https://github.com/dlemstra/Magick.NET) |
| CLIP (Clip Studio Paint) | The embedded canvas preview PNG is extracted directly from the file's internal SQLite chunk — see [`ClipThumbnailReader`](ImageViewer/Services/ClipThumbnailReader.cs) |

### How CLIP preview extraction works

A `.clip` file is a custom chunk container (`CSFCHUNK` header, then a sequence of
`[8-byte signature][8-byte big-endian length][payload]` chunks). One chunk, `CHNKSQLi`, contains a
genuine embedded SQLite3 database whose `CanvasPreview` table stores the thumbnail as a plain PNG
blob. `ClipThumbnailReader` walks the chunk stream, seeking past large layer-data chunks
(`CHNKExta`) without reading them into memory, and pulls out just that PNG.

## Keyboard & mouse

| Action | Input |
|---|---|
| Previous / next image | `←` / `→`, or the on-screen ‹ › buttons |
| Zoom in / out | `Ctrl` + mouse wheel, or `+` / `-` |
| Reset zoom (fit to window) | `0` |
| Pan | Left-click drag |
| Scroll | Mouse wheel (without `Ctrl`) |
| Close single-image viewer | `Esc` |

## Building

Requires the .NET 8 SDK.

```
dotnet build ImageViewer.sln
```

The built executable is `ImageViewer/bin/Debug/net8.0-windows/VistaMia.exe`.

## Project structure

```
ImageViewer/
  MainWindow.xaml(.cs)          Folder explorer: tree + thumbnails + preview
  SingleViewerWindow.xaml(.cs)  Borderless single-image viewer window
  Models/ImageFileItem.cs       Simple file record used by the folder tree
  Services/
    ImageLoader.cs              Format dispatch (native / Magick.NET / CLIP)
    ClipThumbnailReader.cs      CLIP chunk parsing + embedded PNG extraction
    ImagePanZoomController.cs   Shared zoom/pan/fit-to-window logic
  Resources/icon.ico            App icon
```
