# Photo Gallery

A fast Windows desktop gallery for a large local photo library — built for a quarter-million photos and videos in a
OneDrive-synced `Pictures` folder. It finds things by what's in them, not just by file name: people, places, text in
photos, what was said in videos, and what a photo looks like. The AI work runs on your own PC.

Your files are never changed unless you ask (edit and save, or delete). Ratings, tags, albums and everything the app
works out live in its own database.

## Features

### Browsing
- **Timeline** of every photo and video, newest first, with day badges and a year/month jump list that follows the
  scrolling. It stays smooth with 250,000 items.
- **Adjustable tiles** (slider or Ctrl+mouse wheel), cropped around faces. Video tiles play a preview in place.
- **Filters** on every gallery: photos or videos, minimum rating, Live Photos only, screenshots (none / with / only),
  and **people** (photos with everyone you pick, chosen from face avatars).
- **Folders**, **On this day** (this date in past years), **Favorites** (4★ and up), **Live Photos**, **Albums**
  (drag to reorder), **Tags**, **People**, **Map**, **Duplicates**, **Blurry photos**.

### Search
- **One search box** in the title bar (Ctrl+E). Best matches come first. That means photos that *look like* what you
  typed ("birthday cake", "dog on the beach") together with photos whose names, folders, tags, people, places,
  cameras, text or speech contain the words.
- **Exact words** narrows the results to direct word matches.
- **Sort** best match / newest / oldest. When sorted by date, **group** by day, month or year.
- **Recent searches** and suggestions (people, places, tags) as you type.
- A new search started from the results keeps your sort, grouping and filters. **Clear search** (or Esc) takes you
  back to where you started.

### Viewer
- **Zoom**, next and previous, **ratings** (1–5), tags, albums, copy path, show in Explorer, open in OneDrive.
- **Details**: date, camera, size, location with the **place name** (your place, the park or restaurant, and the
  town). Click the name or the coordinates to open the map there.
- **Faces**: names on hover, face outlines, and a click takes you to that person.
- **Text in photo**: every line of text in the photo, searchable. Search matches are outlined on the photo, and a
  find box helps with long documents.
- **Similar photos**: the photos and videos that look most like this one.
- **Delete** (Del): to the Recycle Bin, a Live Photo's video included.

### Live Photos and video
- **Live Photos and motion photos** play with Space. Their video can come from:
  - a `.MOV` stored next to the photo (older iPhone photos);
  - an MP4 embedded in the file (Pixel and Samsung);
  - OneDrive, for newer iPhone Live Photos whose video is only in the cloud.
- **Step through frames** ( , and . ). **Save a frame** as a photo (S), or save the motion as an MP4.
- **Sharpest frame** finds the clearest frame in a Live Photo's video, for when the still came out blurry.
- **Video transcripts**, made on your PC with Whisper large-v3-turbo on the graphics card:
  - shown beside the video in sentences and paragraphs, with speakers told apart;
  - click a line to jump to it, and the current line is highlighted as it plays;
  - a find box, and every word is searchable.
- **Video editor**: rotate, trim, remove the sound, and save as MP4.

### Editing (photos)
- Rotate, flip, and crop freely or to a fixed shape. **Smart crop** frames faces and the main subject.
- **Light and colour**:
  - exposure, brightness, contrast, saturation, warmth and tint;
  - **Auto** sets levels, white balance and saturation. It only corrects the colour casts it's confident about, so
    lamp-lit rooms lose their orange cast while daylight scenes are left alone.
- **Save as copy**, **overwrite the original** (the old version goes to the Recycle Bin), or keep the edits in the
  gallery only.

### People, places and the map
- **People from OneDrive**: the names you gave and the people OneDrive merged, with face positions and avatars. You
  can rename, merge and hide them.
- **Place names**:
  - **your places**: a centre and a radius you draw on the map, e.g. "Home" or "Grandma's";
  - OneDrive's place names, and the nearest town from GeoNames' offline list;
  - optionally, **parks, schools, restaurants, cafés, churches, museums, beaches** and more from OpenStreetMap.
  - All of them are searchable ("Manito Park", "restaurant").
- **Map** with clustered photo markers. The photos in view are listed beside it, and clicking a cluster lists just
  those.

### Cleaning up
- **Duplicates**: exact copies, and re-saved or resized versions of the same shot. Extra copies go to the Recycle Bin
  after you confirm.
