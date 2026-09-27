# Publishing to the Microsoft Store: plan

What it would take to put Photo Gallery in the Microsoft Store, in order. It covers the decisions to make, the things
that would block certification or cause trouble later, the engineering work, the Partner Center steps, and a rough
timeline. Written September 2026; check the linked policies before submitting, as they change.

## Summary

It's feasible, and most of the app can ship as it is. There are three problems to solve first:

1. **The OneDrive web-session features have to come out of the Store build.** These are Live Photo videos stored
   only in OneDrive, and people, names and faces from OneDrive. They work by reading the authorization header of the
   user's own OneDrive web session and calling undocumented OneDrive endpoints. That is a poor fit for Store Policy
   10.2 (security) and for OneDrive's terms, and it could break whenever OneDrive changes.
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
| Features in v1 | See §2 | Ship without the web-session features. Place names off (or served by your own data). |
| Architectures | x64, ARM64 | x64 first. ARM64 after checking the native parts (§3, item 11). |
| Minimum Windows | Currently 10.0.19041 | Keep it. Windows 10 needs WebView2 checks (§2.7). |

## 2. Blockers and risks

### 2.1 OneDrive web session (must change)
- **What it does:** a WebView2 loads onedrive.live.com with the user's sign-in, captures the `Authorization` header
  of its API calls, and uses it (in memory only) for:
  - Live Photo videos stored only in OneDrive;
  - people, names and face boxes.
- **Why it's a problem:**
  - Policy 10.2: products must not "jeopardize or compromise user security", and a reviewer may read token capture
    that way.
  - The endpoints are undocumented and can change without notice.
  - It may conflict with OneDrive's terms of use.
- **Action:**
  - Add a `STORE` build flag that removes the Connect page and the web-session clients.
  - In the Store build:
    - cloud-only Live Photos show the still, with "Open in OneDrive" (already the fallback);
    - people come from the Microsoft Graph metadata sync (the older, pre-merge data), or are left out.
  - Revisit if Microsoft documents a Live Photo or people API.

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

### 2.8 First run and certification testing
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
3. **Store build flag** (`STORE`): removes the web-session features (§2.1) and the development client ID.
4. **App data under MSIX.** Writes to `%LocalAppData%\PhotoGallery` are redirected to the package's private storage.
   The app keeps working unchanged, but:
   - an existing unpackaged install's database and caches aren't visible. Offer "import existing data", or accept a
     fresh index;
   - uninstalling removes about 6 GB of thumbnails, a 1 GB database and 2.5 GB of models. Say so in the listing and
     the FAQ;
   - "Reset" in Windows Settings clears everything.
5. **First-run experience** (§2.8), plus a way to rerun it from Settings.
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

## 5. After launch
- Watch health reports (crashes, hangs) and ratings, and answer reviews.
- Keep Windows App SDK, .NET, WebView2, ONNX Runtime and Whisper.net up to date (security fixes).
- Keep the tile key and provider plan in budget. Refresh place data if you host it.
- Watch the Store policies (currently version 7.20) for changes.

## 6. Rough timeline (focused work)

| Week | Work |
|---|---|
| 1 | MSIX and self-contained build, `STORE` flag, Partner Center account and name, Entra app registration. |
| 2 | First-run page, download consent and hashes, About and notices, privacy policy page. |
| 3 | Map tile provider, place-names decision, codec and WebView2 detection, accessibility pass. |
| 4 | Clean-VM and WACK testing, listing assets, private flight to a few testers. |
| 5 | Fixes from the flight, submit, certification. |

## 7. Open questions for you
- Free or paid (and a trial)? Individual or company account?
- The brand name.
- Ship v1 without the OneDrive web-session features? (Recommended.)
- Budget for map tiles, and possibly place data hosting.
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
