# Testing remote access from another computer

This is the plan for testing remote access (see the README) with two computers on the same network:

- **The host** is the computer with the library and the synced OneDrive. Photo Gallery runs there with remote access
  on.
- **The tester** is another Windows computer. It opens the host's library in a browser, and in Photo Gallery's
  *Another computer* page.

Three people take part:

| Who | Where | Does |
|---|---|---|
| **Grant** | both | Turns on remote access on the host. Gives the tester agent the host's name, passphrase and security code. Does anything that needs a human (clicking through a dialog, a phone test). |
| **Host agent** | Claude Code on the host, in `C:\Git\PhotoGallery` | Fixes bugs, pushes to `main`, and reads the host's `app.log`. |
| **Tester agent** | Claude Code on the tester | Sets up the tester, runs the tests below, and reports. Doesn't change code. |

## To start the tester agent

Grant: open Claude Code on the tester, in a clone of this repo, and paste:

> You're the tester agent for Photo Gallery's remote access. Read `docs/remote-testing.md` and follow it. The host
> computer is `<NAME>`, its security code is `<XXXX XXXX XXXX XXXX>`, and I'll give you the passphrase separately.
> Never write the passphrase into a file, a commit or the message channel.

## Rules for both agents

1. **The repo is public.** The following must never go in a commit, the message channel, or an issue:
   - the passphrase or session tokens;
   - personal details;
   - what's in photos, or personal file names;
   - screenshots.

   Test ids, pass or fail, error text, HTTP status codes and timings are fine. Screenshots stay on the tester; tell
   Grant where they are if he needs to see one.
2. **The host's library is real.** Anything that changes things happens only in the host's test folder,
   `C:\PhotoGalleryRemoteTest`. It holds copies of about 25 photos and a video, outside OneDrive. In the web app it
   shows under **Folders** as *C:\PhotoGalleryRemoteTest* (folders the library starts from show their full path).
   - **Delete, tag, rate, add to albums:** only items in that folder.
   - **Albums:** only an album you create, named "Remote test…". Delete it at the end.
   - **Tags:** only "remote-test…" tags, and only on test-folder items. Remove them at the end.
   - **People:** Hide then Show again; Rename to a test name then back to the exact original name. **Never Merge**
     real people; merging is covered by the automated tests.
   - **Duplicates:** only delete copies whose names start with `remote-test`.
   - **Ratings outside the test folder:** only as in B13, set back afterwards.
   - **Downloads:** delete anything you download from the host once you've checked it.
3. **Don't change the host's settings.** If a test needs a change there (port, passphrase, restart), ask Grant in the
   channel.
4. **Input only goes to the right window.** Scripted clicks or keys on the tester go only to Photo Gallery or the
   browser, only while that window is in the foreground. Stop if it isn't. If the tester is locked or the window
   can't be brought forward, ask Grant to do that step.
5. **The tester agent doesn't push code.** Report bugs in the channel, with steps, expected and actual results. A fix
   idea is welcome as text. The host agent fixes and pushes to `main`; then pull, rebuild (`PhotoGallery.bat`), and
   test again.

## Talking to each other

### The channel (always)
`tools/remote-testing/channel.ps1` keeps an append-only `log.md` on the `remote-testing` branch. It needs only git,
with push access to this repo.

```powershell
# read everything, or the last few messages
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 read
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 read -Last 3

# post (From is "tester" or "host")
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 send -From tester -Message "READY: tester set up, starting S1"

# wait up to 30 minutes for a reply (exit 0 with the new messages, or exit 1)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 wait -Minutes 30

# print each new message as it arrives (for a background monitor)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 watch
```

Message conventions:
- `READY`: set up, starting.
- `RESULTS <section>`: each test's id and PASS, FAIL or SKIP, with a line on each FAIL.
- `BUG-<n>: <title>`, then steps, expected, actual, error text, and how often it happens.
- `ASK GRANT: <what>`: needs a human.
- The host agent answers `FIXED BUG-<n> in <commit>: pull, run PhotoGallery.bat, retest <ids>`.