- **Blurry photos**, blurriest first. Turn on *Live* to see the ones whose video may have a sharper frame.
- **Delete** from the gallery (Del, the selection bar, or right-click) or the viewer. When deleting several, the next
  photo is selected, so you can keep weeding.

### OneDrive
- **Sign in** (Settings) to read OneDrive's tags, categories and places, and to open photos on OneDrive.com. This uses
  Microsoft Graph with read-only access.
- **Connect** the OneDrive web session (Settings) to play Live Photo videos stored only in OneDrive, and to bring in
  people, names and faces.
- Files On-Demand placeholders (cloud-only files) are never downloaded just to index them.

### Remote access (another computer on your network)
- **Open the library from another computer** on the same network: the timeline, best-match and exact-word search
  (sorted and grouped), On this day, Favorites, Live Photos, Videos, People and Albums, with the people and
  photo/video filters.
- **The viewer**: photos at screen size, videos and Live Photos, details (place, people, text in the photo, what's
  said in a video), star ratings, and downloading the original.
- **In a browser** (any computer, tablet or phone) at `https://<computer name>:47813`, or in **Photo Gallery on the
  other computer**: *Another computer* at the bottom of the menu. There, the title bar's search box searches the
  other computer's photos.
- **The main PC does the work**: searching by description on its graphics card, decoding HEIC for browsers,
  fetching Live Photo videos from OneDrive. The other computer needs nothing installed but a browser.
- **Off until you turn it on** (Settings › Remote access, with a passphrase). Other computers can browse, search,
  view, download and rate, but not edit, move or delete anything. OneDrive's sign-in never leaves the main PC.
- **Secured for a home network**:
  - it only answers computers on the local network;
  - the connection is encrypted (HTTPS, with a certificate the main PC makes for itself);
  - the passphrase is stored only as a salted PBKDF2 key, and wrong guesses lock the guesser out for longer each time.

  Photo Gallery on the other computer shows the main PC's **security code** the first time, to compare with the one
  in its Settings; after that it only accepts that certificate. Browsers warn once that the connection isn't private,
  because nothing vouches for a certificate the PC made itself.

### What stays on your PC and what doesn't
- Indexing, thumbnails, transcripts, text recognition, sharpness, similar photos and description search all run
  locally. Nothing is uploaded.
- Downloaded once:
  - the AI models: Whisper about 1.6 GB, CLIP about 860 MB, speaker models about 50 MB;
  - GeoNames' town list, about 10 MB.
- Place names from OpenStreetMap are **off until you turn them on**. When on, they send the rough areas (squares of
  about 5 × 4 km) where your photos were taken to OpenStreetMap's Overpass service, once each.
- The map shows OpenStreetMap tiles.
- Remote access is **off until you turn it on**. When on, it serves your library only to computers on your local
  network that have the passphrase.

## Getting started

### Requirements
- Windows 10 (19041) or later, or Windows 11, x64.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download). To run a build without the SDK, install the .NET 10
  Runtime and the ASP.NET Core 10 Runtime (the web server behind remote access).
- The WebView2 runtime (built into Windows 11). It's used by the map and the OneDrive connection.
- For iPhone and camera formats: *HEIF Image Extensions*, *HEVC Video Extensions* and *Raw Image Extension* from the
  Microsoft Store.
- Optional but recommended: a graphics card with current drivers. Transcripts use Vulkan and similar photos use
  DirectML; without one they run on the CPU, much more slowly.
- Disk space: about 2.5 GB for the AI models, and about 25 KB of thumbnails plus 4 KB of database per photo (for
  250,000 photos: about 6 GB of thumbnails and a 1 GB database).

### Build and run
Double-click **`PhotoGallery.bat`** in the repo root. It builds the app the first time, then starts it. Run
`PhotoGallery.bat build` after pulling changes. It won't start a second copy.

Or from a terminal:

```
dotnet build PhotoGallery.slnx
dotnet run --project src/PhotoGallery.App
```

### First run
1. The library starts as `%OneDrive%\Pictures`. Add or remove folders in **Settings › Library folders**.
2. The first index of about 250,000 files takes a few minutes. Later starts check for changes in about a second, and
   files added or changed while the app runs are picked up automatically.
