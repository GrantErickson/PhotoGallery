# Publishing to the Microsoft Store: plan

What it would take to put Photo Gallery in the Microsoft Store, in order. It covers the decisions to make, the things
that would block certification or cause trouble later, the engineering work, the Partner Center steps, and a rough
timeline. Written September 2026; check the linked policies before submitting, as they change.

## Summary

It's feasible, and most of the app can ship as it is. Three things need attention first:

1. **The OneDrive web-session features are a known risk, and you've decided to keep them.** These are Live Photo
   videos stored only in OneDrive, and people, names and faces from OneDrive. They work by reading the authorization
   header of the user's own OneDrive web session and calling undocumented OneDrive endpoints. A reviewer could read
   that as a problem under Store Policy 10.2 (security), it may conflict with OneDrive's terms, and it could break
   whenever OneDrive changes. §2.1 makes it opt-in, clearly disclosed and switchable off, with a fallback if
   certification objects. §6 lists documented alternatives that reduce how much the app depends on it.
2. **The map and the place names use free OpenStreetMap community servers** that aren't meant for apps distributed
   to the public. They need a paid or self-hosted provider, or those features stay off.
3. **The name.** "Photo Gallery" is generic and close to Microsoft's old *Windows Photo Gallery*. The Store needs a
   unique name (Policy 10.1.1).

The rest is ordinary packaging and polish:

- MSIX packaging, with a self-contained .NET;
- a first-run experience;
- consent before the 2.5 GB model download;
- a privacy policy, an About page with licences, an accessibility pass, and Store listing assets.

**Estimate:** about 3–5 weeks of focused work to a first submission, then a few days of certification.

## 1. Decisions to make first

| Decision | Options | Recommendation |
|---|---|---|
| Account type | Individual: free since Sept 2025. Company: one-time $99 plus business verification. | Individual, unless you want a company as the publisher name. |
| Package type | **MSIX**: Store-signed, auto-updates, clean uninstall. **EXE/MSI from your own URL** (Policy 10.2.9): you sign it (needs a code-signing certificate), host it and update it. | MSIX. |
| Price | Free / paid / free trial / add-ons | Decide before submission. Using the Store's commerce costs 15% for apps; non-game apps can use their own commerce and keep 100%. |
| Name | — | A distinct brand. Keep "OneDrive", "iPhone" and "Live Photos" out of the title; "works with OneDrive" is fine in the description. |
| Features in v1 | See §2 | Keep the web-session features, opt-in and switchable off (§2.1). Place names from your own data, or off (§2.4, §6). |
| Architectures | x64, ARM64 | x64 first. ARM64 after checking the native parts (§3, item 11). |
| Minimum Windows | Currently 10.0.19041 | Keep it. Windows 10 needs WebView2 checks (§2.7). |

## 2. Blockers and risks

### 2.1 OneDrive web session (kept: accepted risk)
- **What it does:** a WebView2 loads onedrive.live.com with the user's sign-in, captures the `Authorization` header
  of its API calls, and uses it (in memory only) for:
  - Live Photo videos stored only in OneDrive;
  - people, names and face boxes.