### Direct messages (when available)
If both Claude Code sessions are connected with Remote Control, `ListAgents` on one shows the other, and
`SendMessage` reaches it at once. The host session was named `photogallery-4e` when this was written; look for the
session working in `C:\Git\PhotoGallery` on the host. Use it to nudge the other agent ("posted RESULTS B"). Keep
the results in the channel so there's a record.

## On the host (Grant)

1. Start Photo Gallery with `PhotoGallery.bat`. It builds first, so it's the latest version.
2. In **Settings › Remote access**, set a passphrase and turn it on.
3. If Windows asks whether Photo Gallery may use the network, allow **private** networks. The network must be
   *Private* in Windows: check with `Get-NetConnectionProfile` in PowerShell, and fix under Settings › Network ›
   properties.
4. Give the tester agent three things: the computer name, the security code, and the passphrase. All three are shown
   or set in Settings.
5. Optional, for B9: **Connect** the OneDrive web session in Settings, so Live Photos stored only in OneDrive play.
6. For the F tests:
   - turn on **Let them change things too** under Remote access;
   - add `C:\PhotoGalleryRemoteTest` under Settings › Library folders (the host agent made it);
   - when testing is done, **Remove** that folder there, which also clears its rows, and turn changes off again if
     you like.

## On the tester (tester agent)

