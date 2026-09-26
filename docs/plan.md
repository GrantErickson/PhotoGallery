# Plan: Local OneDrive Photo Gallery (WinUI 3 Desktop App)

## Status (2026-09-25): v1 core built
Done, running against the real library (253k files):
- Phases 0–3, plus Phase 5 (ratings, tags, albums): incremental indexing (full index 3m40s, no-change rescan ~1s, watcher + startup rescan), SQLite/FTS5, virtualized timeline with year/month jump list, folders, search, on this day, favorites, screenshot filter.
- Viewer: zoom, video, Live Photo motion from local pairs and embedded MP4s. Cloud-only iPhone Live Photos: OneDrive stopped serving `format=video` (406), so the LIVE button falls back to opening the photo on OneDrive.com. Details, rating, tags, albums, copy path, Explorer/OneDrive links.
- Counts after indexing: 226k photos, 10.5k videos, 14.3k screenshots; motion: 2,503 local pairs, 54 embedded, ~32k cloud-only Live Photos.

Round 2 (2026-09-25): Phase 6 editing (Win2D, non-destructive, export) and Phase 7 map (WebView2 + Leaflet + markercluster, photos-in-view gallery) are done.

Duplicate detection is done too: exact copies (size + head/tail hash) and similar re-saves (same capture second, dHash ≤ 5, different size/format/copy name); on the real library 5,774 exact groups (31.4 GB) and ~4.4k similar groups. Extra copies go to the Recycle Bin only after confirmation.

Month headers inside the timeline grid and drag-to-reorder in albums are done.

Not yet done (next milestones): Phase 4 Graph tags/people sync (deferred by decision; tables are in place). Cloud Live Photo motion depends on OneDrive serving `format=video` again (it has refused since 2026-09-25; the app retries with backoff).