- **The risks you're accepting:**
  - **Certification:** Policy 10.2 says products must not "jeopardize or compromise user security". A reviewer who
    notices token capture may reject the submission, or ask for changes.
  - **Removal later:** the app could be pulled after a complaint, even after it's approved.
  - **Breakage:** the endpoints are undocumented and can change without notice. The Live Photo video endpoint has
    already changed once (Graph's `format=video` began refusing with 406).
  - **Terms:** it may conflict with OneDrive's or the Microsoft Services Agreement's terms. Keep it low-volume and
    limited to the user's own data, as it is now.
- **Mitigations to build before submitting:**
  - **Opt-in and plain-spoken.** Nothing happens until the user clicks Connect. The Connect page and the listing say
    what it does ("uses your OneDrive web sign-in to play cloud-only Live Photos and bring in OneDrive's people"), and
    that it's unofficial and may stop working.
  - **Handle the token exactly as now**, and say so in the privacy policy:
    - memory only, never stored, logged or sent anywhere but OneDrive's API host;
    - on demand and low-volume;
    - Disconnect clears the sign-in cookies.
  - **Remote off switch.** A small JSON file the app reads at start, on your own site, can turn the feature off
    everywhere if Microsoft objects or the endpoints break. Turning a described feature *off* is fine; the dynamic-code
    rule (10.2.2) is about adding behaviour.
  - **Fails gracefully.** If an endpoint stops answering as expected:
    - Live Photos fall back to "Open in OneDrive" (already done);
    - people keep the last data read, with a note in Settings.
  - **A `STORE_SAFE` build flag** that removes the feature entirely, ready to resubmit within a day if certification
    objects.
  - **Certification notes:** say it's optional, off by default, and how it works. Being upfront beats being found
    out.
- **Reduce the dependence over time** with the documented alternatives in §6:
  - local face recognition for people;
  - local `.MOV` pairs for Live Photos (iCloud for Windows and USB import keep them).

### 2.2 Microsoft Graph sign-in (needs a production setup)
- Register the app in Microsoft Entra ID under your publisher account (personal Microsoft accounts, read-only
  `Files.Read` + `offline_access`).
- Add the redirect URI for the packaged app. Consider the Windows broker (WAM) for sign-in.
- Publisher verification makes the consent screen show a verified publisher instead of "unverified".
- Don't ship the development client ID.

### 2.3 Map tiles (must change)
- The map loads tiles from `tile.openstreetmap.org`. The OSMF tile policy requires a distinct User-Agent, visible
  attribution and caching. It forbids offline or bulk use, warns commercial services that "access may be withdrawn at
  any point", and says access "may be blocked without prior notice".
- **Action:** use a tile provider with an API key: MapTiler, Stadia Maps, Thunderforest, Azure Maps, or self-hosted
  vector tiles (e.g. Protomaps on your own storage). Keep the OpenStreetMap attribution.
  - Restrict the key to the app (keys shipped in apps can be extracted), or route requests through a small proxy.

### 2.4 Place names from OpenStreetMap (must change or stay off)
- The Overpass guidance asks users to stay under about 10,000 requests and 1 GB a day. It also says an app "for more
  than just OSM mappers" should not rely on the public instances and should run its own.
- **Options:**
  - Run your own Overpass instance: a VM, the planet data, and upkeep.
  - Better: **pre-build compact place tiles** from OSM extracts (e.g. Geofabrik) with just the named places this app
    uses. Publish them on a CDN; the app downloads only the tiles it needs. There's no per-user query load, and it's
    faster.
  - Ship v1 with place names off.
- ODbL applies either way: attribution, and share-alike for a derived database you make public.

### 2.5 AI model downloads (manageable)
- The first run downloads about 2.5 GB: Whisper large-v3-turbo 1.6 GB, CLIP ViT-L/14 fp16 860 MB, and speaker models
  about 50 MB.
- **Actions:**
  - **Ask first** (show the sizes), and wait on metered connections (Windows' network cost API).
  - **Pin** the download URLs to fixed revisions (e.g. Hugging Face `resolve/<commit>/…`) or host the files
    yourself. **Check SHA-256** before using them.
  - Models are data, not code, so Policy 10.2.2 (no dynamic code) isn't an issue. Describe the downloads in the
    listing.
- **Licences** (confirm each and credit them in the About page and a third-party notices file):
  - Whisper: MIT;
  - Silero VAD: MIT;
  - pyannote segmentation 3.0: MIT;
  - NVIDIA NeMo TitaNet small: check the model card;
  - OpenAI CLIP: MIT (the ONNX export is by Xenova);
  - GeoNames: CC BY 4.0;
  - OpenStreetMap: ODbL.

### 2.6 Privacy (required)
- **Policy 10.5.1:** desktop (Win32 and Desktop Bridge) products must always have a privacy policy URL in Partner
  Center.
- **Policy 10.5.2:** sending personal information to a third party needs opt-in consent, and a way to take it back.
  The place-name lookup is already opt-in; keep it so.
- **The privacy policy should say:**
  - photos, faces, text, speech and similarity are processed on the device;
  - there is no telemetry (unless you add some);
  - what goes where: Microsoft Graph (OneDrive, when signed in), the map tile provider (the areas you view), place
    lookups (opt-in), and model and town-list downloads;
  - logs stay local.

### 2.7 Things the app depends on (disclose and detect)
- **Policy 10.2.4:** if primary features depend on other software, disclose it at the start of the description.
  - *HEIF Image Extensions* and *HEVC Video Extensions* (the HEVC one is paid) are needed for iPhone photos and
    videos. Detect missing codecs and link to their Store pages.
  - *Raw Image Extension* is needed for camera RAW.
- **WebView2 runtime:** built into Windows 11; it may be missing on Windows 10. Detect it and offer the Evergreen
  installer (an MSIX can't bundle the bootstrapper).
- **GPU:** Vulkan (transcripts) and DirectML (similarity) are optional, with a CPU fallback. Recommend a GPU in the
  system requirements.

### 2.8 Remote access (manageable)
- **What it does:** when turned on, the app runs an HTTPS server (Kestrel, port 47813) that other computers on the
  local network open in a browser or in the app.
- **Packaging:**
  - a full-trust desktop MSIX can listen on the network;
  - declare the port with a `desktop2:FirewallRules` extension, so Windows doesn't prompt and the rule goes away with
    the app;
  - the self-contained build must include the ASP.NET Core shared framework, which adds about 20 MB.
- **Certificate:** the app makes a self-signed certificate and keeps it in the user's certificate store
  (`CurrentUser\My`). Uninstalling an MSIX doesn't remove it, so offer "Remove remote access certificate" in
  Settings.
- **Policy 10.2 (security):** it's opt-in, and:
  - it only answers local-network addresses;
  - it requires a passphrase, and slows down guessing;
  - it never exposes the OneDrive sign-in;
  - other computers get read access plus ratings only.

  Say so in the certification notes, and in the privacy policy (§2.6): photos are served only to your own network.
- **Reviewers:** test it with a browser on the same machine (`https://localhost:47813`).

### 2.9 First run and certification testing
- **Policy 10.1.1:** "the value proposition … must be clear during the first run". The app currently assumes
  `%OneDrive%\Pictures`. It needs a welcome page that:
  - picks folders;
  - explains the background work;
  - offers the downloads and the opt-ins.
- Reviewers will use small libraries and may not have OneDrive: everything must work, and look good, with a local
  folder of a few hundred photos and no sign-in.
- **Policy 10.4 (usability):**
  - 10.4.2: start promptly, stay responsive, and never close unexpectedly. The first index already runs in the
    background; keep it that way.
  - 10.4.1: if the device can't run the app, say what's needed at launch.
  - Test on a clean VM with no .NET, no codecs, no GPU and no OneDrive.
- **Policy 10.3 (testable):** reviewers must be able to try everything. No sign-in is required, which helps.

## 3. Engineering work

1. **MSIX packaging.**
   - Set `WindowsPackageType` to MSIX and add `Package.appxmanifest`:
     - identity from Partner Center;
     - display name, description and logos;
     - `runFullTrust` and `internetClient` capabilities;
     - MinVersion 10.0.19041.0.
   - Choose between the Windows App SDK as a framework dependency (the Store installs it) or self-contained (as
     now).
   - Include `embedder\` in the package.
   - Produce an `.msixupload` bundle from CI.
2. **Self-contained .NET.** The Store doesn't install the .NET runtime.
   - Publish the app and the embedder self-contained for win-x64.
   - Consider ReadyToRun for startup time.
   - Avoid trimming: WinUI and reflection-based binding don't trim safely.
3. **Build flags and switches:**
   - a remote off switch for the web-session features;
   - a `STORE_SAFE` build that removes them (§2.1);
   - production app registration IDs instead of the development client ID.
4. **App data under MSIX.** Writes to `%LocalAppData%\PhotoGallery` are redirected to the package's private storage.
   The app keeps working unchanged, but:
   - an existing unpackaged install's database and caches aren't visible. Offer "import existing data", or accept a
     fresh index;
   - uninstalling removes about 6 GB of thumbnails, a 1 GB database and 2.5 GB of models. Say so in the listing and
     the FAQ;
   - "Reset" in Windows Settings clears everything.
5. **First-run experience** (§2.9), plus a way to rerun it from Settings.
6. **Download consent, metered-network handling and SHA-256 checks** for the models (§2.5).
7. **Map tile provider** with key handling (§2.3).
8. **Place names**: pre-built tiles, your own instance, or off (§2.4).
9. **About page:**
   - version;
   - privacy policy and support links;
   - the third-party notices file (packages, models, data).
10. **Accessibility:**
    - an `AutomationProperties.Name` on every icon-only button (tooltips alone aren't read by all tools);
    - keyboard-only navigation;
    - high contrast and text scaling;
    - test with Narrator.
11. **ARM64** (later). Check a win-arm64 build of each native part:
    - Whisper.net (CPU and Vulkan runtimes);
    - sherpa-onnx (has win-arm64);
    - ONNX Runtime DirectML;
    - Win2D.

    x64 runs under emulation on ARM64 Windows 11, but slower.
12. **Diagnostics.** Partner Center health reports show crashes for Store installs. Keep `app.log` local, and add a
    "Copy diagnostics" button for support.
13. **Testing:**
    - run the **Windows App Certification Kit** locally;
    - test on a clean Windows 10 and Windows 11 VM;
    - test install, upgrade (database migrations) and uninstall;
    - test with a small library and a 250k library, offline, and on a metered connection;
    - test without the HEIF and HEVC codecs.
14. **Localization:** English only for v1. Wording is in XAML and code; move it into resources if you plan more
    languages.

## 4. Partner Center steps

1. **Create the developer account** (individual, free) and sign the agreement.
2. **Reserve the name.** Try alternatives early.
3. **Create the app** and copy its identity (Name, Publisher, PublisherDisplayName) into the manifest.
4. **Properties:**
   - category *Photo & video*;
   - privacy policy URL;
   - website and support contact;
   - system requirements: x64, 8 GB+ RAM recommended, GPU recommended, disk space for the cache and models.
5. **Age rating:** the IARC questionnaire. It's a photo tool; mention that it downloads AI models and can connect to
   OneDrive.
6. **Pricing and availability:** price or trial, markets, release date, visibility. Use **package flights** for a
   private beta first.
7. **Store listing:**
   - a description that starts with the codec dependency (§2.7);
   - what runs locally and what goes online;
   - 4–8 screenshots at 1920×1080: timeline, search results, viewer with a transcript, map, people, editor;
   - logos and tile images;
   - up to 7 search terms;
   - release notes.
8. **Submission options:** in the notes for certification, say that no sign-in is needed (OneDrive is optional), that
   the models download on consent, and how to try it with a small folder.
9. **Upload** the `.msixupload`, then **submit**. Certification usually takes a few days; fix anything reported and
   resubmit.
10. **Publish.** The Store handles updates: bump the version for each submission.

## 5. Branding

"Photo Gallery" can't be the Store name: it's generic, and close to Microsoft's old *Windows Photo Gallery*. The app's
real difference is **finding** things in a huge library, by what's in the photos, privately on your own PC. A name
and look should carry that.

### Name ideas
In a quick search I found no photo app with these names, except where noted. That is **not** a trademark check (see
below).

| Name | Idea | Notes |
|---|---|---|
| **Findframe** | Find the frame: search, and the sharpest frame of a Live Photo | Short, says what it does. No conflicts found. |
| **Cairn** | Stones stacked to mark a trail: photos as markers of where you've been | Calm and memorable. A 2025 game shares the name, in a different class. |
| **Hindsight** | Looking back, and seeing clearly | Common word; check for photo or software marks. |
| **Porchlight** | Warm, family, "come on in" | Friendly; less about search. |
| **Keepwell** | Keep your photos well organized, safe and local | Fits the privacy angle. |
| **Trove** | A treasure trove of photos | Very common word; a longer form like "Photo Trove" may be needed. |
| ~~Everframe~~ | — | Taken (an event photo platform and a digital frame). |

**Avoid:**
- "Lens" (Google Lens, Microsoft Lens);
- "Recall" (Windows Recall);
- "Photos" alone, or anything with "Windows", "OneDrive", "iPhone" or "Live Photos" in the title;
- "Aperture" and "Darkroom" (existing apps).

**Before committing to a name:**
1. Search the Microsoft Store, the web and app stores for it.
2. Search the USPTO trademark database (classes 9 and 42 cover software), and your country's register.
3. Reserve it in Partner Center; the name is then yours for the Store.
4. Get a matching domain for the privacy policy and support pages.

### Tagline options
- "Every photo, found."
- "Your whole photo library, searchable, private, on your PC."
- "Search your photos by what's in them: people, places, words and moments."

### Look
- **Dark-first**, matching the app's default: near-black surfaces, and photos as the colour.
- **One warm accent**, for example amber (#F2A33A), for selection and highlights. It stands out against the blues
  of most photo tools.
- **Icon:** a simple mark that reads at 16 px. Ideas:
  - a photo frame with a small magnifier in one corner;
  - for Cairn, three stacked rounded stones, the top one a photo.

  Provide it in the Store's logo sizes and as the app's tile and taskbar icon.
- **Screenshots:**
  - lead with search ("birthday cake" finding cakes);
  - then people, the map, a transcript beside a video, and Blurry photos with Sharpest frame.

### Positioning
Against Mylio, Excire, ACDSee and Microsoft Photos:
- private on-device AI (search by description, speech in videos, text in photos);
- built for very large libraries;
- made for iPhone photos in OneDrive (Live Photos, people).

## 6. Store-friendly services and APIs

Documented, licensed ways to cover what the app uses community servers or private APIs for today.

| Need | Now | Store-friendly options | Notes |
|---|---|---|---|
| Map tiles | tile.openstreetmap.org | **Azure Maps** (Render); **MapTiler**, **Stadia Maps**, **Thunderforest**; or **Protomaps** vector tiles on your own storage | Keep the OpenStreetMap attribution. Keys shipped in an app can be extracted: restrict them, or proxy. |
| Place names (parks, schools, restaurants) | Overpass (public) | **Overture Maps Places** (CDLA Permissive 2.0 and Apache 2.0) or **Foursquare Open Source Places** (Apache 2.0, 100M+ places, updated monthly): build compact place tiles and host them. Or **Azure Maps Search** (points of interest near a point: 5,000 free transactions a month, then per 1,000). | Pre-built tiles cost nothing per user and work offline. Check any API's terms on *storing* results: many geocoding APIs forbid keeping them long-term. |
| Towns | GeoNames download | The same file, **bundled in the package** (CC BY 4.0, credited) | No runtime download. |
| People and faces | OneDrive web API (private) | **On-device face detection and recognition**: OpenCV Zoo's YuNet (detection) and SFace (recognition), run with ONNX Runtime, then cluster the faces locally | Works without OneDrive and with any photos. Check each model's licence: avoid InsightFace or ArcFace weights, which are for non-commercial use only. Windows' own FaceAnalysis only finds faces; it doesn't recognise them. |
| Live Photo video | OneDrive web API (private) | **Local pairs**: the app already plays `.MOV`s stored beside the photo. iCloud for Windows and USB import keep them, and older OneDrive camera uploads did too (2,503 pairs in the author's library). | There's no documented OneDrive or Graph API for the video part (Graph's `format=video` returns 406). |
| OneDrive files and metadata | Microsoft Graph | **Microsoft Graph** (documented): files, thumbnails, the photo facet (date taken, camera, location) | Already used; needs a production app registration (§2.2). |
| Text in photos | Windows OCR | Keep it; on Copilot+ PCs, the **Windows AI text recognition** API is more accurate | Optional improvement. |
| Captions and search | CLIP (local) | Keep it; on Copilot+ PCs, **Windows AI image description** could add captions | Optional. |
| Speech | Whisper (local) | Keep it | Already Store-safe (models are downloaded data). |

## 7. After launch
- Watch health reports (crashes, hangs) and ratings, and answer reviews.
- Keep Windows App SDK, .NET, WebView2, ONNX Runtime and Whisper.net up to date (security fixes).
- Keep the tile key and provider plan in budget. Refresh place data if you host it.
- Watch the Store policies (currently version 7.20) for changes.

## 8. Rough timeline (focused work)

| Week | Work |
|---|---|
| 1 | MSIX and self-contained build, `STORE_SAFE` flag, remote off switch, Partner Center account and name, Entra app registration. |
| 2 | First-run page, download consent and hashes, About and notices, privacy policy page. |
| 3 | Map tile provider, place-names decision, codec and WebView2 detection, accessibility pass. |
| 4 | Clean-VM and WACK testing, listing assets, private flight to a few testers. |
| 5 | Fixes from the flight, submit, certification. |

## 9. Open questions for you
- Free or paid (and a trial)? Individual or company account?
- The brand name (§5).
- Where to host the remote off switch, privacy policy and support pages (GitHub Pages works for all three).
- Budget for map tiles, and possibly place data hosting (§6).
- Is ARM64 needed at launch?
- Who hosts the privacy policy and support pages (GitHub Pages works)?

## Sources
- [Microsoft Store Policies (v7.20)](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies): 10.1.1
  naming and first run, 10.2 security, 10.2.2 dynamic code, 10.2.4 dependencies, 10.2.9 installer URLs, 10.3
  testable, 10.4 usability, 10.5 privacy.
- [Free developer registration for individual developers](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)
- [Benefits of distributing through the Store (commerce fees)](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/why-distribute-through-store)
- [OSMF tile usage policy](https://operations.osmfoundation.org/policies/tiles/)
- [Overpass API: commons and fair use](https://dev.overpass-api.de/overpass-doc/en/preface/commons.html)
- [Overture Maps: attribution and licensing](https://docs.overturemaps.org/attribution/)
- [Foursquare Open Source Places](https://foursquare.com/resources/blog/products/foursquare-open-source-places-a-new-foundational-dataset-for-the-geospatial-community/)
- [Azure Maps pricing](https://azure.microsoft.com/en-us/pricing/details/azure-maps/)