### Setup
1. **Prerequisites:**
   - Windows 10 or 11 on the same network as the host;
   - git, with push access to this repo (for the channel);
   - the [.NET 10 SDK](https://dotnet.microsoft.com/download);
   - Edge, and the WebView2 runtime (built into Windows 11).
2. **Build:** clone (or pull) the repo and build the CLI:
   `dotnet build tools/PhotoGallery.Cli`.
3. **Keep Photo Gallery on the tester from indexing this computer's pictures and downloading its AI models:** give it
   an empty test library before its first start. Skip this if Photo Gallery is already set up on this computer and
   should keep its own library.

   ```powershell
   $dir = Join-Path $env:LOCALAPPDATA 'PhotoGallery'
   New-Item -ItemType Directory -Force $dir, 'C:\PhotoGalleryTest\library' | Out-Null
   $file = Join-Path $dir 'settings.json'
   if (Test-Path $file) { Copy-Item $file "$file.before-remote-test" }
   @{ LibraryRoots = @('C:\PhotoGalleryTest\library'); TranscribeInBackground = $false; ReadPhotoTextInBackground = $false;
      FindSimilarInBackground = $false; Version = 2 } | ConvertTo-Json | Set-Content -Encoding UTF8 $file
   ```

   Afterwards, restore `settings.json.before-remote-test` if you made one.
4. Post `READY` to the channel.

### S: smoke test (automatic)
The passphrase goes in an environment variable for that one command only; don't echo it.

```powershell
$env:PG_REMOTE_PASSPHRASE = '<passphrase from Grant>'
dotnet run --no-build --project tools/PhotoGallery.Cli -- remote-check <HOST> --code "<XXXX XXXX XXXX XXXX>"
Remove-Item Env:PG_REMOTE_PASSPHRASE
```

- It checks, among others:
  - reaching the host, and that its security code matches;
  - that requests without a session, for another host name, or from a form are refused;
  - signing in;
  - the lists, search, people and albums;
  - details and thumbnails, photos at screen size, a video range and a Live Photo video;
  - signing out.
- It prints PASS, FAIL or INFO lines with timings, and never the passphrase or token.
- It changes nothing on the host, though it may make it fetch one Live Photo video.
- Post the whole output as `RESULTS S`.

If S1 fails, check:
- `Test-NetConnection <HOST> -Port 47813`;
- that the name resolves (`Resolve-DnsName <HOST>`; if not, use the host's IP address);
- remote access is on, and the firewall allows it (host steps 2 and 3).

### B: in a browser (Edge)
Open `https://<HOST>:47813`. You can do these by hand through Grant, or drive Edge yourself. Headless Edge with
`--ignore-certificate-errors`, controlled through its DevTools protocol, works well; only point it at the host.

| Id | Do | Expect |
|---|---|---|
| B1 | Open the address | A certificate warning once (the host made its own certificate). After continuing, the sign-in page says "on <HOST>". The certificate's SHA-256 fingerprint starts with the security code. |
| B2 | Sign in with a wrong passphrase | "That passphrase isn't right." |
| B3 | Sign in with the right one | The timeline. Photo and video counts under the title match the host (the host's Settings shows them under Library). |
| B4 | Scroll fast through years, drag the scrollbar | Month headings. A date label shows while scrolling. Tiles fill in within a second or two, with no gaps left behind. |
| B5 | − / +, and *By day / month / year / No groups* | Tile size and grouping change; the photo at the top stays in view. |
| B6 | Open a photo (HEIC if possible), then ← / → | A blurry preview, then a sharp photo, in about a second on a wired network. Neighbours load fast. |
| B7 | Details (I) | Date, place, people (links), file, camera, text in the photo when there is some. |
| B8 | Open an iPhone video (MOV, usually HEVC); seek; click a transcript line | Plays and seeks. The transcript line jumps there. If it can't play, it says so and offers the download (note the browser, and whether the HEVC extension is installed). |
| B9 | A Live Photo: LIVE button, or Space | The motion plays over the still. For one stored only in OneDrive: it plays if the host's OneDrive web session is connected, otherwise a clear message. |
| B10 | Search "birthday cake"; then Exact words; then Newest first and By month | Best matches first; exact narrows the results; sorting by date and grouping work. |
| B11 | Photos only / Videos only; People filter with two people; remove a chip | The list narrows (photos with both people); removing restores it. |
| B12 | People → a person; Albums → an album; On this day; Favorites; Live Photos; Videos | Each section loads; faces or covers show. |
| B13 | On a photo with no stars: press 3, check the tile, then press 0 | Three stars on the tile, then none. Report the id (`view=` in the address) so the host agent can confirm it's back to none. |
| B14 | Download the original | The file downloads with its own name. Delete it afterwards. |
| B15 | Browser Back and Forward | Back closes the viewer, then goes to the previous section at the same scroll position. |
| B16 | A window under 760 px wide | Menu button and a sliding menu; the viewer's details at the bottom. |
| B17 | Sign out (top right) | Back to the sign-in page. |

### A: in Photo Gallery on the tester (*Another computer*)
Start Photo Gallery with `PhotoGallery.bat`, then choose **Another computer** at the bottom of the menu.

| Id | Do | Expect |
|---|---|---|
| A1 | Connect to a name that doesn't exist | A readable "couldn't reach" message. |
| A2 | The host's name and a wrong passphrase | The security-code dialog first (the first time). After "The codes match", the host's page shows "That passphrase isn't right." The passphrase isn't saved. |
| A3 | Disconnect, then connect with the right passphrase, "Remember" on | No code dialog now (the code is pinned). The library opens with no certificate warning, the bar says "Photos on <HOST>", and the app's menu folds away. |
| A4 | The title bar's search box (Ctrl+E): search, then Esc or clear | It searches the host's photos, and clearing goes back. |
| A5 | Open a photo, then the title bar's Back, twice | It closes the viewer, then goes back within the host's page. |
| A6 | Settings › Appearance: Light, then Dark; back to Another computer | The host's page follows the theme. |
| A7 | Reload; Disconnect; Connect again | Signs in by itself with the saved passphrase. |
| A8 | "Open this computer's photos when Photo Gallery starts" on; restart the app | Opens straight to the host's photos. |
| A9 | Ask Grant to restart Photo Gallery on the host, then press Reload | It signs in again by itself (the host forgot its sessions). |
| A10 | Close the app. Back up `%LOCALAPPDATA%\PhotoGallery\settings.json`, then change the host's value under `RemotePins` to 64 zeros. Connect. | "The security code changed" dialog, with Cancel as the default. Cancel means not connected. Restore the file afterwards. |
| A11 | Details → the coordinates link | Opens in the usual browser, not inside the app. |
| A12 | Download the original | WebView2's download bar; the file is in Downloads. Delete it afterwards. |
| A13 | "Forget the saved passphrase" | Removed from Credential Manager (*Web Credentials*, "Photo Gallery remote access"). The next connect asks for it. |

### F: doing things from the client (phase 1)
These need host step 6. Follow rule 2: change nothing outside the test folder.

| Id | Do | Expect |
|---|---|---|
| F1 | With changes off on the host, select a photo | Only Rate shows. Add to album, Tag and Delete are gone, and in the viewer the album and delete buttons too. |
| F2 | Changes on. Folders › C:\PhotoGalleryRemoteTest. Hover a tile, click its circle; Shift-click another; Ctrl+A; Esc | Selection bar with the count; a run selected; everything; cleared. |
| F3 | Select 3 test photos › Rate › ★★★ | Stars on the tiles; still there after reloading the page. |
| F3b | Right-click a test photo › ★★; right-click it again › Clear rating; select two › press 4, then 0; in the viewer, Clear next to the stars | The menu opens at the pointer with the current stars marked (Esc closes it). The tiles' stars follow each change; Clear rating is greyed out when there are none. |
| F4 | Select 2 › Add to album › New album… "Remote test" | "Added 2 items…". Albums lists it with a cover. In the album: select one › Remove from album; Rename; Delete album (the photos stay). |
| F5 | Select 2 › Tag "remote-test" | Tags lists it; the tag's page shows the 2. In the viewer's details the tag has an ×; remove it there. |
| F6 | Select 1 › Delete (or the Del key) | A question naming the host's Recycle Bin, then the photo leaves the list, and the count updates. On the host it's in the Recycle Bin. |
| F7 | Viewer on a test photo: Delete (Del) | Goes to the next photo. |
| F8 | Viewer: Similar photos button | "Similar photos": that photo first, then look-alikes. |
| F9 | Viewer: F (faces) and T (text) on a photo with people or text | Outlines with names; clicking a face opens that person. Text boxes; in a search's results, matching words are highlighted without T. |
| F10 | Viewer: mouse wheel, double-click, drag; on a touch screen, pinch | Zooms around the pointer, pans, a sharper picture loads when zoomed; Esc zooms out first. |
| F11 | Viewer details: the find box, on a photo with text or a video with speech | Matches highlighted; the first scrolls into view. |
| F12 | Filters: rating, Live Photos only, utility shots (leave out / include / only), photos or videos | The list narrows; "Filters (n)"; Clear filters. Only utility shots: screenshots and photos of receipts, documents, screens… |
| F13 | Map | Clusters. The list below shows the photos in view and follows panning. Click a cluster: just its photos. Click the map: back to all in view. Zoomed right in, thumbnails as pins; clicking one opens it. |
| F14 | Folders, then a folder; untick Include subfolders | Its photos, then without subfolders. |
| F15 | Tags | Your tags first, then OneDrive's; each opens its photos. |
| F16 | People › Show › each choice; an unnamed person › Known, but don't tag, then Look at again in Who's this?; Not someone I know, then Show again | Each works and shows at once. **Don't name, rename or join ("Same person as…") real people**: names and joins now go to Grant's OneDrive. Setting aside stays on the host. |
| F16b | People › Who's this?: look at a few (faces, years, "See all"), click a face, Skip, Back; type the first letters of a named person | Faces from different years; a face opens that photo, Back returns to the same person. Typing a named person's name turns the button into "Same person as …" with their face. **Don't press it**, and don't name anyone (both go to OneDrive): Skip instead. |
| F17 | Duplicates › Find duplicates | Progress, then groups ("Keep" on one). The two `remote-test-… (copy)` files show as exact copies. Delete the extra copy of **those** only. |
| F18 | Blurry photos | Blurriest first; grouping off. |
| F19 | Type 2+ letters in the search box | Suggestions include people, places and tags. |
| F20 | In the app on the tester (Another computer): repeat F3, F6 and F13 | The same, inside the app. |
| F21 | Map › the Find a place box: type a town ("spokane", "paris, france"), a park near your photos; press Enter; then "Search OpenStreetMap for …" with an address | Matches from the host as you type (your places, places near photos, towns biggest first). Choosing one moves the map there with a labelled pin, and the list below shows the photos there. OpenStreetMap finds addresses and landmarks. |
| F22 | Viewer › Details (I) on a test photo: Kind › untick or tick Utility shot, then Let the photo computer decide; then select two test photos › Utility shots › each choice | "Chosen by hand." and the button appear, then go. A photo marked as a utility shot leaves the timeline (and shows with Only utility shots). Works with changes off on the host too, like ratings. Put the test photos back with Let the photo computer decide. |

### G: editing from the client (phase 2)
Only on items in the test folder (rule 2). Everything saved lands next to the original there, and shows up in the
folder after a moment.

| Id | Do | Expect |
|---|---|---|
| G1 | Viewer on a test photo › Edit (E) | The editor, with the preview rendered by the host within a second or so. With changes off on the host, there's no Edit button. |
| G2 | Move each slider; double-click one; Auto; Reset | The preview follows within about half a second; double-click resets it; Auto sets light and colour. |
| G3 | Rotate left and right ([ and ]), Flip | The preview turns and mirrors. |
| G4 | Crop: drag the corners and the middle; choose Square, then 16:9; Whole photo; Done (Enter) | The box follows the pointer and keeps the shape; Done shows the cropped photo. |
| G5 | Save as copy (Ctrl+S) | "Saved <name>_1.jpg next to the original." The new photo appears in the folder, and matches the preview. |
| G6 | Edit another › Keep in gallery | The photo shows with the edits (in the grid too, after a moment); no new file. Edit again and Reset everything, Keep: back to the original. |
| G7 | On a test JPEG: Overwrite original | A question; then the photo shows the edits, and the old version is in the host's Recycle Bin. For HEIC there's no Overwrite. |
| G8 | Edit, change something, Esc | "Discard your changes?"; Discard closes without saving. |
| G9 | The test video in the viewer: play it, pause, , and . | Steps a frame back and forward. |
| G10 | Save frame (S, or the camera button) | "Saved …_frame_….jpg"; the new photo appears in the folder. |
| G11 | Sharpest frame (the sparkle button) on the video, and on a Live Photo if the test folder has one | Pauses on a sharp frame with its time; for a Live Photo its video appears, paused there. Save frame keeps it. |
| G12 | Trim (scissors): Start here (I), End here (O), turn right, Remove the sound, Save as MP4 | A progress bar, then "Saved … next to the original."; the new MP4 is trimmed, turned and silent. Closing while it saves asks to stop. |
| G13 | In the app on the tester (Another computer): G1, G5 and G12 | The same, inside the app. |

### X: refusals (last, since X1 locks the tester out for a minute)
| Id | Do | Expect |
|---|---|---|
| X1 | Five wrong passphrases in the browser, then the right one | "Too many wrong passphrases. Try again in 60 seconds." After a minute, the right one works. |
| X2 | Covered by the smoke test | S3 to S5. |

### P: how it feels (write numbers down)
- Wired or Wi-Fi.
- Time to the first tiles of the timeline.
- How long tiles take to fill after a fast scroll.
- Opening a photo.
- Starting a video.
- Compare with the host's own Photo Gallery if Grant can.

## Known limits (not bugs)
- No map from another computer yet.
- iPhone videos (HEVC) play only where the browser has the codec: Edge and Chrome on Windows with *HEVC Video
  Extensions*, or hardware support. The download always works.
- The host must be on and awake, with Photo Gallery running. Restarting it signs everyone out: browsers ask again,
  and the app signs in again by itself.
- Remote users can always rate. Editing, deleting, tags, albums and people need the host's "Let them change things
  too".
- Host-only, by design: Settings (library folders, OneDrive, background work, remote access itself), Show in
  Explorer, and exporting to a place of your choice (Download the original instead).
- Only computers on the local network can connect (not over the internet, VPN or Tailscale).

## For the host agent
- The host's log: `%LOCALAPPDATA%\PhotoGallery\app.log`. Remote access writes "Remote access: …" lines (start,
  sign-ins, failures). It never logs passphrases or tokens.
- Watch the channel with the `watch` command in a background monitor, and answer bug reports there.
- Run the tests (`dotnet test --project tests/PhotoGallery.Core.Tests`) before pushing a fix. Then tell the tester
  which ids to retest.
