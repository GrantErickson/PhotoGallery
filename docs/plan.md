# Plan: Local OneDrive Photo Gallery (WinUI 3 Desktop App)

## Status (2026-09-25): v1 core built
Done, running against the real library (253k files):
- Phases 0–3, plus Phase 5 (ratings, tags, albums): incremental indexing (full index 3m40s, no-change rescan ~1s, watcher + startup rescan), SQLite/FTS5, virtualized timeline with year/month jump list, folders, search, on this day, favorites, screenshot filter.
- Viewer: zoom, video, Live Photo motion from local pairs and embedded MP4s. Cloud-only iPhone Live Photos: OneDrive stopped serving `format=video` (406), so the LIVE button falls back to opening the photo on OneDrive.com. Details, rating, tags, albums, copy path, Explorer/OneDrive links.
- Counts after indexing: 226k photos, 10.5k videos, 14.3k screenshots; motion: 2,503 local pairs, 54 embedded, ~32k cloud-only Live Photos.

Round 2 (2026-09-25): Phase 6 editing (Win2D, non-destructive, export) and Phase 7 map (WebView2 + Leaflet + markercluster, photos-in-view gallery) are done.

Duplicate detection is done too: exact copies (size + head/tail hash) and similar re-saves (same capture second, dHash ≤ 5, different size/format/copy name); on the real library 5,774 exact groups (31.4 GB) and ~4.4k similar groups. Extra copies go to the Recycle Bin only after confirmation.

Month headers inside the timeline grid and drag-to-reorder in albums are done.