How the build differs from the architecture below:
- **Thumbnails:** no Win2D or FFmpeg. The thumbnail cache (`ThumbnailCache`) first probes the Windows thumbnail cache (IShellItemImageFactory, cache-only), then falls back to a WIC decode for images (using the embedded HEVC preview for HEIC) or a Media Foundation frame for video. HEIC decoding is capped by the codec at ~10–12 files/s, so warming takes a while on first run; tiles on screen get priority.
- **Metadata:** MetadataExtractor for images, plus our own QuickTime `moov` parser for video (so MOVs aren't read in full). `LibraryIndexer.MetadataVersion` forces a one-time re-read when extraction rules change.
- **Graph:** a plain HttpClient plus MSAL (no Graph SDK) — only content and webUrl calls so far.

## Decisions from discussion
- Platform: Native Windows desktop app, WinUI 3 (.NET 10 LTS — .NET 8 support ends Nov 2026), for best perf + virtualization at 250k+ items.
- Language: C#.
- Local DB: SQLite (embedded, WAL mode, FTS5 for tag/keyword search) — chosen over local SQL Server for zero-service-dependency, portability, single-file backup. (User has SQL Server available but it's overkill for single-user desktop app.)
- Scale target: ~250,000 photos/videos.
- Local folder layout: mostly Camera Roll dump, some manual folders, includes screenshots (need "Screenshots" classification/smart-exclude from main flow).
- Live Photos (spike done 2026-09-25, see "Phase 0 spike findings" below): iPhone Live Photos have NO video on disk — OneDrive keeps the video half in the cloud and the sync client downloads only the still. Android motion photos (Pixel/Samsung JPG) DO embed the MP4 locally, but there are only ~54 valid ones in the library.
  - UPDATE (spike run 2026-09-25): Graph returns the cloud-only video. `GET /me/drive/items/{id}/content?format=video` with a normal `Files.Read` token gives the Live Photo's QuickTime MOV (content ID verified against the still). So v1 can play iPhone Live Photos in the app: download the MOV on demand and cache it on disk. Keep "Open in OneDrive" as a fallback when offline or not signed in.
  - Play embedded Android motion-photo video and local JPG+MOV pairs directly from disk.
- Formats: must handle every format that actually occurs in the library (see "Media formats" below), not just JPG/HEIC.
- Change detection: size + modified time is sufficient. No full-content hashing during scans.
- Duplicate detection: wanted, post-v1 (the library has "(0)"-style duplicate copies, a `HEIC Conversions` folder and re-exported folders).
- Graph faces/people: deferred. Keep the `People`/`PhotoFaces` tables and the Tag `Source`/`TagType` columns as placeholders.
- Editing scope v1: basic non-destructive adjustments only — crop, rotate, brightness/contrast/exposure. AI editing explicitly deferred to future phase.
- OneDrive integration: register a free Azure AD app (personal MSA), OAuth via MSAL.NET, use Microsoft Graph API to pull tags/people(face)/location metadata NOT present in local EXIF.
- "On This Day" simplified: implement as a pure LOCAL query (DateTaken month/day match across years) — no Graph API needed for this feature.
- Local-to-cloud matching: primary by relative path (local mirrors OneDrive folder structure since it's a straight sync), fallback verify via OneDrive quickXorHash if path match ambiguous.

## Phase 0 spike findings: Live Photos / embedded video (2026-09-25)
Library: `D:\OneDrive\Pictures`, 253,755 files, 1.66 TB, all locally available (pinned).

| Source | Video in local file? | Layout |
|---|---|---|
| iPhone Live Photo (`*_iOS.heic`) | **No** | Plain HEIC: HEVC grid + HDR gain map (`tmap`) + Exif + XMP. No `moov`, no appended data, no NTFS alternate stream, no sibling `.MOV`. |
| Google Pixel (`PXL_*.MP.jpg`, older `MVIMG_*.jpg`) | Yes | JPEG + appended MP4. XMP `Container:Directory` item `Semantic="MotionPhoto"` gives its `Length` from end of file (older files: `GCamera:MicroVideoOffset`). |
| Samsung (`yyyyMMdd_HHmmss.jpg`) | Yes | JPEG + MP4 + `SEFH…SEFT` trailer. XMP sets `GCamera:MotionPhoto=1` but may not list the video; find it via the SEFT directory (entry named `MotionPhoto_Data`, or an entry whose data starts with `ftyp`). |

Whole-library census of JPG/HEIC: 45 valid Pixel, 8 valid Samsung, 1 valid MVIMG. 12 have a byte range that runs past the end of the file (truncated "(0)" duplicates / re-exports), and 52 set `MotionPhoto=1` with no video (edited/re-saved).

Rules for the extractor (Core):
- The motion-photo flag alone isn't enough to mark a MediaType. Only mark `MotionPhoto` if the range is inside the file and starts with an `ftyp` box.
- Parse box and trailer structure; don't search for byte patterns (a HEIC matched `mpvd` inside its image data).
- Extract to the cache on demand for playback; never modify the original.

Local Live Photo pairs (older iPhone exports, mostly `IMG_xxxx.JPG` + `IMG_xxxx.MOV`): 2,103 MOVs carry Apple `com.apple.quicktime.content.identifier` + `still-image-time` and are all ≤4s. 1,972 of the 2,099 checked match a same-name still in the same folder, whose Apple MakerNote holds the same content-identifier UUID. Another 111 have no same-name still, and 16 have a still whose ID doesn't match (candidates for pairing by ID anywhere in the library). The remaining ~9k MOVs are ordinary videos.
- Pairing rule: match on content-identifier UUID (MOV metadata ↔ still MakerNote `ContentIdentifier`). The same name is only a hint. A paired MOV is hidden from the grid and plays as the still's motion.
- A still with a ContentIdentifier and no local MOV → `IsLivePhoto`, motion in the cloud only (show the OneDrive link). Need to confirm on a few known non-Live iPhone shots that ordinary stills don't carry this tag.

OneDrive/Graph (`spikes/OneDriveLivePhoto`, app registration `5f0b2132-a2b1-45ef-9f3c-2ffc26fe24b5`, personal accounts only, scope `Files.Read`):
- **UPDATE 2026-09-25 evening: this stopped working.** The same request (same spike binary, same item) now answers `406 UnknownError` for every item, as do `format=mp4/mov`, Accept headers, and the pre-authenticated downloadUrl variants; the item metadata has no Live Photo facet. The path form (`/root:/path:/content?format=video`) always returned 406. The app now treats 406/400 as "service refused" (never as "no motion"), keeps retrying on demand, and opens the photo on OneDrive.com (whose viewer plays the motion) when the download is refused.
- Original result: `GET graph.microsoft.com/{v1.0|beta}/me/drive/items/{id}/content?format=video` → **200, QuickTime MOV** (3.9 MB, 2.37s, `ftypqt`, Apple `content.identifier` = the still's MakerNote ContentIdentifier). This isn't in Graph's documentation, but it works with our own app. It depends on behaviour Microsoft could change without notice, so treat it as best-effort.
- `POST` to the same URL → 406. `api.onedrive.com …&ump=1` → 401 with a Graph token (not needed).
- Plain `/content` returns the 1,766,478-byte HEIC, byte-identical to the local file. The Graph metadata (`file`, `photo`, `image`) has **no Live Photo indicator**, so Live Photo detection has to be local: the still's Apple MakerNote ContentIdentifier.
- Graph `photo` facet provides takenDateTime, camera, exposure and quickXorHash/sha1/sha256 (sha256 could help duplicate detection later).
- Still to check: what `format=video` returns for a non-Live photo (expect 4xx). Throttling behaviour if motion is prefetched in bulk. Prefer fetching on demand.

## Media formats (from the library census)
| Kind | Extensions (count) | Decode / handling |
|---|---|---|
| Photos | .jpg/.jpeg (182k), .heic (47k), .png (11k), .bmp, .gif, .webp, .tif/.tiff | WIC via Win2D. HEIC needs the HEIF + HEVC Video Extensions (check at startup). GIF: show the first frame in the grid, animate in the viewer. |
| RAW | .cr2 (230), .dng (62) | WIC via the Windows Raw Image Extension. If it's missing, fall back to the embedded JPEG preview. |
| Video | .mov (11k, 896 GB), .mp4 (1k), .avi, .mts, .mpg, .wmv, .mod, .m4v, .3gp | MediaPlayerElement where Media Foundation supports the format. FFmpeg for thumbnails/probing, and for playback fallback (.avi/.mod/.mts codecs vary). |
| Layered | .psd (17) | Try WIC; otherwise show a placeholder thumbnail. |
| Sidecars (not media) | .aae (iOS edit sidecar), .thm (camera video thumb), .xmp | Link to the parent media item; don't list them in the grid. |
| Ignore | .ini, .db, .info, .epp, .pdn, .pdf, .docx, .pptx, .zip, .lnk, temp files, etc. | Skip during scanning. |

## Architecture Overview
- **UI**: WinUI 3 (.NET 8), MVVM (CommunityToolkit.Mvvm).
- **DB**: SQLite via Microsoft.Data.Sqlite + Dapper (lightweight, full control over perf-critical queries); FTS5 virtual table for tags/keywords/people search.
- **Image decode/render/basic edits**: Win2D (Microsoft.Graphics.Win2D) — leverages WIC, hardware-accelerated, good for crop/brightness/contrast effects and thumbnail generation. Relies on Windows' built-in HEIF codec for HEIC decode.
- **EXIF/metadata extraction**: MetadataExtractor NuGet (JPEG/HEIC/TIFF/video containers) for DateTaken, GPS, camera model, orientation.
- **Video/Live Photo processing**: FFmpeg (via FFMpegCore wrapper, bundle ffmpeg.exe) for frame extraction, probing embedded video streams, and generating video thumbnails. MediaPlayerElement (built into WinUI3) for playback.
- **OneDrive Graph integration**: Microsoft.Identity.Client (MSAL.NET, public client + PKCE) for auth; Microsoft Graph SDK for driveItem metadata (tags, people, location facets); JSON batching + delta query to respect rate limits and avoid full 250k re-fetch each sync.
- **Map viewer**: WebView2 hosting Leaflet.js + OpenStreetMap/MapTiler tiles + Leaflet.markercluster for clustering; communicates with app via WebView2 messaging, driven by the same FilterContext as grid/search.
- **Indexing pipeline**: Producer/consumer using .NET Channels — file scanner → EXIF extractor → thumbnail generator → DB writer, parallelized across cores, with FileSystemWatcher for live updates + periodic fallback rescan (watcher can miss events) and hash/mtime-based change detection to skip unchanged files.

## DB Schema (initial)
- `Photos`: Id, FilePath, ContentHash, FileSize, DateTaken, DateModified, Width, Height, MediaType (Photo/Video/MotionPhoto/Screenshot/Raw), IsLivePhoto (iPhone, motion only in the cloud), MotionVideoOffset, MotionVideoLength (embedded Android video range, validated), CameraModel, GpsLat, GpsLon, Rating, OneDriveItemId, ThumbnailPath, PreviewPath
- `Folders`: mirrors local directory tree, used for folder-browse view
- `Tags` + `PhotoTags` (many-to-many): Source (OneDriveAI/User/Local), TagType (Person/Object/Keyword/Location)
- `People` + `PhotoFaces` (from OneDrive person tags)
- `Albums` + `AlbumPhotos` (ordered)
- `Edits`: PhotoId, OperationsJson (crop/rotate/brightness/contrast/exposure params), non-destructive, applied at render/export time
- `SyncState`: Graph delta tokens, last-indexed timestamps per folder, for incremental sync/rescan

## Phases

### Phase 0 — Foundations & Spikes (blocking, do first)
1. Scaffold WinUI 3 solution (.NET 8), project structure (App, Core/Indexing, Data, Graph, UI).
2. ~~**Spike**: Live Photo container format~~ — DONE, see findings above. OneDrive `format=video` spike also DONE — it works via Graph.
3. **Spike**: verify Windows HEIC codec available (WIC) for decode via Win2D on target machine; confirm large-image performance.
4. ~~Register Azure AD app~~ — DONE: client ID `5f0b2132-a2b1-45ef-9f3c-2ffc26fe24b5` (personal accounts, public client, redirect `http://localhost`, Files.Read).
5. ~~**Spike**: classify the 11k `.MOV` files~~ — DONE: ~2.1k are Live Photo videos (paired by content identifier); the rest are ordinary videos.

### Phase 1 — Core Indexing & Data Layer (*depends on Phase 0*)
1. Define SQLite schema + migrations, enable WAL + FTS5.
2. Build file scanner (recursive, folder mirror into `Folders` table) + change detection (size + mtime only). Uses the extension allow-list from "Media formats"; sidecars are linked to their parent item.
3. EXIF/metadata extraction via MetadataExtractor → populate `Photos` (DateTaken, GPS, camera, dimensions, MediaType classification incl. screenshot heuristic).
4. Thumbnail generation pipeline (Win2D): grid thumb (~256px) + preview (~1600px) sizes, cached on disk keyed by ContentHash.
5. FileSystemWatcher + periodic rescan for incremental updates.

### Phase 2 — Main Browsing UI (*depends on Phase 1*)
1. **Prototype early** (perf risk): grouped-by-date virtualized grid at 250k-item scale — evaluate ItemsRepeater custom layout vs alternatives; build "jump to date" scrollbar/index (like Windows Photos app).
2. Folder browser view (tree from `Folders` table).
3. Photo detail/viewer: pan/zoom for stills, MediaPlayerElement for video, live-photo playback (pending Phase 0 spike result).
4. Screenshot smart-filter (exclude/include toggle in main flow).

### Phase 3 — Search & Filtering (*depends on Phase 1, parallel with Phase 2*)
1. Central `FilterContext`/query builder (date range, rating, folder, person, tag, keyword, location bbox) shared across grid/map views.
2. FTS5-backed keyword/tag search UI.
3. Saved smart searches.

### Phase 4 — OneDrive Graph Integration (*depends on Phase 0 app registration, parallel with Phase 2/3*)
1. MSAL.NET auth flow + token cache.
2. Local-file-to-driveItem matching (path-based primary, quickXorHash fallback).
3. Pull tags/people(face)/location facets via Graph, batch requests, delta query for incremental sync; cache into `Tags`/`People`/`PhotoFaces`.
4. Background/idle-triggered sync job with progress + rate-limit backoff.

### Phase 5 — Ratings, Tags, Albums (*depends on Phase 2/3*)
1. Star rating UI (grid overlay + detail view).
2. Custom user tags (separate from OneDrive AI tags, stored with Source=User).
3. Album CRUD + drag-drop add, ordered album view.

### Phase 6 — Basic Editing (*depends on Phase 2*)
1. Non-destructive edit model (`Edits.OperationsJson`): crop, rotate, brightness/contrast/exposure.
2. Win2D-based live preview pipeline applying operations.
3. Save-as/export flattened copy (never overwrite original OneDrive-synced file).

### Phase 7 — Map Viewer (*depends on Phase 3*)
1. WebView2 + Leaflet + tile provider integration, marker clustering.
2. Wire to shared FilterContext (bbox drawn on map narrows grid/search results and vice versa).

### Phase 8 — Stretch/Future (explicitly out of v1 scope)
- Duplicate detection (size → partial hash → full hash / perceptual hash), including "(0)" copies and re-exports.
- Graph people/faces and AI tags (tables already exist as placeholders).
- Optional background prefetch of cloud Live Photo videos (v1 fetches on demand).
- AI-based local editing enhancements.
- Local on-device face/object detection beyond OneDrive tags.
- Push notifications for "On This Day".
- Remote/mobile web companion view.

## Verification strategy
- Indexing: run full scan against real 250k-photo library, measure throughput, verify DB counts match filesystem, verify WAL mode + FTS5 queries return in <100ms for typical searches.
- Grid perf: profile scroll FPS at 250k items pre/post virtualization prototype (Phase 2 step 1) — this is the top perf risk, prototype before building full UI around it.
- Live Photo: validate against real spike findings from Phase 0 before committing to extraction code.
- Graph sync: validate against Graph API rate limits (429 handling/backoff), confirm delta query avoids full re-pull.
- Editing: confirm original files are never mutated; edits reversible/re-editable.

## Open technical risks (flagged, not blocking plan start)
1. ~~Exact Live Photo embedded-video container format~~ — resolved: iPhone video is cloud-only; Android motion photos are embedded (see findings).
2. Virtualized grouped grid performance at 250k scale in WinUI3 — resolved via Phase 2 early prototype.
3. Graph API rate limiting for a 250k-item one-time metadata backfill — mitigate via batching + delta + background pacing.
