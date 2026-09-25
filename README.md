# Photo Gallery

A fast Windows desktop gallery for a large local photo library (built for a ~250k item OneDrive-synced
`Pictures` folder). Files are never modified; everything the app adds (ratings, tags, albums) lives in its
own database.

## Features (v1)

- **Timeline** of every photo and video, newest first, with a year/month jump list. Virtualized, so a
  quarter-million items scroll smoothly.
- **Live Photos and motion photos** play in the viewer (Space or the LIVE button):
  - older iPhone photos whose `.MOV` sits next to them (paired by Apple's content identifier),
  - Pixel/Samsung motion photos (the MP4 embedded in the JPG),
  - newer iPhone Live Photos whose video only exists in OneDrive (downloaded on demand after signing in).
- **Viewer** with zoom (double-click / pinch), video playback, and details: date, camera, size, location.
- **Ratings** (stars, or keys 1–5 in the viewer), **tags**, **albums**, **favorites** (4★+).
- **Search** across file and folder names, tags and cameras.
- **Folders** view, **On this day**, **Live Photos** view. Screenshots are kept out of the main timeline
  (toggle to include them).
- Incremental indexing on startup and when files change (size + modified time), with background thumbnails.

Planned next: basic non-destructive editing, map view, OneDrive people/AI tags, duplicate detection.
See [docs/plan.md](docs/plan.md).

## Requirements

- Windows 10 19041+ / Windows 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For HEIC and RAW: the *HEIF Image Extensions*, *HEVC Video Extensions* and *Raw Image Extension* from the
  Microsoft Store

## Build and run

```
dotnet build PhotoGallery.slnx
dotnet run --project src/PhotoGallery.App
```

On first run the library defaults to `%OneDrive%\Pictures`; change it in **Settings**. The first index of
~250k files takes a few minutes; later starts check for changes in about a second.

Sign in to OneDrive in **Settings** to play Live Photos whose video is only in the cloud. The app uses its own
Azure app registration (personal Microsoft accounts, read-only `Files.Read`).

## Project layout

| Path | What |
|---|---|
| `src/PhotoGallery.App` | WinUI 3 app (unpackaged, Windows App SDK) |
| `src/PhotoGallery.Core` | Indexing, metadata, SQLite data layer, thumbnails, OneDrive client |
| `tests/PhotoGallery.Core.Tests` | xUnit v3 tests (`dotnet test --project tests/PhotoGallery.Core.Tests`) |
| `tools/PhotoGallery.Cli` | Headless indexing / stats / thumbnail benchmark (`dotnet run --project tools/PhotoGallery.Cli -- stats`) |
| `spikes/` | Phase 0 experiments (OneDrive Live Photo video) |
| `docs/plan.md` | Plan, decisions and spike findings |

App data (database, thumbnail cache, Live Photo video cache, settings, `app.log`) is in
`%LocalAppData%\PhotoGallery`. Deleting that folder resets the app; your photos are untouched.