3. The background work starts about a minute after launch and runs newest first. Progress is shown in Settings:
   - thumbnails;
   - text in photos;
   - sharpness;
   - video transcripts;
   - similar-photo matching.
4. Optional, all in Settings:
   - **Sign in** to OneDrive;
   - **Connect** the web session, for people and cloud Live Photos;
   - turn on **Place names**.

### Remote access
1. On the computer with the library: **Settings › Remote access**. Set a passphrase (a few words work well), then
   turn it on. Settings shows the address and a **security code**. If Windows asks whether Photo Gallery may use the
   network, allow it on **private** networks. The network itself has to be set to *Private* in Windows.
2. On the other computer, either:
   - open `https://<computer name>:47813` in a browser, accept the warning about the certificate, and enter the
     passphrase; or
   - in Photo Gallery, choose **Another computer** (bottom of the menu), enter the computer's name and the
     passphrase, and check that the security code matches. It can remember the passphrase (in Windows' Credential
     Manager) and open the other computer's photos at start.
3. The main PC has to be on and awake, with Photo Gallery running.

### Keyboard
| Where | Keys |
|---|---|
| Anywhere | **Ctrl+E** search · **Alt+←/→** or mouse back/forward buttons: back/forward |
| Gallery | **Enter** open · **Del** delete · **Ctrl+wheel** tile size |
| Viewer | **←/→** previous/next · **Space** play Live Photo/video · **1–5** rate, **0** clear · **E** edit · **I** info · **Del** delete · **Ctrl+Shift+C** copy path · **Ctrl+F** find in transcript or photo text · **Esc** close |
| Viewer, video | **,** / **.** previous/next frame · **S** save frame |
| Photo editor | **[** / **]** rotate · **Ctrl+S** save as copy · **Esc** close |
| Video editor | **Space** play/pause · **I** / **O** set start/end · **Ctrl+S** save · **Esc** close |
| Remote (web) | **/** or **Ctrl+E** search · viewer: **←/→**, **Space** play Live Photo, **1–5** rate, **0** clear, **I** info, **Esc** close |

### Your data
The database, thumbnail and video caches, AI models, settings and `app.log` live in `%LocalAppData%\PhotoGallery`.
Deleting that folder resets the app; your photos aren't touched. Caches are never kept inside a OneDrive folder.

## Project layout

| Path | What |
|---|---|
| `src/PhotoGallery.App` | WinUI 3 app (Windows App SDK, unpackaged) |
| `src/PhotoGallery.Core` | Indexing, metadata, SQLite data layer, search, thumbnails, OneDrive clients, places, similarity |
| `src/PhotoGallery.Embedder` | CLIP on the graphics card (DirectML), a separate process so its ONNX Runtime doesn't clash with the app's |
| `src/PhotoGallery.Remote` | Remote access: the HTTPS server (Kestrel) and the web app it serves (`web/`) |
| `tests/PhotoGallery.Core.Tests` | xUnit v3 tests (`dotnet test --project tests/PhotoGallery.Core.Tests`) |
| `tools/PhotoGallery.Cli` | Headless indexing, stats and a thumbnail benchmark (`dotnet run --project tools/PhotoGallery.Cli -- stats`) |
| `spikes/` | Early experiments (OneDrive Live Photo video) |
| `docs/plan.md` | Plan, decisions and findings |
| `docs/store-plan.md` | What it would take to publish in the Microsoft Store |

## Credits

Built with the Windows App SDK (WinUI 3), Win2D, SQLite (Microsoft.Data.Sqlite, Dapper) and MetadataExtractor. Also:

- **Speech**: [Whisper.net](https://github.com/sandrohanea/whisper.net) running OpenAI's Whisper, with Silero VAD.
- **Speakers**: [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), with pyannote segmentation and NVIDIA NeMo
  TitaNet.
- **Similar photos**: OpenAI [CLIP](https://github.com/openai/CLIP) ViT-L/14, in the ONNX export by Xenova on
  Hugging Face, run with ONNX Runtime and DirectML.
- **Text in photos**: Windows' built-in OCR.
- **Map**: [Leaflet](https://leafletjs.com) and Leaflet.markercluster, with map tiles © OpenStreetMap contributors.
- **Place names**: © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors (ODbL), through the Overpass
  API. Towns from [GeoNames](https://www.geonames.org) (CC BY 4.0).

Each component and model keeps its own license; see their projects.
