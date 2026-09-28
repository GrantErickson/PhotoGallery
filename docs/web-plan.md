# Photo Gallery on the web: what it could be

## Why OneDrive online feels worse, and what makes this app good

OneDrive in a browser pages through photos slowly, searches only a little, and knows nothing about what's in the
videos. This app feels different for three reasons, and a web version has to keep all three:

1. **The whole library as one list.** All 236,852 items arrive in under a second (1.4 MB compressed), and the grid
   only draws what's on screen. Jumping to 2006 fills in about 0.16 s later.
2. **An index nobody else has**, made once on your PC's graphics card:
   - what's *in* every photo (CLIP), and what's said in every video (Whisper);
   - the text in photos, the places, and OneDrive's people;
   - sharpness and duplicates.

   That's many GPU-hours of work, and the index is what makes search by description, transcripts and the rest
   possible.
3. **Heavy lifting next to the photos.** HEIC decoding, edits and video trimming all happen where the files are.

The remote access built this week already *is* a web app with all of that. The PC is its server. So the question
isn't how to rebuild it for the web. It's how to reach it from anywhere, and what happens when the PC is off.

## Three ways to put it on the web

### A. Your PC stays the server, reachable from anywhere (days)
The web app as it is today, plus a private way in from outside the house.

- **Tailscale** (recommended). It's a private network between your own devices (WireGuard), free for personal use.
  - Install it on the PC and on the phone or laptop; `https://keller:47813` then works from anywhere, like at home.
  - Nothing is exposed to the internet, and no third party sees the traffic.
  - The only app change: remote access needs a setting to also accept Tailscale's IPv4 addresses (100.64.0.0/10),
    which the local-network check refuses today. Its IPv6 addresses (fd7a:115c:a1e0::/48) already pass, and its
    names (keller, keller.<tailnet>.ts.net) pass the host-name check.
- **Cloudflare Tunnel with Cloudflare Access** (alternative).
  - A real web address, with a sign-in (a one-time email code, or a Microsoft account) in front of the passphrase.
  - No open ports on the router.
  - Cloudflare does see the traffic, since it runs the HTTPS.
- **Install it like an app** (a PWA): an icon on the phone's home screen, full screen, and recently viewed thumbnails
  kept for when the connection is poor.

What you get: everything, including AI search, transcripts, editing and the map, at no running cost.

What it costs you:
- the PC has to be on and awake;
- the home connection's upload speed decides how fast it feels away from home. Thumbnails are ~23 KB and photos
  ~300–600 KB at screen size, so a 20 Mbps upload feels fine; long videos less so.

### B. The index goes to the cloud; the photos come from OneDrive (weeks)
For when the PC is off. The PC keeps doing the expensive AI work, and publishes what it learned. A web app in the
browser (on any static host, even GitHub Pages) reads it, and gets the photos themselves straight from OneDrive.

| Piece | Where it lives | Size for this library |
|---|---|---|
| The list (ids, dates, kinds, ratings) | Your OneDrive's app folder, as JSON shards by year | ~1.5 MB compressed |
| Details: people, places, tags, text in photos, transcripts | The app folder, by year | tens of MB, loaded as needed |
| Words index (exact search) | A read-only SQLite file, read in the browser a page at a time (sql.js-httpvfs over range requests). From the app folder if OneDrive's download links allow those requests from a web page (to be checked); otherwise from Azure Blob storage (cents a month) | ~150 MB, but a search reads only a few KB |
| Picture index (search by description, similar photos) | The app folder: 250k × 256 numbers (reduced from 768), int8 | ~64 MB, cached in the browser |
| Thumbnails, photos, videos | OneDrive itself, through Microsoft Graph (thumbnails and downloads) | nothing extra |

- **Sign-in:** the Microsoft account, in the browser (MSAL.js), with Files.Read and the app folder.
- **Search by description:**
  - CLIP's text half runs in the browser: ONNX Runtime Web on WebGPU, ~125 MB quantized, downloaded once and cached;
  - it turns the words into numbers in ~0.1–0.3 s (faster with WebGPU);
  - then comparing against 250k photos takes ~50 ms in a Web Worker. Reducing to 256 numbers loses a little
    accuracy; to be measured against today's search.
- **The same web app**: it gets a second backend. Today's page asks the PC's API. The cloud version asks the
  published index and Graph instead. One UI, two sources, and it can pick the PC when it's reachable (everything,
  faster) and the cloud copy when not.
- **Publishing:** the PC uploads the changes to the app folder after each background pass. That's small: new items,
  new transcripts. It never sends the photos, which OneDrive already has.

What you get: most of the app, with the PC off, from any browser. Nothing sits on anyone's servers but your own
OneDrive.

What it costs you, and the catches:
- real engineering: the second backend, publishing, and the browser model;
- Graph's rate limits on thumbnails: a browser cache, and batching;
- Live Photos stored only in OneDrive can't play here (that needs the private web session; local pairs can);
- editing would be in the browser (canvas or WebGL) and saved back through Graph, a later step.

### C. A full cloud service (months, and ongoing)
Like Google Photos on top of OneDrive: a cloud backend indexes everyone's OneDrive with cloud GPUs.

- The GPU work itself is cheap: CLIP for 250k photos is well under an hour on a small cloud GPU. Transcribing 10,000
  videos is more, but still dollars, not hundreds.
- The expensive parts are the rest:
  - running a service;
  - storing people's thumbnails and indexes;
  - security, and a privacy policy that holds up when you're the one holding everyone's photos;
  - support.

Only worth it as a business.

## Recommendation

1. **Now: A.** Add "Also allow Tailscale" to remote access, make the web app installable (a manifest and a small
   service worker for the thumbnails), and polish the phone layout. A day or two, and the app works from anywhere,
   with everything.
2. **Next: B, if a PC-off version matters.** Start with read-only browsing and search, then the map and people, then
   editing. Split the web app's data calls behind one interface first; that's worth doing anyway.
3. **C only as a product decision.** It fits the Store plan's paid version better than a personal tool.

## Open questions for you
- Is "the PC must be on" acceptable for now? (Grandma-proof: a scheduled wake, or a small always-on mini PC as the
  host.)
- Tailscale on the phone: fine, or would a plain web address (Cloudflare) be friendlier for family?
- Should family members get their own sign-in? Remote access has one passphrase today; per-person passphrases (and
  "view only" for some) are straightforward.