Round 3 (2026-09-26): OneDrive tags and people via the SharePoint list behind the drive (People and Tags pages, person filter, naming/merging, face-crop avatars via Windows' face detector); day markers and a stronger selection highlight in the grid; Ctrl+wheel zoom; On this day with per-year date headers and day stepping; map cluster selection and deeper zoom; editor side handles and explicit Save as copy / Overwrite original / Keep edits in gallery, with derived-copy badges and an exit warning for gallery-only edits; cloud-only (Files On-Demand) placeholders are never read.

Round 7 (2026-09-26): screenshots-only view; face names in the viewer link to the person; photo text at the bottom of the details with its own find; timeline tiles centred on faces, bigger by default, with video previews and a jump list that follows scrolling; the map opens at a photo (coordinates) or its town/place (offline GeoNames names), and custom places (centre + radius) are searchable; Live badges only where OneDrive has the video; Auto light and colour plus saturation/warmth/tint in the editor; Blurry photos (blurriest first) and a Live Photo's sharpest frame; Similar photos and searching by description (CLIP on the GPU).

Round 6 (2026-09-26): find box in transcripts (Enter/Shift+Enter, Ctrl+F); text in photos read with Windows OCR, searchable, shown under Details with search matches outlined on the photo.

Round 5 (2026-09-26): video transcripts on this PC (Whisper on the GPU, speaker labels), shown beside the video with click-to-seek and follow-along highlighting, and searchable.

Round 4 (2026-09-26): Live Photo motion plays from OneDrive via the web session; save a frame from any video; save a Live Photo's motion as an MP4; a video editor (rotate, trim, remove sound → MP4); smart crop in the photo editor; People from OneDrive's web API with names, merges and face boxes (People avatars cropped to the face, face outline on hover in the viewer).

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

## Live Photo video via the OneDrive web API (2026-09-26, working)
`GET https://my.microsoftpersonalcontent.com/_api/v2.1/drives/{driveId}/items/{itemId}/content?format=video` with the
OneDrive **web session's** Authorization header returns the original iPhone MOV (our MSAL/Graph token gets 401).
- Item id: Graph `/root:/{path}` returns the same id the web app uses (`{DRIVE}!s{guid}`; legacy files `{DRIVE}!{n}`), stored in Media.OneDriveItemId.
- Token: captured in-app from the web app's own requests in WebView2 (Settings → Live Photos → Connect; profile in `%LocalAppData%\PhotoGallery\webview`). Memory only, never logged. Refreshed silently by a hidden WebView2: a hidden page skips the `_api` calls but still calls `api.onedrive.com`, whose token also works for the video API.
- Validation: QuickTime-shaped body (not the still or JSON) and `com.apple.quicktime.content.identifier` matches the photo's Apple ContentIdentifier; otherwise discarded.
- Non-Live photo (legacy id, 2015 JPG): 400 JSON → Motion = CloudMissing, nothing cached. Test photo 20260926_141010123_iOS.heic → 5,090,295-byte MOV, content ID matches.
- Cache refused inside OneDrive roots (env vars + `HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder`).

## OneDrive tags and people (spike 2026-09-26)
Graph's driveItem has no tags/people, but personal OneDrive now runs on SharePoint (`my.microsoftpersonalcontent.com`), and the
document library's hidden list columns carry the AI metadata. Undocumented but readable with our `Files.Read` token:
`GET /me/drive/list/items?$expand=fields($select=FileRef,RecognizedEntities,MediaServiceOCR,MediaServiceLocation,TagListTags,UserAddedTags)&$top=999`
(~3 s per 999 items, pageable via `@odata.nextLink`).
- `MediaServiceOCR`: `;`-separated AI tags mixed with place parts, e.g. `Dog;Animal;LikelyPleasantMemory;Medical Lake;Spokane Co.;WA;United States;99022`.
- `MediaServiceLocation`: `United States    WA    Spokane` (used to strip place parts from the tags, and as a place tag).
- `TagListTags`: category lookups, e.g. `__Nature_32`, `Text_4`, `Screenshot_2`, `Receipt_2`, `Selfie_4`.
- `RecognizedEntities`: lookups to a people list (GUID per person, stable across photos; one GUID per face in a group shot).
  The people list itself (`/sites/{id}/lists/{id}`) is blocked for MSA accounts, so names and face crops are unavailable
  here. These GUIDs are the groups as first detected: people merged in OneDrive stay split (Megan: 145 photos here vs
  31,669 in OneDrive), so once the web API below has run, people come from there and this sync only reads tags.
- Sample of 40k items: 11.8k with people, 29.7k with AI tags, 22.8k with categories.

## OneDrive people, names and face boxes via the web API (2026-09-26, working)
Found by watching onedrive.live.com's Photos › People view in WebView2. Same web-session token as the Live Photo video
(`my.microsoftpersonalcontent.com/_api/v2.1`; the website itself uses cookies on `onedrive.live.com/personal/{cid}/_api/v2.1`).
- People: `GET /drives/{driveId}/recognizedEntities?top=100` (100 per page max, `@odata.nextLink`): `id`, `photoCount`,
  `isHidden`, `representativeItemId`, `identity.user.displayName` (the name given in OneDrive). 3,066 people, 112 named.
- Faces: the photo listing with `expand=detectedEntities(expand=recognizedEntity)` gives each face's id
  (`{itemGuid}_{nn}`), box (`boundingBoxLeft/Top/Width/Height`, pixels) and person id:
  `GET /drives/{id}/items/root/items?$filter=photo ne null and photo/takenDateTime ge 1900-01-01T00:00:00.000Z&orderby=photo/takenDateTime desc&select=id,name,parentReference,image,photo,cTag,lastModifiedDateTime,fileSystemInfo,createdDateTime,size&top=1000&expand=...`
  The `select` must include `cTag`/`lastModifiedDateTime`/`fileSystemInfo`-type fields or it returns 400. 1,000 per page
  (~2.7 s); the whole library (257k photos) takes ~14 minutes. Filtering by person (`detectedEntity/recognizedEntity/id eq '…'`)
  works but caps at 200 per page.
- Boxes: pixels of the upright picture, in a frame whose **long side** equals the long side of `image.width/height`, but
  `image.width/height` itself is unreliable: sometimes before EXIF rotation, sometimes the full 4:3 grid of an iPhone HEIC
  that displays cropped to 16:9, sometimes a downscaled size. So boxes are stored as fractions of the long side and
  scaled by the long side of the picture as displayed.
- Merges: the listing sometimes still names a face's person as first grouped; the single-item call has the current one.
  Groups merged away are missing from the people list but still answer `GET /recognizedEntities/{id}`, often with the
  person's name ("Mark" appears on three ids). The sync gives unlisted groups their name and then combines people with
  the same name; unnamed ones are folded into whoever most of five sampled faces belong to now.
- Result: counts match OneDrive (Megan 33,208 locally vs 31,669; local has some duplicate copies), no duplicate names.
- Sync: full scan monthly or on request, otherwise the newest 2,000+ photos until a page brings nothing new (eTags).

## Video transcripts (2026-09-26, working)
Local speech to text, nothing uploaded. Pipeline per video (`SpeechTranscriber`):
- Media Foundation `MediaTranscoder` → 16 kHz mono PCM in memory (15 min of audio in ~2 s); no sound track → NoAudio.
- Silero VAD (Whisper.net's `WhisperVadProcessor`, model v6.2.0): speech regions, bridging pauses < 2 s. Under 1 s of
  speech → NoSpeech (skips the silence where Whisper invents "Thanks for watching").
- `SpeechStitcher`: regions joined with 1 s gaps into one buffer (Whisper costs a full 30 s window per call however
  short) and times mapped back. Cutting at every 0.5 s pause gave choppy one-phrase "sentences"; 2 s keeps them whole.
- Whisper large-v3-turbo (ggml, 1.6 GB) via Whisper.net 1.9.1 on the RTX 4070 Ti through **Vulkan** (only the driver is
  needed; CUDA would need the toolkit installed and was judged not worth it). ~50–70× real time: 15 min in ~19 s; the
  library (10.5k videos, 215 h) ≈ 4–5 hours in the background.
- `TranscriptFormatter`: drops lone fillers, [Music]/(laughs)/*laughs* labels, stock silence phrases and repeated lines;
  paragraphs at pauses ≥ 1.5 s after a sentence, ≥ 4 s anywhere, speaker changes, or after 30 s.
- Speakers: sherpa-onnx 1.13.8 offline diarization (pyannote segmentation 3.0 + NeMo TitaNet small, CPU), clustering
  threshold 0.85. Tuned on voice journals (must stay one speaker; at 0.5–0.7 walking/driving split one voice into 2–7)
  and journal + a different (TTS) voice interleaved (≈ all covered speech attributed correctly at 0.8–0.9). 3D-Speaker
  ERes2Net split more. `SpeakerAssigner` folds voices under 15 % / 4 s into neighbours and labels only when ≥ 2 remain.
- Stored in `Transcripts` (valid for the file's size/date; model name so older pipelines get redone), words in
  `MediaFts.Speech`. Background queue newest first after videos without transcripts; the viewed video jumps the queue.

## Text in photos (OCR, 2026-09-26, working)
- Windows' built-in `Windows.Media.Ocr` (offline, user's languages; en-US here). Decoded upright at ~1600 px long side
  (2600 for tall phone screenshots): as accurate as full size and much faster; a 4K screenshot read *better* downscaled.
  Per photo ≈ 7 ms open, 20 ms decode (JPEG/PNG; HEIC 180–550 ms), 30–45 ms OCR; scales with workers
  (1 → 18/s, 4 → 51/s, 8 → 83/s on screenshots). In the app ~20+/s mixed → ~3 h for 240k photos.
- Quality: fine on receipts, documents, signs; photo-of-paper splits some words ("S okane", "Ai rport").
- `OcrCleaner` drops texture noise (lone letters/symbols, lines without a word of ≥ 3 letters/digits; a photo needs two
  real words or one of ≥ 4 letters). Words stored with boxes as fractions of the long side (like faces) in `PhotoText`;
  words in `MediaFts.PhotoText`. Order: screenshots and OneDrive's Text/Receipt/Document/Whiteboard/Sign/Menu/Poster/Book
  categories first, then newest first.
- Viewer: "Text in photo" under Details; opened from search, matches are highlighted there and outlined on the photo.

## Live Photo badges (2026-09-26)
- A still with an Apple content identifier and no local video was badged as a cloud Live Photo (32,000), but plain
  photos carry one too. OneDrive's photo listing (already read by the face sync) marks Live Photos with an empty
  `photo.livePhoto` object; it's stored as `Media.CloudLive` and decides the badge (the old guess only for photos
  OneDrive hasn't reported). After a full scan (15 min): 29,349 Live in OneDrive, 28,969 badged as cloud-only (the rest
  have local videos), ~3,000 false badges gone.

## Auto light and colour (2026-09-26)
- All colour edits are one matrix (`ColorAdjust`): white balance as log-balanced channel gains (warmth, tint) →
  saturation around Rec. 709 luma → contrast/brightness around mid-grey. Exposure stays a separate effect.
- Auto (`AutoAdjust`, from a 256 px render of the crop): levels stretch the 0.5–99.5th luma percentiles to 0.02–0.98
  (contrast only raised, ×1.6 at most), midtones nudged into 0.38–0.55, a little saturation for dull pictures. White
  balance only corrects what two estimates agree on — near-grey pixels and "grey edge" (colour differences across
  edges) — taking the smaller, 70 % of it: grey world alone cooled sunny grass/wood scenes by −0.4–0.6, while lamp-lit
  rooms still lose their orange cast (−0.2 to −0.3).

## Sharpness and Blurry photos (2026-09-26)
- Score (`Sharpness`): on the 360 px thumbnail, brightness stretched to its 1st–99th percentile (so dark ≠ blurry), then
  the standard deviation of the Laplacian per tile of a 4 × 4 grid, 90th percentile tile (a sharp subject on a soft
  background counts as sharp). Tried per-tile contrast normalisation: flagged smooth sharp portraits. Threshold 24:
  12.6k of 240k photos (5 %); ~490 photos/s from cached thumbnails.
- Sharpest frame of a Live Photo's video: `MediaComposition.GetThumbnailsAsync` (batch) returns neighbouring frames,
  and a paused MediaPlayer shows a frame or two off, so frames are grabbed one at a time (~200 ms each; ≤ 30 spread
  frames, then the neighbours of the best) at times read from the file's `stts`/`ctts`/`elst` (an iPhone 6s Live Photo
  starts at 7.5 fps, then 15), and the chosen frame is shown as a still — exactly what Save frame writes.

## Similar photos and searching by description (CLIP, 2026-09-26)
- OpenAI CLIP ViT-L/14 as ONNX fp16 (Xenova/clip-vit-large-patch14: vision 609 MB, text 248 MB), embeddings from the
  thumbnails (shorter side 224, centre crop), stored as 768 × int8 (unit vector × 127) in `Embeddings`; all in memory
  (~170 MB) and searched by brute force on all cores in tens of milliseconds.
- ONNX Runtime conflict: sherpa-onnx ships the CPU runtime 1.28.2 as `onnxruntime.dll`, which wins over Windows ML's
  1.24 (DirectML) in the output; DirectML packages stop at 1.24.4. So CLIP runs in its own process,
  `PhotoGallery.Embedder` (copied to `embedder\`), JSON lines over stdin/stdout, below-normal priority, stopped after
  5 minutes idle. On the RTX 4070 Ti through DirectML: ~120 photos/s → the library in ~35 min.
- Similar: nearest 200 with cosine ≥ 0.55 (a photo not yet reached is embedded on demand). Description: CLIP BPE
  tokenizer in Core, nearest 300 with cosine ≥ 0.19, offered from word-search results as "Photos that look like …".

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
