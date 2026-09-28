"use strict";
// Photo Gallery, opened from another computer: the library's timeline, search, people, albums, tags, folders, the map,
// duplicates and blurry photos, with selecting, organizing and a viewer. Talks only to the computer it was loaded from
// (see RemoteServer*.cs for the API).
(() => {
  const $ = id => document.getElementById(id);
  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  };
  const icon = name => {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#i-" + name);
    svg.append(use);
    return svg;
  };
  const button = (text, iconName, onClick, className) => {
    const b = el("button", className);
    b.type = "button";
    if (iconName) b.append(icon(iconName));
    b.append(document.createTextNode(text));
    b.addEventListener("click", onClick);
    return b;
  };
  const store = {
    get(key, fallback) {
      try {
        const value = localStorage.getItem("pg." + key);
        return value == null ? fallback : JSON.parse(value);
      } catch {
        return fallback;
      }
    },
    set(key, value) {
      try {
        localStorage.setItem("pg." + key, JSON.stringify(value));
      } catch {
        // private window or storage blocked: fine, it's only a preference
      }
    },
  };

  const params = new URLSearchParams(location.search);
  const embedded = params.get("embedded") === "1";
  if (embedded) document.body.classList.add("embedded");
  // Inside Photo Gallery on another computer: tells the app about signing in and out.
  const report = message => {
    if (embedded) window.chrome?.webview?.postMessage(message);
  };

  const EMPTY = { ids: [], dates: [], flags: [] };
  const state = {
    name: "",
    changes: false,
    listKey: null,
    route: null,
    loadToken: 0,
    items: EMPTY,
    group: "month",
    yearsAgo: false,
    rows: [],
    rendered: new Map(),
    cols: 0,
    tile: 0,
    tileSize: store.get("tileSize", window.innerWidth < 700 ? 110 : 180),
    people: null,
    albums: null,
    tags: null,
    folders: null,
    scrollMemory: new Map(),
    lastList: "#/",
    selected: new Set(),
    anchor: null,
  };

  // ---------- Server ----------

  async function api(path, options = {}) {
    const init = { credentials: "same-origin", ...options, headers: { ...(options.headers || {}) } };
    if (init.method && init.method !== "GET") init.headers["X-Photo-Gallery"] = "1";
    if (init.body && typeof init.body !== "string") {
      init.body = JSON.stringify(init.body);
      init.headers["Content-Type"] = "application/json";
    }
    const response = await fetch(path, init);
    if (response.status === 401 && path !== "/api/login") {
      showLogin("Signed out. Enter the passphrase again.");
      throw new Error("Signed out.");
    }
    return response;
  }

  async function json(path, options) {
    const response = await api(path, options);
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      const error = new Error(body.error || `The photo computer answered ${response.status}.`);
      error.status = response.status;
      throw error;
    }
    return body;
  }

  const post = (path, body = {}) => json(path, { method: "POST", body });

  // Photos edited from here get a new address, so the browser doesn't show what it kept from before.
  const bust = new Map();
  const thumbUrl = id => `/api/media/${id}/thumb` + (bust.has(id) ? `?v=${bust.get(id)}` : "");
  const displayUrl = (id, size) => `/api/media/${id}/display?size=${size}` + (bust.has(id) ? `&v=${bust.get(id)}` : "");
  const displaySize = () => {
    const pixels = Math.max(screen.width, screen.height) * (window.devicePixelRatio || 1);
    return Math.min(4096, Math.max(1024, Math.ceil(pixels / 512) * 512));
  };

  const getPeople = async () => (state.people ??= await json("/api/people"));
  const getAlbums = async () => (state.albums ??= await json("/api/albums"));
  const getTags = async () => (state.tags ??= await json("/api/tags"));
  const getFolders = async () => (state.folders ??= await json("/api/folders"));

  // ---------- Formatting (dates are the local time the photo was taken, sent as if UTC) ----------

  const formats = {
    month: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", month: "long", year: "numeric" }),
    day: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", weekday: "short", month: "short", day: "numeric", year: "numeric" }),
    date: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", month: "short", day: "numeric", year: "numeric" }),
    full: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", weekday: "short", month: "short", day: "numeric", year: "numeric", hour: "numeric", minute: "2-digit" }),
    long: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", weekday: "long", month: "long", day: "numeric", year: "numeric", hour: "numeric", minute: "2-digit" }),
  };
  const number = n => n.toLocaleString();
  const plural = (n, one, many) => `${number(n)} ${n === 1 ? one : many}`;
  const duration = seconds => {
    seconds = Math.round(seconds);
    const h = Math.floor(seconds / 3600), m = Math.floor(seconds / 60) % 60, s = seconds % 60;
    return (h ? `${h}:${String(m).padStart(2, "0")}` : `${m}`) + `:${String(s).padStart(2, "0")}`;
  };
  const fileSize = bytes => bytes >= 1 << 30 ? `${(bytes / (1 << 30)).toFixed(1)} GB`
    : bytes >= 1 << 20 ? `${(bytes / (1 << 20)).toFixed(1)} MB`
      : bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`;
  const describe = (count, videos) => videos === 0 ? plural(count, "photo", "photos")
    : videos === count ? plural(count, "video", "videos") : `${number(count)} photos and videos`;

  function toast(message) {
    const node = $("toast");
    node.textContent = message;
    node.hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => (node.hidden = true), 5000);
  }

  const fail = error => {
    if (error?.message !== "Signed out.") toast(error?.message || String(error));
  };

  // ---------- Questions: a name, a choice or a yes ----------

  /**
   * Asks something. With input: resolves to the text (or null). With choices ({label, hint, value}): resolves to the
   * value chosen (or null). Otherwise it's a yes-or-no question: resolves to true or false.
   */
  function ask({ title, text = "", input = null, value = "", options = [], choices = null, ok = "OK", danger = false }) {
    return new Promise(resolve => {
      const back = $("dialog"), form = $("dialog-form"), field = $("dialog-input");
      $("dialog-title").textContent = title;
      $("dialog-text").textContent = text;
      field.hidden = input === null;
      field.placeholder = input || "";
      field.value = value;
      $("dialog-options").replaceChildren(...options.map(o => Object.assign(el("option"), { value: o })));
      const list = $("dialog-choices");
      list.hidden = !choices;
      list.replaceChildren();
      $("dialog-ok").hidden = !!choices && input === null;
      $("dialog-ok").textContent = ok;
      $("dialog-ok").classList.toggle("danger-ok", danger);
      let done = false;
      const finish = result => {
        if (done) return;
        done = true;
        back.hidden = true;
        form.onsubmit = null;
        document.removeEventListener("keydown", onKey, true);
        resolve(result);
      };
      const onKey = e => {
        if (e.key === "Escape") {
          e.preventDefault();
          e.stopPropagation();
          finish(input !== null || choices ? null : false);
        }
      };
      for (const choice of choices || []) {
        const b = button(choice.label, choice.icon, () => finish(choice.value));
        if (choice.hint) b.append(el("span", "muted", choice.hint));
        list.append(b);
      }
      form.onsubmit = e => {
        e.preventDefault();
        if (input !== null) {
          const answer = field.value.trim();
          finish(answer || null);
        } else finish(true);
      };
      $("dialog-cancel").onclick = () => finish(input !== null || choices ? null : false);
      back.onclick = e => {
        if (e.target === back) finish(input !== null || choices ? null : false);
      };
      document.addEventListener("keydown", onKey, true);
      back.hidden = false;
      (input !== null ? field : choices ? list.querySelector("button") : $("dialog-ok"))?.focus();
      if (input !== null) field.select();
    });
  }

  /** Picks an album (or makes one): resolves to {id, name} or null. */
  async function chooseAlbum(title) {
    const albums = await getAlbums().catch(() => []);
    const choice = await ask({
      title,
      choices: [{ label: "New album…", icon: "plus", value: "new" },
        ...albums.map(a => ({ label: a.name, hint: number(a.count), value: a.id }))],
    });
    if (choice == null) return null;
    if (choice !== "new") return albums.find(a => a.id === choice);
    const name = await ask({ title: "New album", input: "Album name", ok: "Create" });
    if (!name) return null;
    const created = await post("/api/albums", { name });
    state.albums = null;
    return { id: created.id, name };
  }

  // ---------- Sign in ----------

  async function start() {
    try {
      const hello = await json("/api/hello");
      state.name = hello.name;
      setChanges(hello.changes);
      document.title = `Photos on ${hello.name}`;
      $("brand-name").textContent = `Photos on ${hello.name}`;
      if (hello.signedIn) showApp();
      else showLogin();
    } catch (error) {
      showLogin(error.message);
    }
  }

  function setChanges(allowed) {
    state.changes = !!allowed;
    document.body.classList.toggle("read-only", !state.changes);
  }

  function showLogin(message) {
    closeViewer(true);
    $("app").hidden = true;
    $("login").hidden = false;
    state.listKey = null;
    state.people = state.albums = state.tags = state.folders = null;
    $("login-host").textContent = state.name ? `on ${state.name}` : "";
    $("login-error").textContent = message || "";
    $("passphrase").focus();
    report({ signedIn: false, signedOut: true }); // the app may sign in again with the passphrase it saved
  }

  $("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    const submit = event.submitter;
    if (submit) submit.disabled = true;
    $("login-error").textContent = "";
    try {
      await post("/api/login", { passphrase: $("passphrase").value });
      $("passphrase").value = "";
      showApp();
    } catch (error) {
      $("login-error").textContent = error.message;
      $("passphrase").select();
    } finally {
      if (submit) submit.disabled = false;
    }
  });

  $("sign-out").addEventListener("click", async () => {
    await api("/api/logout", { method: "POST" }).catch(() => {});
    showLogin();
  });

  async function showApp() {
    $("login").hidden = true;
    $("app").hidden = false;
    // Whether changes are allowed can change on the host while signed in.
    json("/api/hello").then(hello => setChanges(hello.changes)).catch(() => {});
    updateSuggestions("");
    routeNow();
  }

  // ---------- Routes: #/section[/id][?params], with view=<id> while the viewer is open ----------

  function parseHash() {
    const hash = location.hash.replace(/^#\/?/, "");
    const [path, query = ""] = hash.split("?");
    const parts = path.split("/").filter(Boolean);
    return { section: parts[0] || "photos", id: parts[1] || null, params: new URLSearchParams(query) };
  }

  function hashOf(route) {
    const query = route.params.toString();
    return "#/" + [route.section === "photos" ? "" : route.section, route.id].filter(Boolean).join("/") + (query ? "?" + query : "");
  }

  function listKeyOf(route) {
    const copy = new URLSearchParams(route.params);
    copy.delete("view");
    return hashOf({ ...route, params: copy });
  }

  /** Changes the address without a new history entry (filters, sorting, the next photo) and follows it. */
  function replaceRoute(route) {
    history.replaceState(null, "", hashOf(route));
    routeNow();
  }

  const go = hash => (location.hash = hash);

  window.addEventListener("hashchange", routeNow);

  async function routeNow() {
    if ($("app").hidden) return;
    const current = parseHash();
    const key = listKeyOf(current);
    if (key !== state.listKey) {
      if (state.listKey != null && !$("scroller").hidden) state.scrollMemory.set(state.listKey, $("scroller").scrollTop);
      state.listKey = key;
      state.route = current;
      if (current.section !== "search") state.lastList = key;
      $("app").classList.remove("nav-open");
      await showSection(current, key);
    }
    const view = Number(current.params.get("view"));
    if (view) openViewerById(view);
    else closeViewer(true);
  }

  // ---------- Sections ----------

  const lists = {
    photos: { title: "Photos", query: { section: "timeline" }, empty: "No photos yet. They're indexed on the photo computer; check back soon." },
    onthisday: { title: "On this day", group: "year" },
    favorites: { title: "Favorites", query: { section: "favorites" }, subtitle: "Rated 4 stars or more", empty: "Rate photos with the stars in the viewer (or keys 1–5), and your 4 and 5 star photos show up here." },
    live: { title: "Live Photos", query: { section: "live" }, subtitle: "iPhone Live Photos and Android motion photos", empty: "No Live Photos found." },
    videos: { title: "Videos", query: { section: "videos" }, empty: "No videos found." },
    blurry: { title: "Blurry photos", query: { section: "blurry" }, group: "none", subtitle: "Blurriest first · Filters › Live Photos only finds the ones whose video may have a sharper frame", empty: "No blurry photos (or they haven't all been measured yet on the photo computer)." },
  };
  const pages = new Set(["people", "albums", "tags", "folders", "duplicates", "who"]);

  function setNav(section) {
    const selected = { person: "people", who: "people", album: "albums", tag: "tags", folder: "folders", area: "map", similar: null }[section] ?? section;
    for (const link of document.querySelectorAll(".nav a")) link.classList.toggle("selected", link.dataset.section === selected);
  }

  function showPane(name) {
    $("scroller").hidden = !(name === "grid" || name === "map");
    $("map-pane").hidden = name !== "map";
    $("cards-page").hidden = name !== "cards";
    $("page").hidden = name !== "page";
    $("controls").hidden = !(name === "grid" || name === "map");
    $("main").classList.toggle("map-mode", name === "map");
    if (name !== "grid" && name !== "map") $("chips").replaceChildren();
  }

  async function showSection(route, key) {
    const token = ++state.loadToken;
    setNav(route.section);
    closePopovers();
    clearSelection();
    $("section-actions").replaceChildren();
    if (route.section !== "search") setSearchText("");
    if (pages.has(route.section)) {
      showPane(["folders", "duplicates", "who"].includes(route.section) ? "page" : "cards");
      if (route.section === "folders") return showFolders(token);
      if (route.section === "duplicates") return showDuplicates(token);
      if (route.section === "who") return showWho(token);
      return showCards(route.section, route.params.get("show") || (route.params.get("hidden") === "1" ? "hidden" : ""), token);
    }
    if (route.section === "map") {
      showPane("map");
      return showMap(route, token);
    }
    showPane("grid");

    const p = route.params;
    const query = new URLSearchParams();
    const known = lists[route.section] || lists.photos;
    let title = known.title, subtitle = known.subtitle || "", empty = known.empty || "Nothing here.";
    let group = p.get("group") || known.group || "month";
    let path = null; // a list of its own rather than /api/items
    const search = route.section === "search";
    Object.entries(known.query || {}).forEach(([k, v]) => query.set(k, v));

    switch (route.section) {
      case "search": {
        const text = p.get("q") || "";
        const sort = p.get("sort") || "best";
        query.set("section", "search");
        query.set("q", text);
        if (p.get("exact") === "1") query.set("exact", "1");
        if (sort !== "best") query.set("sort", sort);
        title = `“${text}”`;
        group = sort === "best" ? "none" : p.get("group") || "month";
        empty = p.get("exact") === "1"
          ? "No exact matches. Try “Best matches” for photos that look like it, or loosen the filters."
          : "No matches. Try other words, or loosen the filters.";
        setSearchText(text);
        remember(text);
        break;
      }
      case "onthisday": {
        const today = new Date();
        query.set("section", "onthisday");
        query.set("day", `${today.getMonth() + 1}-${today.getDate()}`);
        subtitle = today.toLocaleDateString(undefined, { month: "long", day: "numeric" }) + " in years past";
        empty = "Nothing from this day in other years.";
        break;
      }
      case "person": {
        query.set("section", "person");
        query.set("id", route.id);
        const person = (await getPeople().catch(() => [])).find(x => String(x.id) === route.id)
          || (await json("/api/people?hidden=1").catch(() => [])).find(x => String(x.id) === route.id);
        title = person?.name || "Unnamed person";
        subtitle = person?.hidden ? "Not someone you know (kept on the photo computer)" : person?.notTagged ? "Known, not tagged (kept on the photo computer)" : "";
        personActions(person || { id: Number(route.id) });
        break;
      }
      case "album": {
        query.set("section", "album");
        query.set("id", route.id);
        const album = (await getAlbums().catch(() => [])).find(x => String(x.id) === route.id);
        title = album?.name || "Album";
        group = p.get("group") || "none";
        empty = "This album is empty. Select photos anywhere and choose “Add to album”.";
        albumActions(album || { id: Number(route.id), name: title });
        break;
      }
      case "tag": {
        query.set("section", "tag");
        query.set("id", route.id);
        const tag = (await getTags().catch(() => [])).find(x => String(x.id) === route.id);
        title = tag?.name || "Tag";
        subtitle = tag ? (tag.yours ? "Your tag" : "Tag from OneDrive") : "";
        break;
      }
      case "folder": {
        query.set("section", "folder");
        query.set("id", route.id);
        if (p.get("sub") === "0") query.set("sub", "0");
        const folders = await getFolders().catch(() => []);
        const folder = folders.find(x => String(x.id) === route.id);
        title = folder?.name || "Folder";
        subtitle = folder ? folderPath(folder, folders) : "";
        folderActions(p);
        break;
      }
      case "similar":
        path = `/api/media/${route.id}/similar`;
        title = "Similar photos";
        subtitle = "The first is the one you started from; the rest look most like it, most alike first";
        group = "none";
        empty = "Nothing looks much like it (or the photo computer hasn't compared it yet).";
        break;
      case "area":
        ["s", "w", "n", "e"].forEach(k => query.set(k, p.get(k) || ""));
        query.set("section", "area");
        title = "Photos in this area";
        break;
    }
    if (!path) addFilters(query, p, route.section);
    if (token !== state.loadToken) return;

    state.group = group;
    state.yearsAgo = route.section === "onthisday";
    $("title").textContent = title;
    updateControls(route, group);
    await loadItems(() => json(path || "/api/items?" + query), { subtitle, empty, search, exact: p.get("exact") === "1", key }, token);
  }

  function addFilters(query, p, section) {
    if (p.get("kind") && section !== "videos") query.set("kind", p.get("kind"));
    for (const name of ["people", "rating", "screenshots"]) if (p.get(name)) query.set(name, p.get(name));
    if (p.get("live") === "1") query.set("live", "1");
  }

  /** Loads a list into the grid (keeping where it was, when coming back to it). */
  async function loadItems(fetcher, { subtitle = "", empty = "Nothing here.", search = false, exact = false, key = null, keepPosition = false }, token) {
    $("subtitle").textContent = [subtitle, "Loading…"].filter(Boolean).join(" · ");
    if (!keepPosition) showItems(EMPTY, "");
    try {
      const data = await fetcher();
      if (token !== state.loadToken) return;
      const lead = search
        ? (exact
          ? "Exact words in names, folders, tags, people, places, text in photos and what's said in videos"
          : data.pictures
            ? "Best matches: what's in the picture, and the words in names, tags, people, places and text"
            : "Words in names, tags, people, places and text")
        : subtitle;
      // Kept, so the counts can follow deletions.
      state.describeList = list => [lead, countText(list)].filter(Boolean).join(" · ");
      $("subtitle").textContent = state.describeList(data);
      const scrollTop = $("scroller").scrollTop;
      showItems(data, empty);
      $("scroller").scrollTop = keepPosition ? scrollTop : (key && state.scrollMemory.get(key)) || 0;
      render();
    } catch (error) {
      if (token === state.loadToken && error.status !== 401) {
        $("subtitle").textContent = "";
        showItems(EMPTY, error.message);
      }
    }
  }

  function countText(data) {
    let videos = 0;
    for (const f of data.flags) if (f & 1) videos++;
    const photos = data.ids.length - videos;
    if (!data.ids.length) return "no photos";
    return [photos ? plural(photos, "photo", "photos") : "", videos ? plural(videos, "video", "videos") : ""].filter(Boolean).join(" and ");
  }

  function folderPath(folder, folders) {
    const names = [];
    for (let f = folder; f; f = folders.find(x => x.id === f.parent)) names.unshift(f.name);
    return names.join(" › ");
  }

  // ---------- A section's own actions: a person, an album, a folder ----------

  const actions = (...nodes) => $("section-actions").replaceChildren(...nodes);

  function personActions(person) {
    const rename = button(person.name ? "Rename" : "Name", null, async () => {
      const name = await ask({ title: person.name ? "Rename" : "Who is this?", input: "Name", value: person.name || "", ok: "Save" });
      if (name == null) return;
      try {
        const result = await post(`/api/people/${person.id}/rename`, { name });
        if (result.sameAs) {
          // One name, one person.
          const other = result.sameAs;
          if (!await ask({
            title: `Same person as ${other.name}?`,
            text: `${other.name} is already someone. These photos will be joined to theirs, here and in OneDrive. OneDrive can't undo that.`,
            ok: "Join them",
          })) return;
          await post(`/api/people/${person.id}/merge`, { into: other.id });
          state.people = null;
          toast(`Joined to ${other.name}.`);
          return go(`#/person/${other.id}`);
        }
        state.people = null;
        $("title").textContent = name;
        person.name = name;
        toast(`Named ${name}.`);
      } catch (error) {
        fail(error);
      }
    });
    // Setting aside stays on the photo computer.
    const hide = button(person.hidden ? "Show again" : "Not someone I know", null, async () => {
      try {
        await post(`/api/people/${person.id}/hide`, { hidden: !person.hidden });
        state.people = null;
        person.hidden = !person.hidden;
        hide.lastChild.textContent = person.hidden ? "Show again" : "Not someone I know";
        notTagged.hidden = !!person.name || person.hidden;
        toast(person.hidden ? "Left out of People." : "Back in People.");
      } catch (error) {
        fail(error);
      }
    });
    const notTagged = button(person.notTagged ? "Look at again in Who's this?" : "Known, but don't tag", null, async () => {
      try {
        await post(`/api/people/${person.id}/nottagged`, { notTagged: !person.notTagged });
        state.people = null;
        person.notTagged = !person.notTagged;
        notTagged.lastChild.textContent = person.notTagged ? "Look at again in Who's this?" : "Known, but don't tag";
        toast(person.notTagged ? "Set aside: known, not tagged." : "Back in Who's this?");
      } catch (error) {
        fail(error);
      }
    });
    notTagged.hidden = !!person.name || !!person.hidden;
    const merge = button("Same person as…", null, async () => {
      const people = (await getPeople().catch(() => [])).filter(x => x.id !== person.id && x.name);
      const into = await ask({
        title: `${person.name || "This person"} is the same person as…`,
        text: "Their photos are joined to the person you choose, here and in OneDrive.",
        choices: people.map(x => ({ label: x.name, hint: number(x.count), value: x.id })),
      });
      if (into == null) return;
      const target = people.find(x => x.id === into);
      if (!await ask({ title: `Join to ${target.name}?`, text: `${person.name || "This person"} becomes ${target.name}, here and in OneDrive. OneDrive can't undo that.`, ok: "Join them", danger: true })) return;
      try {
        await post(`/api/people/${person.id}/merge`, { into });
        state.people = null;
        toast(`Joined to ${target.name}.`);
        go(`#/person/${into}`);
      } catch (error) {
        fail(error);
      }
    });
    [rename, hide, notTagged, merge].forEach(b => b.setAttribute("data-change", ""));
    actions(rename, merge, notTagged, hide);
  }

  // ---------- Who's this? People nobody has named, most photos first ----------

  const who = { queue: null, index: 0, decisions: new Map() };
  const monthYear = new Intl.DateTimeFormat(undefined, { timeZone: "UTC", month: "short", year: "numeric" });

  /** Starts going through them afresh (coming back from a person's photos keeps the place instead). */
  function startWho() {
    who.queue = null;
    go("#/who");
  }

  async function showWho(token) {
    $("title").textContent = "Who's this?";
    $("subtitle").textContent = "Loading…";
    $("page").replaceChildren();
    try {
      if (!who.queue) {
        who.queue = await json("/api/people/review");
        who.index = 0;
        who.decisions.clear();
      }
      state.people = null;
      await getPeople();
      if (token === state.loadToken) renderWho(token);
    } catch (error) {
      if (token === state.loadToken && error.status !== 401) $("subtitle").textContent = error.message;
    }
  }

  function renderWho(token) {
    const page = $("page");
    const person = who.queue[who.index];
    if (!person) {
      $("subtitle").textContent = who.queue.length ? "That's everyone for now." : "Everyone OneDrive found has a name or has been set aside.";
      const note = el("p", "muted");
      const people = el("a", null, "People");
      people.href = "#/people";
      note.append("Set someone aside by mistake? ", people, " has them under Show.");
      page.replaceChildren(note);
      return;
    }
    $("subtitle").textContent = `${number(who.index + 1)} of ${number(who.queue.length)} · ${plural(person.count, "photo", "photos")}`;

    const about = el("div", "who-about");
    const years = el("span", "muted");
    const all = el("a", null, person.count === 1 ? "See the photo" : `See all ${number(person.count)}`);
    all.href = `#/person/${person.id}`;
    about.append(years, all);
    if (who.decisions.has(person.id)) about.append(el("span", "decided", who.decisions.get(person.id)));
    const faces = el("div", "who-faces");

    // A new name names them; someone already named joins them (one name, one person).
    const named = (state.people || []).filter(p => p.name && p.id !== person.id).sort((a, b) => b.count - a.count);
    const matchOf = text => named.find(p => p.name.toLowerCase() === text.toLowerCase());
    const form = el("form", "who-answer");
    form.setAttribute("data-change", "");
    const input = el("input");
    input.type = "search";
    input.placeholder = "Their name, or someone you've named";
    input.autocomplete = "off";
    input.setAttribute("aria-label", "Name");
    input.setAttribute("list", "who-names");
    const list = el("datalist");
    list.id = "who-names";
    list.append(...named.map(p => Object.assign(el("option"), { value: p.name })));
    const match = el("img", "match");
    match.alt = "";
    match.hidden = true;
    match.addEventListener("error", () => (match.hidden = true));
    const save = el("button", "primary", "Name");
    save.type = "submit";
    save.disabled = true;
    form.append(input, list, match, save);
    input.addEventListener("input", () => {
      const text = input.value.trim();
      const same = text ? matchOf(text) : null;
      save.disabled = !text;
      save.textContent = same ? `Same person as ${same.name}` : "Name";
      match.hidden = !same;
      if (same) {
        match.src = `/api/people/${same.id}/face`;
        match.title = same.name;
      }
    });
    form.addEventListener("submit", async event => {
      event.preventDefault();
      const text = input.value.trim();
      if (!text) return;
      save.disabled = true;
      try {
        let into = matchOf(text);
        if (!into) {
          const result = await post(`/api/people/${person.id}/rename`, { name: text });
          if (!result.sameAs) {
            decide(person, `Named ${text}`);
            return whoStep(1);
          }
          into = result.sameAs; // someone not in the list (set aside): make sure
          if (!await ask({ title: `Same person as ${into.name}?`, text: `${into.name} is already someone. Join these photos to theirs, here and in OneDrive?`, ok: "Join them" })) {
            save.disabled = false;
            return;
          }
        }
        await post(`/api/people/${person.id}/merge`, { into: into.id });
        person.joined = true;
        decide(person, `Joined to ${into.name}`);
        whoStep(1);
      } catch (error) {
        save.disabled = false;
        fail(error);
      }
    });
    const hint = el("p", "muted who-hint",
      "A new name names them here and in OneDrive. Someone you've named: these photos join theirs, here and in OneDrive (OneDrive can't undo that).");
    hint.setAttribute("data-change", "");

    // Setting aside stays on the photo computer.
    const aside = (label, tip, path, body, decision) => {
      const b = button(label, null, async () => {
        try {
          await post(path, body);
          decide(person, decision);
          whoStep(1);
        } catch (error) {
          fail(error);
        }
      });
      b.title = tip;
      b.setAttribute("data-change", "");
      return b;
    };
    const row = el("div", "who-actions");
    const back = button("Back", null, () => whoStep(-1));
    back.disabled = who.index === 0;
    row.append(
      aside("Known, but don't tag", "You know them, but they don't need tagging. Stays on the photo computer; People › Known, not tagged has them.",
        `/api/people/${person.id}/nottagged`, { notTagged: true }, "Known, not tagged"),
      aside("Not someone I know", "Leaves them out of People. Stays on the photo computer; People › Not someone I know has them.",
        `/api/people/${person.id}/hide`, { hidden: true }, "Not someone I know"),
      button("Skip", null, () => whoStep(1)),
      back);
    page.replaceChildren(about, faces, form, hint, row);
    if (state.changes) input.focus();

    // Their faces (click one for the photo), then the next person's meanwhile.
    json(`/api/people/${person.id}/samples`).then(data => {
      if (token !== state.loadToken || who.queue[who.index] !== person) return;
      const year = s => new Date(s * 1000).getUTCFullYear();
      if (data.first != null) years.textContent = year(data.first) === year(data.last) ? `In photos from ${year(data.first)}` : `In photos from ${year(data.first)} to ${year(data.last)}`;
      const ids = data.samples.map(s => s.media);
      faces.replaceChildren(...data.samples.map(s => {
        const face = el("button", "who-face");
        face.type = "button";
        face.title = "Open the photo";
        const img = el("img");
        img.alt = "";
        img.src = `/api/media/${s.media}/face?person=${person.id}`;
        img.addEventListener("error", () => img.classList.add("broken"));
        face.append(img, el("span", null, monthYear.format(new Date(s.taken * 1000))));
        face.addEventListener("click", () => {
          state.items = { ids, dates: ids.map(() => 0), flags: ids.map(() => 0) };
          const current = parseHash();
          current.params.set("view", s.media);
          viewer.pushed = true;
          go(hashOf(current));
        });
        return face;
      }));
    }).catch(() => {});
    const following = who.queue[who.index + 1];
    if (following) json(`/api/people/${following.id}/samples`)
      .then(data => data.samples.forEach(s => { new Image().src = `/api/media/${s.media}/face?person=${following.id}`; }))
      .catch(() => {});
  }

  function decide(person, decision) {
    who.decisions.set(person.id, decision);
    state.people = null;
    toast(decision + ".");
  }

  /** On (or back), past people joined to someone meanwhile. */
  async function whoStep(step) {
    let index = who.index + step;
    while (index >= 0 && index < who.queue.length && who.queue[index].joined) index += step;
    if (index < 0) return;
    who.index = index;
    if (!state.people) await getPeople().catch(() => []);
    renderWho(state.loadToken);
  }

  function albumActions(album) {
    const rename = button("Rename", null, async () => {
      const name = await ask({ title: "Rename album", input: "Album name", value: album.name, ok: "Save" });
      if (!name) return;
      try {
        await post(`/api/albums/${album.id}/rename`, { name });
        state.albums = null;
        album.name = name;
        $("title").textContent = name;
      } catch (error) {
        fail(error);
      }
    });
    const remove = button("Delete album", null, async () => {
      if (!await ask({ title: `Delete “${album.name}”?`, text: "Only the album goes; its photos stay in the library.", ok: "Delete album", danger: true })) return;
      try {
        await post(`/api/albums/${album.id}/delete`);
        state.albums = null;
        go("#/albums");
      } catch (error) {
        fail(error);
      }
    });
    [rename, remove].forEach(b => b.setAttribute("data-change", ""));
    actions(rename, remove);
  }

  function folderActions(p) {
    const label = el("label", "check-inline");
    const box = el("input");
    box.type = "checkbox";
    box.checked = p.get("sub") !== "0";
    box.addEventListener("change", () => changeParam("sub", box.checked ? null : "0"));
    label.append(box, document.createTextNode(" Include subfolders"));
    actions(label);
  }

  // ---------- Controls: match, sort, group, filters, people, tile size ----------

  function updateControls(route, group) {
    const p = route.params;
    const search = route.section === "search";
    const fixed = route.section === "similar";
    $("exact").hidden = !search;
    $("sort").hidden = !search;
    for (const b of $("exact").children) b.classList.toggle("on", (p.get("exact") === "1") === (b.dataset.exact === "1"));
    $("sort").value = p.get("sort") || "best";
    $("group").value = group;
    $("group").disabled = (search && $("sort").value === "best") || route.section === "blurry";
    $("filters-button").parentElement.hidden = fixed;
    $("people-button").parentElement.hidden = fixed;
    $("kind").value = p.get("kind") || "";
    $("kind").disabled = route.section === "videos";
    $("rating").value = p.get("rating") || "0";
    $("screenshots").value = p.get("screenshots") || "";
    $("live-only").checked = p.get("live") === "1";
    const active = ["kind", "screenshots"].filter(k => p.get(k)).length + (Number(p.get("rating")) > 0 ? 1 : 0) + (p.get("live") === "1" ? 1 : 0);
    $("filters-button").classList.toggle("on", active > 0);
    $("filters-button").querySelector("span").textContent = active ? `Filters (${active})` : "Filters";
    renderChips(route);
  }

  function changeParam(name, value, extra) {
    const current = parseHash();
    current.params.delete("view");
    if (value == null || value === "" || value === "0" && name === "rating") current.params.delete(name);
    else current.params.set(name, value);
    if (extra) extra(current.params);
    replaceRoute(current);
  }

  for (const b of $("exact").children)
    b.addEventListener("click", () => changeParam("exact", b.dataset.exact === "1" ? "1" : null));
  $("sort").addEventListener("change", () => changeParam("sort", $("sort").value === "best" ? null : $("sort").value));
  $("group").addEventListener("change", () => changeParam("group", $("group").value));
  $("kind").addEventListener("change", () => changeParam("kind", $("kind").value));
  $("rating").addEventListener("change", () => changeParam("rating", $("rating").value));
  $("screenshots").addEventListener("change", () => changeParam("screenshots", $("screenshots").value));
  $("live-only").addEventListener("change", () => changeParam("live", $("live-only").checked ? "1" : null));
  $("filters-clear").addEventListener("click", () => {
    closePopovers();
    changeParam("kind", null, p => ["rating", "screenshots", "live"].forEach(k => p.delete(k)));
  });

  function zoom(factor) {
    state.tileSize = Math.round(Math.min(400, Math.max(72, state.tileSize * factor)));
    store.set("tileSize", state.tileSize);
    layout();
  }
  $("zoom-in").addEventListener("click", () => zoom(1.25));
  $("zoom-out").addEventListener("click", () => zoom(0.8));

  // Popovers: filters and the people filter.
  function togglePopover(id, onOpen) {
    const pop = $(id);
    const open = pop.hidden;
    closePopovers();
    if (open) {
      pop.hidden = false;
      onOpen?.();
    }
  }
  function closePopovers() {
    $("filters-pop").hidden = true;
    $("people-pop").hidden = true;
  }
  $("filters-button").addEventListener("click", event => {
    event.stopPropagation();
    togglePopover("filters-pop");
  });
  $("people-button").addEventListener("click", event => {
    event.stopPropagation();
    togglePopover("people-pop", async () => {
      $("people-find").value = "";
      await fillPeopleList();
      $("people-find").focus();
    });
  });
  for (const id of ["filters-pop", "people-pop"]) $(id).addEventListener("click", event => event.stopPropagation());
  document.addEventListener("click", closePopovers);

  // People filter: photos with all of the chosen people in them.
  const chosenPeople = () => (parseHash().params.get("people") || "").split(",").filter(Boolean);

  async function renderChips(route) {
    const chips = $("chips");
    chips.replaceChildren();
    const chosen = (route.params.get("people") || "").split(",").filter(Boolean);
    $("people-button").classList.toggle("on", chosen.length > 0);
    $("people-button").querySelector("span").textContent = chosen.length ? `People (${chosen.length})` : "People";
    if (!chosen.length) return;
    const people = await getPeople().catch(() => []);
    for (const id of chosen) {
      const person = people.find(x => String(x.id) === id);
      const chip = el("span", "chip", person?.name || "Unnamed person");
      const remove = el("button");
      remove.type = "button";
      remove.setAttribute("aria-label", "Remove");
      remove.append(icon("close"));
      remove.addEventListener("click", () => changeParam("people", chosen.filter(x => x !== id).join(",")));
      chip.append(remove);
      chips.append(chip);
    }
  }

  async function fillPeopleList() {
    const list = $("people-list");
    const find = $("people-find").value.trim().toLowerCase();
    const chosen = chosenPeople();
    const people = (await getPeople().catch(() => []))
      .filter(p => p.name || chosen.includes(String(p.id)))
      .filter(p => !find || (p.name || "").toLowerCase().includes(find));
    list.replaceChildren();
    if (!people.length) list.append(el("p", "muted", find ? "No one by that name." : "No named people yet."));
    for (const person of people.slice(0, 300)) {
      const label = el("label");
      const box = el("input");
      box.type = "checkbox";
      box.checked = chosen.includes(String(person.id));
      box.addEventListener("change", () => {
        const now = chosenPeople().filter(x => x !== String(person.id));
        if (box.checked) now.push(String(person.id));
        changeParam("people", now.join(","));
      });
      const face = el("img");
      face.loading = "lazy";
      face.alt = "";
      face.src = `/api/people/${person.id}/face`;
      face.addEventListener("error", () => face.replaceWith(el("span", "avatar")));
      label.append(box, face, el("span", null, person.name || "Unnamed person"), el("span", "count", number(person.count)));
      list.append(label);
    }
  }
  $("people-find").addEventListener("input", fillPeopleList);
  $("people-clear").addEventListener("click", () => {
    closePopovers();
    changeParam("people", null);
  });

  // ---------- Search ----------

  function setSearchText(text) {
    $("search").value = text;
    $("search-clear").hidden = !text;
  }

  function remember(text) {
    const recent = store.get("recent", []).filter(x => x.toLowerCase() !== text.toLowerCase());
    recent.unshift(text);
    store.set("recent", recent.slice(0, 12));
  }

  /** The search box's list: recent searches, then people, places and tags whose names contain what's typed. */
  async function updateSuggestions(text) {
    const recent = store.get("recent", []).filter(r => !text || r.toLowerCase().includes(text.toLowerCase()));
    let found = [];
    if (text.length >= 2) {
      try {
        found = (await json("/api/suggest?q=" + encodeURIComponent(text))).map(s => s.text);
      } catch {
        // suggestions are optional
      }
      if ($("search").value.trim() !== text) return; // typed on meanwhile
    }
    const values = [...new Set([...recent, ...found])].slice(0, 12);
    $("suggestions").replaceChildren(...values.map(v => Object.assign(el("option"), { value: v })));
  }

  /** Runs a search, keeping the current match, sort and filters if already searching. */
  function search(text) {
    text = (text || "").trim();
    if (!text) return clearSearch();
    const current = parseHash();
    const next = new URLSearchParams();
    if (current.section === "search") {
      for (const key of ["exact", "sort", "group", "kind", "people", "rating", "screenshots", "live"])
        if (current.params.get(key)) next.set(key, current.params.get(key));
    }
    next.set("q", text);
    go(hashOf({ section: "search", id: null, params: next }));
  }

  function clearSearch() {
    setSearchText("");
    if (parseHash().section === "search") go(state.lastList || "#/");
  }

  $("search-form").addEventListener("submit", event => {
    event.preventDefault();
    $("search").blur();
    search($("search").value);
  });
  $("search").addEventListener("input", () => {
    $("search-clear").hidden = !$("search").value;
    clearTimeout(updateSuggestions.timer);
    updateSuggestions.timer = setTimeout(() => updateSuggestions($("search").value.trim()), 180);
  });
  $("search-clear").addEventListener("click", () => {
    clearSearch();
    $("search").focus();
  });
  $("menu").addEventListener("click", event => {
    event.stopPropagation();
    $("app").classList.toggle("nav-open");
  });
  $("main").addEventListener("click", () => $("app").classList.remove("nav-open"));

  // ---------- People, albums and tags ----------

  async function showCards(kind, show, token) {
    $("title").textContent = { people: "People", albums: "Albums", tags: "Tags" }[kind];
    $("subtitle").textContent = "Loading…";
    $("cards").replaceChildren();
    $("cards-find").value = "";
    $("cards-find").placeholder = { people: "Find a person", albums: "Find an album", tags: "Find a tag" }[kind];
    if (kind === "albums") {
      const create = button("New album", "plus", async () => {
        const name = await ask({ title: "New album", input: "Album name", ok: "Create" });
        if (!name) return;
        try {
          const created = await post("/api/albums", { name });
          state.albums = null;
          go(`#/album/${created.id}`);
        } catch (error) {
          fail(error);
        }
      });
      create.setAttribute("data-change", "");
      actions(create);
    }
    let whoButton = null;
    if (kind === "people") {
      // Everyone, or those set aside (which stay on the photo computer).
      const which = el("select");
      which.title = "People you set aside stay on the photo computer; OneDrive still has them";
      for (const [value, label] of [["", "People"], ["nottagged", "Known, not tagged"], ["hidden", "Not someone I know"]])
        which.append(Object.assign(el("option", null, label), { value }));
      which.value = show;
      which.addEventListener("change", () => go(which.value ? `#/people?show=${which.value}` : "#/people"));
      whoButton = button("Who's this?", null, startWho, "primary");
      whoButton.title = "Go through the people nobody has named yet, most photos first";
      whoButton.setAttribute("data-change", "");
      actions(which, whoButton);
    }
    try {
      const all = kind === "people" ? await json("/api/people?hidden=1") : kind === "albums" ? await getAlbums() : await getTags();
      if (token !== state.loadToken) return;
      const rows = kind !== "people" ? all
        : all.filter(p => show === "hidden" ? p.hidden : show === "nottagged" ? p.notTagged && !p.hidden : !p.hidden && !p.notTagged);
      if (whoButton) {
        const unnamed = all.filter(p => !p.name && !p.hidden && !p.notTagged).length;
        whoButton.lastChild.textContent = unnamed ? `Who's this? (${number(unnamed)})` : "Who's this?";
      }
      $("subtitle").textContent = kind === "people"
        ? show === "hidden" ? `${plural(rows.length, "person", "people")} you don't know`
          : show === "nottagged" ? `${plural(rows.length, "person", "people")} you know but didn't want tagged`
            : `${plural(rows.length, "person", "people")} OneDrive recognised in your photos`
        : kind === "albums" ? plural(rows.length, "album", "albums")
          : `${plural(rows.filter(t => t.yours).length, "tag", "tags")} of yours, ${number(rows.filter(t => !t.yours).length)} from OneDrive`;
      const cards = (kind === "tags" ? [...rows].sort((a, b) => (b.yours - a.yours) || b.count - a.count) : rows).map(row => {
        const card = el("a", "card" + (kind === "people" ? " person" : kind === "tags" ? " tag" : ""));
        card.href = `#/${{ people: "person", albums: "album", tags: "tag" }[kind]}/${row.id}`;
        card.dataset.name = (row.name || "").toLowerCase();
        const cover = el("div", "cover");
        const src = kind === "people" ? `/api/people/${row.id}/face` : kind === "albums" && row.cover ? thumbUrl(row.cover) : null;
        const initials = (row.name || "?").split(/\s+/).map(w => w[0]).join("").slice(0, 2).toUpperCase();
        if (src) {
          const img = el("img");
          img.loading = "lazy";
          img.alt = "";
          img.src = src;
          img.addEventListener("error", () => img.replaceWith(document.createTextNode(initials)));
          cover.append(img);
        } else if (kind === "tags") {
          cover.append(icon("tag"));
        } else {
          cover.textContent = initials;
        }
        const note = kind === "tags" ? `${plural(row.count, "photo", "photos")} · ${row.yours ? "yours" : "OneDrive"}`
          : plural(row.count, "photo", "photos") + (row.hidden ? " · hidden" : "");
        card.append(cover, el("span", "name", row.name || (kind === "people" ? "Unnamed person" : "Untitled")), el("span", "muted", note));
        return card;
      });
      $("cards").replaceChildren(...cards);
      if (!cards.length) $("cards").append(el("p", "muted", { people: "No people yet.", albums: "No albums yet.", tags: "No tags yet." }[kind]));
    } catch (error) {
      if (token === state.loadToken && error.status !== 401) $("subtitle").textContent = error.message;
    }
  }

  $("cards-find").addEventListener("input", () => {
    const find = $("cards-find").value.trim().toLowerCase();
    for (const card of $("cards").children) card.hidden = !!find && !(card.dataset.name || "").includes(find);
  });

  // ---------- Folders ----------

  async function showFolders(token) {
    $("title").textContent = "Folders";
    $("subtitle").textContent = "Loading…";
    const page = $("page");
    page.replaceChildren();
    try {
      const folders = await getFolders();
      if (token !== state.loadToken) return;
      $("subtitle").textContent = plural(folders.length, "folder", "folders") + " on " + state.name;
      const find = el("input");
      find.type = "search";
      find.placeholder = "Find a folder";
      find.className = "page-find";
      const tree = el("div", "tree");
      const children = new Map();
      for (const f of folders) {
        const list = children.get(f.parent ?? 0) || [];
        list.push(f);
        children.set(f.parent ?? 0, list);
      }
      const ids = new Set(folders.map(f => f.id));
      const roots = folders.filter(f => f.parent == null || !ids.has(f.parent));
      const add = (folder, depth) => {
        const link = el("a");
        link.href = `#/folder/${folder.id}`;
        link.style.paddingLeft = 8 + depth * 20 + "px";
        link.dataset.name = folder.name.toLowerCase();
        link.append(icon("folder"), document.createTextNode(folder.name));
        tree.append(link);
        for (const child of (children.get(folder.id) || []).sort((a, b) => a.name.localeCompare(b.name))) add(child, depth + 1);
      };
      roots.sort((a, b) => a.name.localeCompare(b.name)).forEach(f => add(f, 0));
      find.addEventListener("input", () => {
        const text = find.value.trim().toLowerCase();
        for (const link of tree.children) link.hidden = !!text && !link.dataset.name.includes(text);
      });
      page.append(find, tree);
    } catch (error) {
      if (token === state.loadToken) $("subtitle").textContent = error.message;
    }
  }

  // ---------- Duplicates ----------

  async function showDuplicates(token) {
    $("title").textContent = "Duplicates";
    $("subtitle").textContent = "Exact copies, and re-saved or resized versions of the same shot. Found on the photo computer.";
    const page = $("page");
    page.replaceChildren();
    const toolbar = el("div", "dup-toolbar");
    const scan = button("Find duplicates", "search", async () => {
      try {
        render(await post("/api/duplicates/scan"));
        poll();
      } catch (error) {
        fail(error);
      }
    }, "primary");
    const kind = el("select");
    for (const [value, label] of [["", "All kinds"], ["exact", "Exact copies"], ["similar", "Same shot, re-saved"]])
      kind.append(Object.assign(el("option", null, label), { value }));
    kind.value = store.get("dupKind", "");
    kind.addEventListener("change", () => {
      store.set("dupKind", kind.value);
      render(last);
    });
    const status = el("span", "muted");
    const bar = el("div", "progress");
    const fill = el("div");
    bar.append(fill);
    toolbar.append(scan, kind, status, bar);
    const list = el("div");
    page.append(toolbar, list);
    let last = null, shown = 60;

    const render = data => {
      last = data;
      scan.lastChild.textContent = data.groups ? "Scan again" : "Find duplicates";
      scan.disabled = data.running;
      bar.hidden = !data.running;
      if (data.progress) fill.style.width = (data.progress.total ? 100 * data.progress.done / data.progress.total : 5) + "%";
      status.textContent = data.running
        ? `${data.progress?.stage || "Scanning"}… ${data.progress?.total ? `${number(data.progress.done)} of ${number(data.progress.total)}` : ""}`
        : data.error ? `The scan failed: ${data.error}`
          : data.groups ? `${plural(data.groups.length, "group", "groups")} · last scan ${formats.full.format(new Date(data.finished + "Z"))}`
            : "Not scanned yet. The first scan reads every photo on the photo computer and can take a while.";
      list.replaceChildren();
      const groups = (data.groups || []).filter(g => !kind.value || g.kind === kind.value);
      for (const group of groups.slice(0, shown)) list.append(groupCard(group));
      if (groups.length > shown) list.append(button(`Show more (${number(groups.length - shown)} left)`, null, () => {
        shown += 60;
        render(last);
      }));
      if (data.groups && !groups.length) list.append(el("p", "muted", "No duplicates of this kind."));
    };

    const groupCard = group => {
      const card = el("section", "dup-group");
      const header = el("header");
      header.append(el("strong", null, group.kind === "exact" ? "Exact copies" : "Same shot, re-saved or resized"),
        el("span", "muted", `${plural(group.members.length, "copy", "copies")} · ${fileSize(group.reclaimable)} to free`));
      const extras = group.members.filter(m => !m.keep);
      const deleteExtras = button(`Delete the ${extras.length === 1 ? "extra copy" : `${extras.length} extra copies`}`, "trash", async () => {
        if (await deleteIds(extras.map(m => m.id), `Delete ${plural(extras.length, "extra copy", "extra copies")}? The one marked “Keep” stays.`))
          render(await json("/api/duplicates"));
      }, "danger");
      deleteExtras.setAttribute("data-change", "");
      header.append(deleteExtras);
      const members = el("div", "dup-members");
      for (const m of group.members) {
        const box = el("div", "dup-member");
        const thumb = el("div", "thumb");
        const img = el("img");
        img.loading = "lazy";
        img.alt = "";
        img.src = thumbUrl(m.id);
        thumb.append(img);
        if (m.keep) thumb.append(el("span", "keep", "Keep"));
        thumb.addEventListener("click", () => openList(group.members.map(x => x.id), m.id));
        const remove = button("Delete", "trash", async () => {
          if (await deleteIds([m.id])) render(await json("/api/duplicates"));
        }, "danger");
        remove.setAttribute("data-change", "");
        box.append(thumb, el("span", "name", m.name),
          el("span", "muted", [fileSize(m.size), m.width ? `${m.width} × ${m.height}` : "", formats.date.format(new Date(m.taken + "Z"))].filter(Boolean).join(" · ")),
          remove);
        members.append(box);
      }
      card.append(header, members);
      return card;
    };

    const poll = async () => {
      while (token === state.loadToken) {
        await new Promise(r => setTimeout(r, 1500));
        if (token !== state.loadToken) return;
        try {
          const data = await json("/api/duplicates");
          render(data);
          if (!data.running) return;
        } catch {
          return;
        }
      }
    };

    try {
      const data = await json("/api/duplicates");
      if (token !== state.loadToken) return;
      render(data);
      if (data.running) poll();
    } catch (error) {
      if (token === state.loadToken) status.textContent = error.message;
    }
  }

  /** Shows a given set of items in the grid, and opens one of them. */
  async function openList(ids, openId) {
    try {
      const data = await post("/api/items/list", { ids, sort: "listed" });
      state.items = data;
      state.group = "none";
      openViewer(Math.max(0, data.ids.indexOf(openId)));
    } catch (error) {
      fail(error);
    }
  }

  // ---------- Map: clustered photos; the list below shows the photos in view, or a cluster's ----------

  let leaflet = null;
  function loadLeaflet() {
    leaflet ??= new Promise((resolve, reject) => {
      for (const href of ["vendor/leaflet.css", "vendor/MarkerCluster.css", "vendor/MarkerCluster.Default.css"]) {
        const link = el("link");
        link.rel = "stylesheet";
        link.href = href;
        document.head.append(link);
      }
      const load = src => new Promise((ok, bad) => {
        const script = el("script");
        script.src = src;
        script.onload = ok;
        script.onerror = () => bad(new Error("The map couldn't load."));
        document.head.append(script);
      });
      load("vendor/leaflet.js").then(() => load("vendor/leaflet.markercluster.js")).then(resolve, reject);
    });
    return leaflet;
  }

  const map = { view: null, clusters: null, selected: null, ready: false };

  async function showMap(route, token) {
    $("title").textContent = "Map";
    $("subtitle").textContent = "Loading photo locations…";
    updateControls(route, route.params.get("group") || "month");
    state.group = route.params.get("group") || "month";
    try {
      await loadLeaflet();
    } catch (error) {
      $("subtitle").textContent = error.message;
      return;
    }
    if (token !== state.loadToken) return;
    if (!map.view) {
      const L = window.L;
      map.view = L.map("map", { worldCopyJump: true, preferCanvas: true }).setView([20, 0], 2);
      map.view.setMaxZoom(21);
      L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
        maxZoom: 21,
        maxNativeZoom: 19,
        referrerPolicy: "origin",
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors',
      }).addTo(map.view);
      map.clusters = L.markerClusterGroup({ chunkedLoading: true, maxClusterRadius: 60, showCoverageOnHover: false, zoomToBoundsOnClick: false, spiderfyOnMaxZoom: true });
      map.view.addLayer(map.clusters);
      // Clicking a cluster lists exactly its photos; clicking the map goes back to everything in view.
      map.clusters.on("clusterclick", e => {
        map.selected?.getElement()?.classList.remove("selected");
        map.selected = e.layer;
        map.selected.getElement()?.classList.add("selected");
        listMap(e.layer.getAllChildMarkers().map(m => m.options.photoId));
      });
      map.view.on("click", () => {
        $("map-results").hidden = true;
        if (!map.selected) return;
        map.selected.getElement()?.classList.remove("selected");
        map.selected = null;
        listMap();
      });
      let timer;
      map.view.on("moveend", () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
          if (!map.selected) listMap();
        }, 300);
      });
      // A photo's own pin: its thumbnail, drawn when it's shown (not for every photo up front).
      const PhotoIcon = L.DivIcon.extend({
        createIcon(old) {
          const div = L.DivIcon.prototype.createIcon.call(this, old);
          div.style.backgroundImage = `url("${thumbUrl(this.options.photoId)}")`;
          return div;
        },
      });
      try {
        const points = await json("/api/map");
        const markers = points.ids.map((id, i) => {
          const marker = L.marker([points.lat[i], points.lon[i]], {
            icon: new PhotoIcon({ className: "photo-pin", html: "", iconSize: [44, 44], iconAnchor: [22, 22], photoId: id }),
            photoId: id,
          });
          marker.on("click", () => openList([id], id));
          return marker;
        });
        map.clusters.addLayers(markers);
        $("map-status").textContent = `${number(points.ids.length)} photos with a location`;
        setTimeout(() => ($("map-status").textContent = ""), 4000);
        if (points.ids.length) map.view.fitBounds(L.latLngBounds(points.lat.map((lat, i) => [lat, points.lon[i]])), { maxZoom: 12 });
        map.ready = true;
      } catch (error) {
        $("map-status").textContent = "Couldn't load photo locations: " + error.message;
      }
    } else {
      map.view.invalidateSize();
    }
    listMap();
  }

  // Finding a place by name: on the photo computer as you type; OpenStreetMap when asked (it sends the text there).
  let placeToken = 0, placeTimer = 0, placeHits = [];

  async function findPlaces(online) {
    const text = $("map-search").value.trim();
    const token = ++placeToken;
    if (text.length < 2) {
      $("map-results").hidden = true;
      return;
    }
    if (online) showPlaceRows([{ note: "Searching OpenStreetMap…" }]);
    try {
      const hits = await json(`/api/places?q=${encodeURIComponent(text)}${online ? "&online=1" : ""}`);
      if (token !== placeToken) return;
      placeHits = hits;
      showPlaceRows(online
        ? (hits.length ? hits : [{ note: `Nothing found for “${text}”.` }])
        : [...hits, { online: true, name: `Search OpenStreetMap for “${text}”`, caption: "Sends what you typed to OpenStreetMap" }]);
    } catch (error) {
      if (token === placeToken) showPlaceRows([{ note: error.message }]);
    }
  }

  function showPlaceRows(rows) {
    const list = $("map-results");
    list.replaceChildren(...rows.map(row => {
      if (row.note) return el("div", "note", row.note);
      const b = el("button", null, row.name);
      b.type = "button";
      b.append(el("span", null, row.caption || ""));
      b.addEventListener("click", () => (row.online ? findPlaces(true) : goToPlace(row)));
      return b;
    }));
    list.hidden = false;
  }

  function goToPlace(hit) {
    const L = window.L;
    $("map-results").hidden = true;
    $("map-search").value = hit.name;
    if (map.found) map.view.removeLayer(map.found);
    if (hit.s != null) map.view.fitBounds([[hit.s, hit.w], [hit.n, hit.e]], { maxZoom: 17 });
    else map.view.setView([hit.lat, hit.lon], hit.kind === "town" ? 12 : 17);
    map.found = L.marker([hit.lat, hit.lon], { icon: L.divIcon({ className: "found-pin", iconSize: [18, 18], iconAnchor: [9, 9] }), zIndexOffset: 1000 })
      .bindTooltip(hit.name, { permanent: true, direction: "top", offset: [0, -10], className: "found-label" })
      .addTo(map.view);
    map.found.on("click", () => {
      map.view.removeLayer(map.found);
      map.found = null;
    });
  }

  $("map-search").addEventListener("input", () => {
    clearTimeout(placeTimer);
    placeTimer = setTimeout(() => findPlaces(false), 250);
  });
  $("map-search").addEventListener("keydown", event => {
    if (event.key === "ArrowDown") {
      $("map-results").querySelector("button")?.focus();
      event.preventDefault();
    } else if (event.key === "Escape") {
      $("map-results").hidden = true;
    }
  });
  $("map-results").addEventListener("keydown", event => {
    const buttons = [...$("map-results").querySelectorAll("button")];
    const at = buttons.indexOf(document.activeElement);
    if (event.key === "ArrowDown" && at < buttons.length - 1) buttons[at + 1].focus();
    else if (event.key === "ArrowUp") (at > 0 ? buttons[at - 1] : $("map-search")).focus();
    else if (event.key === "Escape") {
      $("map-results").hidden = true;
      $("map-search").focus();
    } else return;
    event.preventDefault();
  });
  $("map-search-form").addEventListener("submit", event => {
    event.preventDefault();
    // Enter: the best match here, or OpenStreetMap when nothing here matched.
    const text = $("map-search").value.trim();
    if (placeHits.length && !$("map-results").hidden && !$("map-results").querySelector(".note")) goToPlace(placeHits[0]);
    else if (text.length >= 2) findPlaces(true);
  });
  // Clicks on the map close the list (and don't reach it through the search box).
  for (const node of [$("map-search-form"), $("map-results")]) {
    node.addEventListener("click", event => event.stopPropagation());
    node.addEventListener("dblclick", event => event.stopPropagation());
    node.addEventListener("wheel", event => event.stopPropagation());
  }

  /** The list under the map: a cluster's photos (ids), or the photos in view. */
  function listMap(ids) {
    if (state.route?.section !== "map" || !map.view) return;
    const token = state.loadToken;
    const p = state.route.params;
    if (ids) {
      loadItems(() => post("/api/items/list", { ids }), { subtitle: "Photos in the cluster · click the map to see everything in view", empty: "Nothing here." }, token);
      return;
    }
    const b = map.view.getBounds();
    const query = new URLSearchParams({ section: "area", s: b.getSouth(), w: b.getWest(), n: b.getNorth(), e: b.getEast() });
    addFilters(query, p, "area");
    loadItems(() => json("/api/items?" + query), { subtitle: "Photos in view · click a cluster for just its photos", empty: "No photos with a location in view." }, token);
  }

  // ---------- The grid: only the rows on (or near) the screen exist ----------

  const GAP = 4, HEADER = 46, TOP = 4, GROUP_SPACE = 12;

  function showItems(data, empty) {
    state.items = data;
    $("empty").hidden = data.ids.length > 0 || !empty;
    $("empty").textContent = empty;
    layout(true);
  }

  function groupKey(seconds) {
    const d = new Date(seconds * 1000);
    switch (state.group) {
      case "year": return d.getUTCFullYear();
      case "day": return d.getUTCFullYear() * 10000 + d.getUTCMonth() * 100 + d.getUTCDate();
      default: return d.getUTCFullYear() * 100 + d.getUTCMonth();
    }
  }

  function groupLabel(seconds) {
    const d = new Date(seconds * 1000);
    switch (state.group) {
      case "year": {
        if (!state.yearsAgo) return String(d.getUTCFullYear());
        const ago = new Date().getFullYear() - d.getUTCFullYear();
        return ago > 0 ? `${d.getUTCFullYear()} · ${ago === 1 ? "a year ago" : ago + " years ago"}` : String(d.getUTCFullYear());
      }
      case "day": return formats.day.format(d);
      default: return formats.month.format(d);
    }
  }

  function layout(reset) {
    const scroller = $("scroller");
    const side = window.innerWidth <= 760 ? 16 : 20;
    const width = Math.max(100, scroller.clientWidth - side * 2);
    const cols = Math.max(2, Math.floor((width + GAP) / (state.tileSize + GAP)));
    const tile = (width - GAP * (cols - 1)) / cols;
    // Keeps the first photo on screen where it was (at the very top, the top stays: its heading included).
    const atTop = scroller.scrollTop < 1;
    const anchor = reset || atTop ? -1 : firstVisibleItem();
    const anchorOffset = anchor >= 0 ? rowTopOf(anchor) - scroller.scrollTop : 0;

    const { ids, dates } = state.items;
    const rows = [];
    let top = TOP;
    const addGroup = (label, start, end) => {
      if (label != null) {
        rows.push({ top, height: HEADER, label, count: end - start });
        top += HEADER;
      }
      for (let i = start; i < end; i += cols) {
        rows.push({ top, height: tile, start: i, end: Math.min(i + cols, end), label });
        top += tile + GAP;
      }
      top += label != null ? GROUP_SPACE : 0;
    };
    if (state.group === "none" || !ids.length) {
      addGroup(null, 0, ids.length);
    } else {
      let key = null, start = 0;
      for (let i = 0; i < ids.length; i++) {
        const k = groupKey(dates[i]);
        if (k !== key) {
          if (key !== null) addGroup(groupLabel(dates[start]), start, i);
          key = k;
          start = i;
        }
      }
      addGroup(groupLabel(dates[start]), start, ids.length);
    }

    state.rows = rows;
    state.cols = cols;
    state.tile = tile;
    for (const node of state.rendered.values()) node.remove();
    state.rendered.clear();
    $("sizer").style.height = top + 24 + "px";
    if (anchor >= 0) scroller.scrollTop = Math.max(0, rowTopOf(anchor) - anchorOffset);
    else if (atTop) scroller.scrollTop = 0;
    render();
  }

  function rowTopOf(index) {
    const row = state.rows.find(r => r.start != null && index >= r.start && index < r.end);
    return row ? row.top : 0;
  }

  function rowIndexAt(y) {
    const rows = state.rows;
    let lo = 0, hi = rows.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (rows[mid].top <= y) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  }

  function firstVisibleItem() {
    if (!state.rows.length) return -1;
    const top = $("scroller").scrollTop;
    for (let i = rowIndexAt(top); i < state.rows.length; i++) if (state.rows[i].start != null) return state.rows[i].start;
    return -1;
  }

  let renderQueued = false;
  function queueRender() {
    if (renderQueued) return;
    renderQueued = true;
    requestAnimationFrame(() => {
      renderQueued = false;
      render();
    });
  }

  function render() {
    const scroller = $("scroller");
    if (scroller.hidden || !state.rows.length) return;
    const top = scroller.scrollTop, height = scroller.clientHeight;
    const first = rowIndexAt(Math.max(0, top - height));
    const last = rowIndexAt(top + height * 2);
    for (const [index, node] of state.rendered) {
      if (index < first || index > last) {
        node.remove();
        state.rendered.delete(index);
      }
    }
    const sizer = $("sizer");
    for (let i = first; i <= last; i++) {
      if (state.rendered.has(i)) continue;
      const node = buildRow(state.rows[i]);
      node.style.transform = `translateY(${state.rows[i].top}px)`;
      sizer.append(node);
      state.rendered.set(i, node);
    }
    showDatePill(state.rows[rowIndexAt(top + 1)]);
  }

  function buildRow(row) {
    if (row.start == null) {
      const header = el("div", "group-header");
      header.style.height = row.height + "px";
      header.append(el("h2", null, row.label), el("span", "muted", number(row.count)));
      return header;
    }
    const node = el("div", "row");
    node.style.height = row.height + "px";
    node.style.gridTemplateColumns = `repeat(${state.cols}, ${state.tile}px)`;
    for (let i = row.start; i < row.end; i++) node.append(buildTile(i));
    return node;
  }

  function buildTile(index) {
    const id = state.items.ids[index], flags = state.items.flags[index];
    const tile = el("button", "tile");
    tile.type = "button";
    tile.dataset.index = index;
    tile.style.height = state.tile + "px";
    if (state.selected.has(id)) tile.classList.add("selected");
    const img = el("img");
    img.alt = "";
    img.loading = "lazy";
    img.decoding = "async";
    img.draggable = false;
    img.src = thumbUrl(id);
    img.addEventListener("error", () => tile.classList.add("broken"));
    const check = el("span", "check");
    check.append(icon("check"));
    tile.append(img, check);
    if (flags & 1) {
      const badge = el("span", "badge kind");
      badge.append(icon("play"), document.createTextNode(duration(flags / 32)));
      tile.append(badge);
    } else if (flags & 2) {
      const badge = el("span", "badge kind");
      badge.append(icon("live"));
      tile.append(badge);
    }
    const rating = (flags >> 2) & 7;
    if (rating) tile.append(el("span", "badge rating", "★".repeat(rating)));
    tile.setAttribute("aria-label", `${flags & 1 ? "Video" : "Photo"}, ${formats.full.format(new Date(state.items.dates[index] * 1000))}`);
    return tile;
  }

  /** Rebuilds the rows on screen (after ratings or selection change). */
  function refreshRows() {
    for (const [i, node] of state.rendered) {
      const fresh = buildRow(state.rows[i]);
      fresh.style.transform = node.style.transform;
      node.replaceWith(fresh);
      state.rendered.set(i, fresh);
    }
  }

  function showDatePill(row) {
    const pill = $("date-pill");
    if (!row || row.label == null || state.group === "none") {
      pill.hidden = true;
      return;
    }
    pill.textContent = row.label;
    pill.hidden = $("scroller").scrollTop < 40;
    clearTimeout(showDatePill.timer);
    showDatePill.timer = setTimeout(() => (pill.hidden = true), 1200);
  }

  $("scroller").addEventListener("scroll", queueRender, { passive: true });
  new ResizeObserver(() => {
    if (!$("scroller").hidden && Math.abs($("scroller").clientWidth - (layout.width || 0)) > 1) {
      layout.width = $("scroller").clientWidth;
      layout();
    }
  }).observe($("scroller"));

  /** Drops items from the list shown (deleted, or taken out of the album), keeping the place. */
  function removeItems(gone) {
    const set = new Set(gone);
    const keep = [];
    state.items.ids.forEach((id, i) => {
      if (!set.has(id)) keep.push(i);
    });
    state.items = {
      ...state.items,
      ids: keep.map(i => state.items.ids[i]),
      dates: keep.map(i => state.items.dates[i]),
      flags: keep.map(i => state.items.flags[i]),
    };
    for (const id of gone) state.selected.delete(id);
    if (state.describeList) $("subtitle").textContent = state.describeList(state.items);
    updateSelection();
    layout();
  }

  // ---------- Selecting: click the circle (or Ctrl-click); then clicks add, Shift-click adds a run ----------

  $("sizer").addEventListener("click", event => {
    const tile = event.target.closest(".tile");
    if (!tile) return;
    const index = Number(tile.dataset.index);
    if (event.target.closest(".check") || state.selected.size > 0 || event.ctrlKey || event.metaKey || event.shiftKey) {
      event.preventDefault();
      if (event.shiftKey && state.anchor != null) selectRange(state.anchor, index);
      else toggleSelected(index);
      state.anchor = index;
      return;
    }
    openViewer(index);
  });

  function toggleSelected(index) {
    const id = state.items.ids[index];
    if (state.selected.has(id)) state.selected.delete(id);
    else state.selected.add(id);
    document.querySelector(`.tile[data-index="${index}"]`)?.classList.toggle("selected", state.selected.has(id));
    updateSelection();
  }

  function selectRange(from, to) {
    const [a, b] = from < to ? [from, to] : [to, from];
    for (let i = a; i <= b; i++) state.selected.add(state.items.ids[i]);
    refreshRows();
    updateSelection();
  }

  function clearSelection() {
    if (!state.selected.size) return;
    state.selected.clear();
    state.anchor = null;
    refreshRows();
    updateSelection();
  }

  function updateSelection() {
    const count = state.selected.size;
    $("selbar").hidden = count === 0;
    $("bar").hidden = count > 0;
    $("main").classList.toggle("selecting", count > 0);
    if (!count) return;
    let videos = 0;
    const flags = new Map(state.items.ids.map((id, i) => [id, state.items.flags[i]]));
    for (const id of state.selected) if (flags.get(id) & 1) videos++;
    $("sel-count").textContent = `${describe(count, videos)} selected`;
    $("sel-unalbum").hidden = state.route?.section !== "album";
    $("sel-all").hidden = count === state.items.ids.length;
  }

  const selectedIds = () => [...state.selected];

  $("sel-clear").addEventListener("click", clearSelection);
  $("sel-all").addEventListener("click", () => {
    state.items.ids.forEach(id => state.selected.add(id));
    refreshRows();
    updateSelection();
  });
  $("sel-rate").addEventListener("click", async () => {
    const rating = await ask({
      title: `Rate ${plural(state.selected.size, "item", "items")}`,
      choices: [5, 4, 3, 2, 1].map(n => ({ label: "★".repeat(n), value: n })).concat({ label: "Clear rating", value: 0 }),
    });
    if (rating != null && await rateIds(selectedIds(), rating)) clearSelection();
  });

  /** Sets (or with 0 clears) the stars of these items. */
  async function rateIds(ids, rating) {
    try {
      await post("/api/media/rating", { ids, rating });
      setRatings(ids, rating);
      return true;
    } catch (error) {
      fail(error);
      return false;
    }
  }

  // ---------- Right-click a tile: its stars (or the selection's, when it's part of it) ----------

  let tileMenu = null;

  $("sizer").addEventListener("contextmenu", event => {
    const tile = event.target.closest(".tile");
    if (!tile) return;
    event.preventDefault();
    const id = state.items.ids[Number(tile.dataset.index)];
    showTileMenu(event.clientX, event.clientY, state.selected.has(id) && state.selected.size > 1 ? selectedIds() : [id]);
  });

  function showTileMenu(x, y, ids) {
    closeTileMenu();
    const chosen = new Set(ids), ratings = new Set();
    state.items.ids.forEach((id, i) => chosen.has(id) && ratings.add((state.items.flags[i] >> 2) & 7));
    const menu = el("div", "tile-menu");
    menu.setAttribute("role", "menu");
    menu.append(el("p", "muted", ids.length > 1 ? `Rate ${plural(ids.length, "item", "items")}` : "Rate"));
    const choose = rating => {
      closeTileMenu();
      rateIds(ids, rating);
    };
    for (let n = 5; n >= 1; n--) {
      const item = button("★".repeat(n), null, () => choose(n), "stars-choice" + (ratings.size === 1 && ratings.has(n) ? " on" : ""));
      item.setAttribute("role", "menuitemradio");
      item.setAttribute("aria-checked", String(ratings.size === 1 && ratings.has(n)));
      item.title = `${n} star${n > 1 ? "s" : ""} (${n})`;
      menu.append(item);
    }
    const clear = button("Clear rating", "close", () => choose(0));
    clear.setAttribute("role", "menuitem");
    clear.title = "Clear rating (0)";
    clear.disabled = ![...ratings].some(r => r > 0);
    menu.append(el("hr"), clear);
    menu.choose = choose; // Escape and 0–5 come through the page's keys
    menu.addEventListener("keydown", e => {
      const items = [...menu.querySelectorAll("button:not(:disabled)")];
      const at = items.indexOf(document.activeElement);
      if (e.key === "ArrowDown") items[(at + 1) % items.length]?.focus();
      else if (e.key === "ArrowUp") items[(at - 1 + items.length) % items.length]?.focus();
      else return;
      e.preventDefault();
      e.stopPropagation();
    });
    document.body.append(menu);
    const box = menu.getBoundingClientRect();
    menu.style.left = Math.max(8, Math.min(x, innerWidth - box.width - 8)) + "px";
    menu.style.top = Math.max(8, Math.min(y, innerHeight - box.height - 8)) + "px";
    tileMenu = menu;
    menu.querySelector("button:not(:disabled)").focus();
  }

  function closeTileMenu() {
    tileMenu?.remove();
    tileMenu = null;
  }

  document.addEventListener("pointerdown", event => {
    if (tileMenu && !tileMenu.contains(event.target)) closeTileMenu();
  }, true);
  $("scroller").addEventListener("scroll", closeTileMenu, { passive: true });
  addEventListener("blur", closeTileMenu);
  $("sel-album").addEventListener("click", async () => {
    try {
      const album = await chooseAlbum(`Add ${plural(state.selected.size, "item", "items")} to…`);
      if (!album) return;
      const ids = selectedIds();
      await post(`/api/albums/${album.id}/add`, { ids });
      state.albums = null;
      toast(`Added ${plural(ids.length, "item", "items")} to ${album.name}.`);
      clearSelection();
    } catch (error) {
      fail(error);
    }
  });
  $("sel-unalbum").addEventListener("click", async () => {
    const ids = selectedIds();
    try {
      await post(`/api/albums/${state.route.id}/remove`, { ids });
      state.albums = null;
      removeItems(ids);
      toast(`Took ${plural(ids.length, "item", "items")} out of the album.`);
    } catch (error) {
      fail(error);
    }
  });
  $("sel-tag").addEventListener("click", async () => {
    const tags = await getTags().catch(() => []);
    const name = await ask({ title: `Tag ${plural(state.selected.size, "item", "items")}`, input: "Tag", options: tags.filter(t => t.yours).map(t => t.name), ok: "Tag" });
    if (!name) return;
    const ids = selectedIds();
    try {
      await post("/api/tags/add", { ids, name });
      state.tags = null;
      toast(`Tagged ${plural(ids.length, "item", "items")} “${name}”.`);
      clearSelection();
    } catch (error) {
      fail(error);
    }
  });
  $("sel-utility").addEventListener("click", async () => {
    const choice = await ask({
      title: `Mark ${plural(state.selected.size, "item", "items")}`,
      text: "Utility shots (screenshots, and photos of receipts, documents, screens, boxes, tickets…) are left out of the timeline and the map.",
      choices: [
        { label: "Utility shots: leave out of the timeline", value: "yes" },
        { label: "Not utility shots: show in the timeline", value: "no" },
        { label: "Let the photo computer decide", value: "auto" },
      ],
    });
    if (choice == null) return;
    const ids = selectedIds();
    try {
      await post("/api/media/utility", { ids, utility: choice === "auto" ? null : choice === "yes" });
      toast(choice === "yes" ? `Marked ${plural(ids.length, "item", "items")} as utility shots.`
        : choice === "no" ? `Marked ${plural(ids.length, "item", "items")} as not utility shots.`
        : `The photo computer decides for ${plural(ids.length, "item", "items")} again.`);
      clearSelection();
      reloadList();
    } catch (error) {
      fail(error);
    }
  });
  $("sel-delete").addEventListener("click", () => deleteIds(selectedIds()));

  function setRatings(ids, rating) {
    const set = new Set(ids);
    state.items.ids.forEach((id, i) => {
      if (set.has(id)) state.items.flags[i] = (state.items.flags[i] & ~(7 << 2)) | (rating << 2);
    });
    refreshRows();
  }

  /** Deletes (after asking) to the photo computer's Recycle Bin; resolves to whether anything went. */
  async function deleteIds(ids, question) {
    if (!ids.length) return false;
    const flags = new Map(state.items.ids.map((id, i) => [id, state.items.flags[i]]));
    const videos = ids.filter(id => flags.get(id) & 1).length;
    const what = ids.length === 1 ? (videos ? "this video" : "this photo") : describe(ids.length, videos);
    if (!await ask({
      title: `Delete ${what}?`,
      text: question || `${ids.length === 1 ? "It goes" : "They go"} to the Recycle Bin on ${state.name}, a Live Photo's video with its photo. OneDrive keeps its copy in its own recycle bin.`,
      ok: "Delete",
      danger: true,
    })) return false;
    try {
      const result = await post("/api/media/delete", { ids });
      if (result.deleted.length) removeItems(result.deleted);
      toast(result.failed.length
        ? `Couldn't delete ${result.failed.join(", ")}${result.deleted.length ? `; deleted ${number(result.deleted.length)} others` : ""}.`
        : `Deleted ${plural(result.deleted.length, "item", "items")} (in the Recycle Bin on ${state.name}).`);
      state.albums = state.people = null;
      return result.deleted.length > 0;
    } catch (error) {
      fail(error);
      return false;
    }
  }

  /** Loads the current list again (keeping the place), after the photo computer saved something new into it. */
  function reloadList() {
    if (state.route?.section === "map") return listMap();
    if (state.listKey) state.scrollMemory.set(state.listKey, $("scroller").scrollTop);
    state.listKey = null;
    routeNow();
  }

  // ---------- Viewer ----------

  const viewer = { index: -1, id: 0, token: 0, pushed: false, details: null, zoom: { scale: 1, x: 0, y: 0 }, big: false };

  function openViewer(index) {
    const current = parseHash();
    current.params.set("view", state.items.ids[index]);
    viewer.pushed = true;
    go(hashOf(current)); // a history entry, so Back closes the viewer
  }

  function openViewerById(id) {
    let index = state.items.ids.indexOf(id);
    if (index < 0) {
      // Not in this list (an address opened directly): show it on its own.
      state.items = { ids: [id], dates: [0], flags: [0] };
      index = 0;
    }
    if (viewer.index === index && viewer.id === id && !$("viewer").hidden) return;
    show(index);
  }

  function closeViewer(fromRoute) {
    if ($("viewer").hidden) return;
    if (!fromRoute) {
      if (viewer.pushed) {
        viewer.pushed = false;
        history.back(); // the route then closes it
        return;
      }
      const current = parseHash();
      current.params.delete("view");
      history.replaceState(null, "", hashOf(current));
    }
    viewer.pushed = false;
    // Back from an editor leaves it too (a video being saved carries on on the photo computer).
    if (!$("editor").hidden) closeEditor();
    if (!$("vedit").hidden) closeVideoEditor(true);
    stopVideo();
    $("viewer").hidden = true;
    viewer.index = -1;
    viewer.id = 0;
    viewer.token++;
    const tile = document.querySelector(`.tile[data-index="${state.lastViewed}"]`);
    if (tile) tile.focus({ preventScroll: true });
  }

  function step(delta) {
    const index = viewer.index + delta;
    if (index < 0 || index >= state.items.ids.length) return;
    const current = parseHash();
    current.params.set("view", state.items.ids[index]);
    history.replaceState(null, "", hashOf(current));
    show(index);
  }

  function stopVideo() {
    const video = $("v-video");
    video.pause();
    video.removeAttribute("src");
    video.load();
    video.hidden = true;
    video.classList.remove("live");
    video.onended = null;
  }

  function message(text, withDownload) {
    const box = $("v-message");
    box.replaceChildren(el("p", null, text));
    if (withDownload) {
      const link = el("a", null, "Download the original");
      link.href = `/api/media/${viewer.id}/original`;
      link.setAttribute("download", "");
      box.append(link);
    }
    box.hidden = false;
  }

  function show(index) {
    const { ids, flags, dates } = state.items;
    const id = ids[index], f = flags[index];
    const token = ++viewer.token;
    viewer.index = index;
    viewer.id = id;
    viewer.details = null;
    viewer.big = false;
    state.lastViewed = index;
    $("viewer").hidden = false;
    stopVideo();
    resetZoom();
    $("v-overlay").replaceChildren();
    $("v-faces").hidden = $("v-text").hidden = true;
    $("v-message").hidden = true;
    $("v-prev").hidden = index === 0;
    $("v-next").hidden = index === ids.length - 1;
    $("v-live").hidden = !(f & 2);
    $("v-download").href = `/api/media/${id}/original`;
    $("v-download").setAttribute("download", "");
    $("v-date").textContent = dates[index] ? formats.full.format(new Date(dates[index] * 1000)) : "";
    updateVideoTools(f);
    $("v-place").textContent = "";
    renderStars((f >> 2) & 7);

    const img = $("v-img"), video = $("v-video");
    if (f & 1) {
      $("v-zoom").hidden = true;
      video.hidden = false;
      video.controls = true;
      video.poster = thumbUrl(id);
      video.onerror = () => {
        if (token === viewer.token && video.error)
          message("This video can't play in this browser (it may need the HEVC codec). Download it to watch.", true);
      };
      video.src = `/api/media/${id}/video`;
      video.play().catch(() => {});
    } else {
      $("v-zoom").hidden = false;
      img.src = thumbUrl(id);
      img.classList.add("preview");
      loadPhoto(id, displaySize(), token);
      preload(index + 1);
      preload(index - 1);
    }
    updateVideoTools(f); // now that the video (if any) is on screen
    loadDetails(id, token);
  }

  function loadPhoto(id, size, token) {
    const img = $("v-img");
    const full = new Image();
    full.onload = () => {
      if (token !== viewer.token) return;
      img.src = full.src;
      img.classList.remove("preview");
      drawOverlay();
    };
    full.onerror = () => {
      if (token === viewer.token && img.classList.contains("preview")) {
        img.classList.remove("preview");
        message("This photo can't be shown here.", true);
      }
    };
    full.src = displayUrl(id, size);
  }

  function preload(index) {
    const { ids, flags } = state.items;
    if (index < 0 || index >= ids.length || flags[index] & 1) return;
    new Image().src = displayUrl(ids[index], displaySize());
  }

  async function playLive() {
    const id = viewer.id, token = viewer.token;
    const live = $("v-live");
    if (live.hidden || live.classList.contains("busy")) return;
    const url = `/api/media/${id}/motion`;
    live.classList.add("busy");
    try {
      // Ask first, so a video that can't be had (only in OneDrive, say) explains itself.
      const probe = await api(url, { headers: { Range: "bytes=0-0" } });
      if (!probe.ok) {
        const body = await probe.json().catch(() => ({}));
        toast(body.error || "The video can't be played.");
        return;
      }
      probe.body?.cancel();
      if (token !== viewer.token) return;
      resetZoom();
      const video = $("v-video");
      video.controls = false;
      video.classList.add("live");
      video.hidden = false;
      video.onended = () => {
        if (token === viewer.token) {
          video.hidden = true;
          video.classList.remove("live");
          updateVideoTools();
        }
      };
      video.onerror = () => {
        if (token === viewer.token) {
          video.hidden = true;
          updateVideoTools();
          toast("This browser can't play the Live Photo's video.");
        }
      };
      video.src = url;
      updateVideoTools();
      await video.play();
    } catch (error) {
      if (error.name !== "AbortError") fail(error);
    } finally {
      live.classList.remove("busy");
    }
  }

  function renderStars(rating) {
    const stars = $("v-stars");
    stars.replaceChildren();
    for (let n = 1; n <= 5; n++) {
      const star = el("button");
      star.type = "button";
      star.title = `${n} star${n > 1 ? "s" : ""} (${n})`;
      star.classList.toggle("on", n <= rating);
      star.append(icon("star"));
      star.addEventListener("click", () => rate(rating === n ? 0 : n));
      stars.append(star);
    }
    if (rating) {
      const clear = el("button", "clear-stars", "Clear");
      clear.type = "button";
      clear.title = "Clear the rating (0)";
      clear.addEventListener("click", () => rate(0));
      stars.append(clear);
    }
  }

  async function rate(rating) {
    const id = viewer.id;
    try {
      await post(`/api/media/${id}/rating`, { rating });
      setRatings([id], rating);
      if (viewer.id === id) renderStars(rating);
    } catch (error) {
      fail(error);
    }
  }

  async function deleteCurrent() {
    if (!state.changes || $("viewer").hidden) return;
    const index = viewer.index;
    const id = viewer.id;
    if (!await deleteIds([id])) return;
    if (!state.items.ids.length) return closeViewer(false);
    const next = Math.min(index, state.items.ids.length - 1);
    const current = parseHash();
    current.params.set("view", state.items.ids[next]);
    history.replaceState(null, "", hashOf(current));
    show(next);
  }

  async function loadDetails(id, token) {
    let details;
    try {
      details = await json(`/api/media/${id}`);
    } catch (error) {
      if (token === viewer.token && error.status !== 401) $("v-place").textContent = "";
      return;
    }
    if (token !== viewer.token) return;
    viewer.details = details;
    const index = viewer.index;
    if (!state.items.dates[index]) {
      // Opened on its own: fill in what the list would have known.
      state.items.dates[index] = Date.parse(details.taken + "Z") / 1000;
      state.items.flags[index] = (details.video ? 1 : 0) | (details.live ? 2 : 0) | (details.rating << 2);
      $("v-live").hidden = !details.live;
      renderStars(details.rating);
    }
    $("v-date").textContent = formats.full.format(new Date(details.taken + "Z"));
    $("v-place").textContent = details.place || "";
    $("v-faces").hidden = details.video || !details.faces?.length;
    $("v-text").hidden = details.video || !details.textLines?.length;
    $("v-faces").classList.toggle("on", store.get("faces", false));
    $("v-text").classList.toggle("on", store.get("text", false));
    drawOverlay();
    renderPanel(details);
  }

  // Faces and text drawn over the photo (boxes are fractions of the upright picture's longer side).
  function drawOverlay() {
    const overlay = $("v-overlay");
    overlay.replaceChildren();
    const d = viewer.details, img = $("v-img");
    if (!d || d.video || $("v-zoom").hidden) return;
    const nw = img.naturalWidth || d.width, nh = img.naturalHeight || d.height;
    const sw = overlay.clientWidth, sh = overlay.clientHeight;
    if (!nw || !nh || !sw || !sh) return;
    const fit = Math.min(sw / nw, sh / nh);
    const dw = nw * fit, dh = nh * fit, ox = (sw - dw) / 2, oy = (sh - dh) / 2, long = Math.max(dw, dh);
    const place = (node, box) => {
      node.style.left = ox + box.x * long + "px";
      node.style.top = oy + box.y * long + "px";
      node.style.width = box.w * long + "px";
      node.style.height = box.h * long + "px";
      overlay.append(node);
    };
    if (store.get("faces", false)) {
      for (const face of d.faces || []) {
        const box = el("div", "face-box");
        box.title = face.name;
        box.append(el("span", null, face.name));
        box.addEventListener("click", e => {
          e.stopPropagation();
          viewer.pushed = false;
          go(`#/person/${face.id}`);
        });
        place(box, face);
      }
    }
    // Text: all of it when asked for; otherwise just the words that match the search.
    const words = searchWords();
    const all = store.get("text", false);
    for (const line of d.textLines || []) {
      for (const word of line) {
        const hit = words.some(w => word.t.toLowerCase().includes(w));
        if (all || hit) place(el("div", "text-box" + (hit ? " hit" : "")), word);
      }
    }
  }

  const searchWords = () => state.route?.section === "search"
    ? (state.route.params.get("q") || "").toLowerCase().split(/\s+/).filter(w => w.length > 1)
    : [];

  function toggleOverlay(kind) {
    const on = !store.get(kind, false);
    store.set(kind, on);
    $(kind === "faces" ? "v-faces" : "v-text").classList.toggle("on", on);
    drawOverlay();
  }

  new ResizeObserver(() => {
    if (!$("viewer").hidden) drawOverlay();
  }).observe($("v-stage"));

  function renderPanel(d) {
    const panel = $("v-panel");
    panel.hidden = !store.get("info", false);
    panel.replaceChildren();
    const section = (title, ...content) => panel.append(el("h3", null, title), ...content);
    section("Taken", el("p", null, formats.long.format(new Date(d.taken + "Z"))));
    if (d.place || d.latitude != null) {
      const parts = [];
      if (d.place) parts.push(el("p", null, d.place));
      if (d.latitude != null) {
        const link = el("a", null, `${d.latitude.toFixed(5)}, ${d.longitude.toFixed(5)}`);
        link.href = `https://www.openstreetmap.org/?mlat=${d.latitude}&mlon=${d.longitude}#map=17/${d.latitude}/${d.longitude}`;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        const line = el("p");
        line.append(link);
        parts.push(line);
      }
      section("Place", ...parts);
    }
    if (d.people?.length) {
      const people = el("div", "people");
      for (const person of d.people) {
        const link = el("a", null, person.name);
        link.href = `#/person/${person.id}`;
        people.append(link);
      }
      section("People", people);
    }
    tagsSection(d, section);
    albumsSection(d, section);
    utilitySection(d, section);
    const file = [fileSize(d.size), d.width ? `${number(d.width)} × ${number(d.height)}` : "", d.video && d.durationMs ? duration(d.durationMs / 1000) : ""]
      .filter(Boolean).join(" · ");
    section("File", el("p", null, d.name), el("p", "muted", file));
    if (d.camera) section("Camera", el("p", null, d.camera));
    if (d.edited) panel.append(el("p", "muted", "Shown with the edits made in Photo Gallery."));
    if (d.text || d.transcript?.length) findSection(d, panel, section);
    const download = el("a", "download");
    download.href = `/api/media/${d.id}/original`;
    download.setAttribute("download", "");
    download.append(icon("download"), document.createTextNode("Download the original"));
    panel.append(download);
  }

  function tagsSection(d, section) {
    const tags = d.tags || [];
    if (!tags.length && !state.changes) return;
    const row = el("div", "chips-row");
    for (const tag of tags) {
      const chip = el("a", "tag-chip" + (tag.yours && state.changes ? "" : " plain"), tag.name);
      chip.href = `#/tag/${tag.id}`;
      if (tag.yours && state.changes) {
        const remove = el("button");
        remove.type = "button";
        remove.title = "Remove the tag";
        remove.append(icon("close"));
        remove.addEventListener("click", async e => {
          e.preventDefault();
          try {
            await post("/api/tags/remove", { ids: [d.id], tagId: tag.id });
            d.tags = tags.filter(t => t !== tag);
            state.tags = null;
            renderPanel(d);
          } catch (error) {
            fail(error);
          }
        });
        chip.append(remove);
      }
      row.append(chip);
    }
    const parts = [row];
    if (state.changes) {
      const form = el("form", "tag-add");
      const input = el("input");
      input.placeholder = "Add a tag";
      input.setAttribute("list", "tag-options");
      const options = el("datalist");
      options.id = "tag-options";
      getTags().then(all => options.replaceChildren(...all.filter(t => t.yours).map(t => Object.assign(el("option"), { value: t.name })))).catch(() => {});
      form.append(input, options, Object.assign(el("button", null, "Add"), { type: "submit" }));
      form.addEventListener("submit", async e => {
        e.preventDefault();
        const name = input.value.trim();
        if (!name) return;
        try {
          await post("/api/tags/add", { ids: [d.id], name });
          state.tags = null;
          const fresh = await json(`/api/media/${d.id}`);
          if (viewer.id === d.id) {
            viewer.details = fresh;
            renderPanel(fresh);
          }
        } catch (error) {
          fail(error);
        }
      });
      parts.push(form);
    }
    section("Tags", ...parts);
  }

  // Utility shots (receipts, documents, screens…) are left out of the timeline and the map, like screenshots.
  function utilitySection(d, section) {
    const box = el("input");
    box.type = "checkbox";
    box.checked = d.utility;
    const label = el("label", "check");
    label.append(box, document.createTextNode("Utility shot"));
    label.title = "Screenshots and photos of receipts, documents, screens, boxes, tickets… are left out of the timeline and the map";
    const note = d.utilityChosen ? "Chosen by hand."
      : d.screenshot ? "A screenshot: left out of the timeline."
      : d.utility ? "Looks like a record of something (a receipt, a document, a screen…): left out of the timeline."
      : "";
    const parts = [label];
    if (note) parts.push(el("p", "muted", note));
    const set = async utility => {
      const id = d.id;
      try {
        await post("/api/media/utility", { ids: [id], utility });
        if (utility === null) {
          const fresh = await json(`/api/media/${id}`);
          d.utility = fresh.utility;
        } else {
          d.utility = utility;
        }
        d.utilityChosen = utility !== null;
      } catch (error) {
        fail(error);
      }
      if (viewer.id === id) renderPanel(d);
    };
    box.addEventListener("change", () => set(box.checked));
    if (d.utilityChosen) parts.push(button("Let the photo computer decide", null, () => set(null), "panel-button"));
    section("Kind", ...parts);
  }

  function albumsSection(d, section) {
    const ids = d.albums || [];
    if (!ids.length && !state.changes) return;
    const row = el("div", "chips-row");
    getAlbums().then(albums => {
      for (const id of ids) {
        const album = albums.find(a => a.id === id);
        if (!album) continue;
        const chip = el("a", "tag-chip plain", album.name);
        chip.href = `#/album/${id}`;
        row.append(chip);
      }
    }).catch(() => {});
    const parts = [row];
    if (state.changes) parts.push(button("Add to album…", "album", addCurrentToAlbum, "panel-button"));
    section("Albums", ...parts);
  }

  async function addCurrentToAlbum() {
    const id = viewer.id;
    try {
      const album = await chooseAlbum("Add to…");
      if (!album) return;
      await post(`/api/albums/${album.id}/add`, { ids: [id] });
      state.albums = null;
      toast(`Added to ${album.name}.`);
      if (viewer.id === id && viewer.details) {
        viewer.details.albums = [...new Set([...(viewer.details.albums || []), album.id])];
        renderPanel(viewer.details);
      }
    } catch (error) {
      fail(error);
    }
  }

  /** The photo's text and what's said in the video, with a box to find words in them. */
  function findSection(d, panel, section) {
    const find = el("input", "find");
    find.type = "search";
    find.placeholder = d.video ? "Find in what's said" : "Find in the text";
    find.value = searchWords().join(" ");
    const blocks = [];
    const highlight = (node, text) => {
      const query = find.value.trim().toLowerCase();
      node.replaceChildren();
      if (!query) return node.append(document.createTextNode(text));
      const lower = text.toLowerCase();
      let at = 0;
      for (let i = lower.indexOf(query); i >= 0; i = lower.indexOf(query, at)) {
        node.append(document.createTextNode(text.slice(at, i)), el("mark", null, text.slice(i, i + query.length)));
        at = i + query.length;
      }
      node.append(document.createTextNode(text.slice(at)));
    };
    const refresh = () => {
      blocks.forEach(b => highlight(b.node, b.text));
      panel.querySelector("mark")?.scrollIntoView({ block: "center", behavior: "smooth" });
    };
    find.addEventListener("input", refresh);
    panel.append(find);
    if (d.text) {
      const pre = el("pre");
      blocks.push({ node: pre, text: d.text });
      section("Text in this photo", pre);
    }
    if (d.transcript?.length) {
      const transcript = el("div", "transcript");
      for (const paragraph of d.transcript) {
        const p = el("p");
        const words = el("span");
        blocks.push({ node: words, text: paragraph.text });
        p.append(el("time", null, duration(paragraph.start)), words);
        p.addEventListener("click", () => {
          const video = $("v-video");
          if (!d.video || video.hidden) return;
          video.currentTime = paragraph.start;
          video.play().catch(() => {});
        });
        transcript.append(p);
      }
      section("What's said", transcript);
    }
    refresh();
  }

  function toggleInfo() {
    const open = !store.get("info", false);
    store.set("info", open);
    $("v-panel").hidden = !open;
  }

  // Zoom: the wheel (around the pointer), a double-click, pinching; drag to move around.
  function resetZoom() {
    viewer.zoom = { scale: 1, x: 0, y: 0 };
    applyZoom();
  }

  function applyZoom(showLabel) {
    const z = viewer.zoom, node = $("v-zoom");
    node.style.transform = z.scale === 1 ? "" : `translate(${z.x}px, ${z.y}px) scale(${z.scale})`;
    node.classList.toggle("zoomed", z.scale > 1);
    if (showLabel) {
      const label = $("v-zoom-label");
      label.textContent = `${Math.round(z.scale * 100)}%`;
      label.hidden = false;
      clearTimeout(applyZoom.timer);
      applyZoom.timer = setTimeout(() => (label.hidden = true), 900);
    }
    // Zoomed in: the biggest picture the photo computer makes, for detail.
    if (z.scale > 1.3 && !viewer.big && !$("v-zoom").hidden && displaySize() < 4096) {
      viewer.big = true;
      loadPhoto(viewer.id, 4096, viewer.token);
    }
  }

  function zoomAt(scale, cx, cy) {
    const z = viewer.zoom;
    const next = Math.min(8, Math.max(1, scale));
    if (next === 1) return resetZoom();
    const rect = $("v-stage").getBoundingClientRect();
    const px = cx - rect.left, py = cy - rect.top;
    z.x = px - (px - z.x) * (next / z.scale);
    z.y = py - (py - z.y) * (next / z.scale);
    z.scale = next;
    clampZoom();
    applyZoom(true);
  }

  function clampZoom() {
    const z = viewer.zoom, rect = $("v-stage").getBoundingClientRect();
    z.x = Math.min(0, Math.max(rect.width * (1 - z.scale), z.x));
    z.y = Math.min(0, Math.max(rect.height * (1 - z.scale), z.y));
  }

  const stage = $("v-stage");
  stage.addEventListener("wheel", event => {
    if ($("v-zoom").hidden || !$("v-video").hidden) return;
    event.preventDefault();
    zoomAt(viewer.zoom.scale * Math.exp(-event.deltaY * 0.0015), event.clientX, event.clientY);
  }, { passive: false });
  stage.addEventListener("dblclick", event => {
    if ($("v-zoom").hidden || event.target.closest("button, .face-box")) return;
    if (viewer.zoom.scale > 1) resetZoom();
    else zoomAt(2.5, event.clientX, event.clientY);
  });

  const pointers = new Map();
  let drag = null, pinch = null, swipe = null;
  stage.addEventListener("pointerdown", event => {
    if (event.target.closest("button, a, .face-box, video")) return;
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    stage.setPointerCapture(event.pointerId);
    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      pinch = { distance: Math.hypot(a.x - b.x, a.y - b.y), scale: viewer.zoom.scale };
      drag = swipe = null;
    } else if (viewer.zoom.scale > 1) {
      drag = { x: event.clientX, y: event.clientY, ox: viewer.zoom.x, oy: viewer.zoom.y };
      $("v-zoom").classList.add("dragging");
    } else if (event.pointerType !== "mouse") {
      swipe = { x: event.clientX, y: event.clientY };
    }
  });
  stage.addEventListener("pointermove", event => {
    if (!pointers.has(event.pointerId)) return;
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pinch && pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      zoomAt(pinch.scale * Math.hypot(a.x - b.x, a.y - b.y) / pinch.distance, (a.x + b.x) / 2, (a.y + b.y) / 2);
    } else if (drag) {
      viewer.zoom.x = drag.ox + event.clientX - drag.x;
      viewer.zoom.y = drag.oy + event.clientY - drag.y;
      clampZoom();
      applyZoom();
    }
  });
  const endPointer = event => {
    pointers.delete(event.pointerId);
    if (swipe && pointers.size === 0) {
      const dx = event.clientX - swipe.x, dy = event.clientY - swipe.y;
      if (Math.abs(dx) > 60 && Math.abs(dy) < 80) step(dx < 0 ? 1 : -1);
    }
    if (pointers.size < 2) pinch = null;
    if (pointers.size === 0) {
      drag = swipe = null;
      $("v-zoom").classList.remove("dragging");
    }
  };
  stage.addEventListener("pointerup", endPointer);
  stage.addEventListener("pointercancel", endPointer);

  $("v-close").addEventListener("click", () => closeViewer(false));
  $("v-prev").addEventListener("click", () => step(-1));
  $("v-next").addEventListener("click", () => step(1));
  $("v-info").addEventListener("click", toggleInfo);
  $("v-live").addEventListener("click", playLive);
  $("v-faces").addEventListener("click", () => toggleOverlay("faces"));
  $("v-text").addEventListener("click", () => toggleOverlay("text"));
  $("v-similar").addEventListener("click", () => {
    viewer.pushed = false;
    go(`#/similar/${viewer.id}`);
  });
  $("v-album").addEventListener("click", addCurrentToAlbum);
  $("v-delete").addEventListener("click", deleteCurrent);
  $("v-panel").addEventListener("click", event => {
    if (event.target.closest(".people a, .tag-chip")) viewer.pushed = false; // following a link replaces the viewer's entry
  });

  // ---------- Video tools in the viewer: the sharpest frame, saving a frame, stepping ----------

  const videoShown = () => !$("v-video").hidden;

  function updateVideoTools(flags = state.items.flags[viewer.index] || 0) {
    const hasVideo = (flags & 1) || (flags & 2);
    $("v-sharpest").hidden = !hasVideo;
    $("v-trim").hidden = !hasVideo;
    $("v-frame").hidden = !videoShown();
    $("v-edit").hidden = !!(flags & 1);
  }

  async function sharpestFrame() {
    const id = viewer.id, token = viewer.token, flags = state.items.flags[viewer.index] || 0;
    const button = $("v-sharpest");
    button.disabled = true;
    toast("Looking through the video for its sharpest frame…");
    try {
      const found = await post(`/api/media/${id}/sharpest`);
      if (token !== viewer.token) return;
      const video = $("v-video");
      if (!(flags & 1) && (video.hidden || !video.src.includes("/motion"))) {
        // A Live Photo: its video, paused on that frame.
        resetZoom();
        video.onended = null;
        video.classList.add("live");
        video.controls = true;
        video.hidden = false;
        video.src = `/api/media/${id}/motion`;
        await new Promise(resolve => video.addEventListener("loadedmetadata", resolve, { once: true }));
        if (token !== viewer.token) return;
      }
      video.pause();
      video.currentTime = found.seconds;
      updateVideoTools();
      toast(`Sharpest frame at ${clock(found.seconds)}${state.changes ? " · Save frame (S) keeps it as a photo" : ""}`);
    } catch (error) {
      fail(error);
    } finally {
      button.disabled = false;
    }
  }

  async function saveFrame() {
    if (!state.changes || !videoShown()) return;
    const video = $("v-video");
    video.pause();
    try {
      const saved = await post(`/api/media/${viewer.id}/frame`, { seconds: video.currentTime });
      toast(saved.message);
      reloadList();
    } catch (error) {
      fail(error);
    }
  }

  function stepFrame(delta) {
    const video = $("v-video");
    if (!videoShown()) return;
    video.pause();
    video.currentTime = Math.max(0, Math.min(video.duration || 0, video.currentTime + delta / 30));
  }

  $("v-sharpest").addEventListener("click", sharpestFrame);
  $("v-frame").addEventListener("click", saveFrame);

  // ---------- Photo editor: the preview comes from the photo computer, rendered as the app renders it ----------

  const signed = (v, digits) => (v > 0 ? "+" : "") + v.toFixed(digits);
  const SLIDERS = [
    ["Exposure", "Exposure", -2, 2, 0.05, v => signed(v, 2) + " EV"],
    ["Brightness", "Brightness", -1, 1, 0.02, v => signed(v * 100, 0)],
    ["Contrast", "Contrast", -1, 1, 0.02, v => signed(v * 100, 0)],
    ["Saturation", "Saturation", -1, 1, 0.02, v => signed(v * 100, 0)],
    ["Temperature", "Warmth", -1, 1, 0.02, v => signed(v * 100, 0)],
    ["Tint", "Tint", -1, 1, 0.02, v => signed(v * 100, 0)],
  ];
  const editor = { id: 0, ops: {}, saved: "", canOverwrite: false, cropping: false, crop: null, aspect: null, token: 0, url: null, timer: 0 };
  const isFull = c => !c || (c.X <= 0.0001 && c.Y <= 0.0001 && c.Width >= 0.9999 && c.Height >= 0.9999);
  /** The edits in the app's own format, with every field (the photo computer leaves out the ones at their defaults). */
  const normal = o => ({
    Rotation: o.Rotation || 0,
    FlipHorizontal: !!o.FlipHorizontal,
    Crop: isFull(o.Crop) ? null : { X: o.Crop.X, Y: o.Crop.Y, Width: o.Crop.Width, Height: o.Crop.Height },
    ...Object.fromEntries(SLIDERS.map(([key]) => [key, o[key] || 0])),
  });

  for (const [key, label, min, max, stepSize, show] of SLIDERS) {
    const row = el("label", "e-slider");
    const input = el("input");
    Object.assign(input, { type: "range", min, max, step: stepSize, value: 0 });
    input.dataset.key = key;
    const output = el("output");
    row.append(el("span", null, label), output, input);
    input.addEventListener("input", () => {
      editor.ops[key] = Number(input.value);
      output.textContent = show(editor.ops[key]);
      schedulePreview();
    });
    input.addEventListener("dblclick", () => {
      input.value = 0;
      input.dispatchEvent(new Event("input"));
    });
    $("e-sliders").append(row);
  }

  function syncSliders() {
    for (const input of $("e-sliders").querySelectorAll("input")) {
      const [, , , , , show] = SLIDERS.find(([key]) => key === input.dataset.key);
      input.value = editor.ops[input.dataset.key] || 0;
      input.previousElementSibling.textContent = show(Number(input.value));
    }
  }

  async function openEditor() {
    const d = viewer.details;
    if (!state.changes || !d || d.video) return;
    try {
      const info = await json(`/api/media/${d.id}/edits`);
      editor.id = d.id;
      editor.ops = normal(info.ops);
      editor.saved = JSON.stringify(editor.ops);
      editor.canOverwrite = info.canOverwrite;
    } catch (error) {
      fail(error);
      return;
    }
    $("e-overwrite").hidden = !editor.canOverwrite;
    editor.cropping = false;
    showCropTools(false);
    syncSliders();
    $("editor").hidden = false;
    preview();
  }

  function schedulePreview() {
    clearTimeout(editor.timer);
    editor.timer = setTimeout(preview, 120);
  }

  async function preview() {
    const token = ++editor.token;
    const stage = $("e-stage");
    const size = Math.min(2560, Math.ceil(Math.max(stage.clientWidth, stage.clientHeight) * (window.devicePixelRatio || 1) / 256) * 256) || 1600;
    $("e-busy").hidden = false;
    try {
      const response = await api(`/api/media/${editor.id}/preview`, { method: "POST", body: { ops: normal(editor.ops), crop: !editor.cropping, size } });
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error || "The preview couldn't be made.");
      }
      const blob = await response.blob();
      if (token !== editor.token) return;
      const url = URL.createObjectURL(blob);
      const img = $("e-img");
      await new Promise(resolve => {
        img.onload = img.onerror = resolve;
        img.src = url;
      });
      if (editor.url) URL.revokeObjectURL(editor.url);
      editor.url = url;
      if (editor.cropping) placeCrop();
    } catch (error) {
      if (token === editor.token) fail(error);
    } finally {
      if (token === editor.token) $("e-busy").hidden = true;
    }
  }

  const turn = (c, clockwise) => c && (clockwise
    ? { X: 1 - c.Y - c.Height, Y: c.X, Width: c.Height, Height: c.Width }
    : { X: c.Y, Y: 1 - c.X - c.Width, Width: c.Height, Height: c.Width });
  const mirror = c => c && { ...c, X: 1 - c.X - c.Width };

  function rotateEdit(clockwise) {
    const o = editor.ops;
    o.Rotation = (o.Rotation + (clockwise ? 90 : 270)) % 360;
    o.Crop = turn(o.Crop, clockwise);
    if (editor.cropping) editor.crop = turn(editor.crop, clockwise);
    preview();
  }

  function flipEdit() {
    const o = editor.ops;
    // Mirrors the picture as it looks now (after turning), as the app does.
    if (o.Rotation === 90 || o.Rotation === 270) o.Rotation = (o.Rotation + 180) % 360;
    o.FlipHorizontal = !o.FlipHorizontal;
    o.Crop = mirror(o.Crop);
    if (editor.cropping) editor.crop = mirror(editor.crop);
    preview();
  }

  function showCropTools(on) {
    $("e-crop-tools").hidden = !on;
    $("e-crop").hidden = !on;
    $("e-crop-button").hidden = on;
  }

  function startCrop() {
    editor.cropping = true;
    editor.crop = editor.ops.Crop ? { ...editor.ops.Crop } : { X: 0, Y: 0, Width: 1, Height: 1 };
    $("e-aspect").value = "";
    editor.aspect = null;
    showCropTools(true);
    preview();
  }

  function finishCrop() {
    editor.ops.Crop = isFull(editor.crop) ? null : { ...editor.crop };
    editor.cropping = false;
    showCropTools(false);
    preview();
  }

  /** Where the picture is drawn in the stage (it's fitted inside the image element). */
  function pictureRect() {
    const img = $("e-img"), stage = $("e-stage").getBoundingClientRect(), box = img.getBoundingClientRect();
    const nw = img.naturalWidth || 1, nh = img.naturalHeight || 1;
    const fit = Math.min(box.width / nw, box.height / nh);
    const w = nw * fit, h = nh * fit;
    return { left: box.left - stage.left + (box.width - w) / 2, top: box.top - stage.top + (box.height - h) / 2, width: w, height: h };
  }

  function placeCrop() {
    const r = pictureRect(), layer = $("e-crop"), box = $("e-box"), c = editor.crop;
    Object.assign(layer.style, { left: r.left + "px", top: r.top + "px", width: r.width + "px", height: r.height + "px" });
    Object.assign(box.style, { left: c.X * r.width + "px", top: c.Y * r.height + "px", width: c.Width * r.width + "px", height: c.Height * r.height + "px" });
  }

  /** The crop's width over its height, in the picture's own fractions, for the shape chosen (or null: any shape). */
  function cropRatio() {
    const value = $("e-aspect").value;
    if (!value) return null;
    const r = pictureRect();
    const pixels = value === "original" ? r.width / r.height : Number(value);
    return pixels * r.height / r.width;
  }

  /** Keeps the crop inside the picture, moving it back in, then shrinking it if it's still too big. */
  function fitCrop(c) {
    let { X, Y, Width, Height } = c;
    if (Width > 1) {
      Height /= Width;
      Width = 1;
    }
    if (Height > 1) {
      Width /= Height;
      Height = 1;
    }
    X = Math.min(Math.max(0, X), 1 - Width);
    Y = Math.min(Math.max(0, Y), 1 - Height);
    return { X, Y, Width, Height };
  }

  function dragCrop(start, handle, dx, dy) {
    if (handle === "move") return fitCrop({ ...start, X: start.X + dx, Y: start.Y + dy });
    const min = 0.03;
    let left = start.X, top = start.Y, right = start.X + start.Width, bottom = start.Y + start.Height;
    if (handle.includes("w")) left = Math.min(Math.max(0, left + dx), right - min);
    if (handle.includes("e")) right = Math.max(Math.min(1, right + dx), left + min);
    if (handle.includes("n")) top = Math.min(Math.max(0, top + dy), bottom - min);
    if (handle.includes("s")) bottom = Math.max(Math.min(1, bottom + dy), top + min);
    const ratio = cropRatio();
    if (!ratio) return { X: left, Y: top, Width: right - left, Height: bottom - top };
    let width = right - left, height = bottom - top;
    if (handle === "n" || handle === "s") width = height * ratio;
    else height = width / ratio;
    // Anchored on the side (or corner) that isn't being dragged.
    if (handle === "n" || handle === "s") left = start.X + start.Width / 2 - width / 2;
    else if (handle.includes("w")) left = right - width;
    if (handle === "e" || handle === "w") top = start.Y + start.Height / 2 - height / 2;
    else if (handle.includes("n")) top = bottom - height;
    return fitCrop({ X: left, Y: top, Width: width, Height: height });
  }

  let cropDrag = null;
  $("e-box").addEventListener("pointerdown", event => {
    event.preventDefault();
    const r = pictureRect();
    cropDrag = { handle: event.target.dataset.h || "move", x: event.clientX, y: event.clientY, start: { ...editor.crop }, w: r.width, h: r.height };
    $("e-box").setPointerCapture(event.pointerId);
  });
  $("e-box").addEventListener("pointermove", event => {
    if (!cropDrag) return;
    editor.crop = dragCrop(cropDrag.start, cropDrag.handle, (event.clientX - cropDrag.x) / cropDrag.w, (event.clientY - cropDrag.y) / cropDrag.h);
    placeCrop();
  });
  const endCropDrag = () => (cropDrag = null);
  $("e-box").addEventListener("pointerup", endCropDrag);
  $("e-box").addEventListener("pointercancel", endCropDrag);
  $("e-aspect").addEventListener("change", () => {
    const ratio = cropRatio();
    if (!ratio) return;
    // The biggest crop of that shape, centred on the one there was.
    const c = editor.crop;
    let width = c.Width, height = width / ratio;
    if (height > c.Height) {
      height = c.Height;
      width = height * ratio;
    }
    editor.crop = fitCrop({ X: c.X + (c.Width - width) / 2, Y: c.Y + (c.Height - height) / 2, Width: width, Height: height });
    placeCrop();
  });
  new ResizeObserver(() => {
    if (!$("editor").hidden && editor.cropping) placeCrop();
  }).observe($("e-stage"));

  async function autoEdit() {
    try {
      const result = await post(`/api/media/${editor.id}/auto`, { ops: normal(editor.ops) });
      const auto = normal(result.ops);
      for (const [key] of SLIDERS) editor.ops[key] = auto[key];
      syncSliders();
      preview();
    } catch (error) {
      fail(error);
    }
  }

  async function saveEdit(mode) {
    if (editor.cropping) finishCrop();
    if (mode === "overwrite" && !await ask({
      title: "Overwrite the original?",
      text: `The edited photo replaces the file on ${state.name} (and in OneDrive). The current version goes to the Recycle Bin there first, so it can be restored.`,
      ok: "Overwrite",
      danger: true,
    })) return;
    const buttons = ["e-keep", "e-overwrite", "e-copy"].map($);
    buttons.forEach(b => (b.disabled = true));
    try {
      const saved = await post(`/api/media/${editor.id}/edit`, { ops: normal(editor.ops), mode });
      toast(saved.message);
      bust.set(editor.id, Date.now());
      closeEditor();
      if (mode === "copy") reloadList();
      else {
        refreshRows();
        if (viewer.id === editor.id) show(viewer.index);
      }
    } catch (error) {
      fail(error);
    } finally {
      buttons.forEach(b => (b.disabled = false));
    }
  }

  async function cancelEditor() {
    if (JSON.stringify(normal(editor.ops)) !== editor.saved &&
        !await ask({ title: "Discard your changes?", text: "Nothing was saved on the photo computer.", ok: "Discard", danger: true })) return;
    closeEditor();
  }

  function closeEditor() {
    $("editor").hidden = true;
    editor.token++;
    clearTimeout(editor.timer);
    $("e-img").removeAttribute("src");
    if (editor.url) URL.revokeObjectURL(editor.url);
    editor.url = null;
  }

  $("v-edit").addEventListener("click", openEditor);
  $("e-cancel").addEventListener("click", cancelEditor);
  $("e-rotl").addEventListener("click", () => rotateEdit(false));
  $("e-rotr").addEventListener("click", () => rotateEdit(true));
  $("e-flip").addEventListener("click", flipEdit);
  $("e-crop-button").addEventListener("click", startCrop);
  $("e-crop-done").addEventListener("click", finishCrop);
  $("e-crop-reset").addEventListener("click", () => {
    editor.crop = { X: 0, Y: 0, Width: 1, Height: 1 };
    $("e-aspect").value = "";
    placeCrop();
  });
  $("e-auto").addEventListener("click", autoEdit);
  $("e-reset-light").addEventListener("click", () => {
    for (const [key] of SLIDERS) editor.ops[key] = 0;
    syncSliders();
    preview();
  });
  $("e-keep").addEventListener("click", () => saveEdit("keep"));
  $("e-overwrite").addEventListener("click", () => saveEdit("overwrite"));
  $("e-copy").addEventListener("click", () => saveEdit("copy"));

  // ---------- Video editor: trim, turn, mute; the photo computer saves an MP4 next to the original ----------

  const vedit = { id: 0, start: 0, end: null, rotation: 0, job: null };
  const clock = seconds => `${Math.floor(seconds / 60)}:${(seconds % 60).toFixed(2).padStart(5, "0")}`;

  function openVideoEditor() {
    if (!state.changes) return;
    const flags = state.items.flags[viewer.index] || 0;
    if (!(flags & 3)) return;
    $("v-video").pause();
    Object.assign(vedit, { id: viewer.id, start: 0, end: null, rotation: 0, job: null });
    const video = $("ve-video");
    video.style.transform = "";
    video.src = flags & 1 ? `/api/media/${vedit.id}/video` : `/api/media/${vedit.id}/motion`;
    $("ve-mute").checked = false;
    $("ve-progress").hidden = true;
    $("ve-save").disabled = false;
    showTrim();
    $("vedit").hidden = false;
  }

  function showTrim() {
    $("ve-start").textContent = clock(vedit.start);
    $("ve-end").textContent = vedit.end == null ? "the end" : clock(vedit.end);
  }

  function turnVideo(clockwise) {
    vedit.rotation = (vedit.rotation + (clockwise ? 90 : 270)) % 360;
    const video = $("ve-video"), box = video.getBoundingClientRect();
    const sideways = vedit.rotation % 180 !== 0;
    const scale = sideways && box.width && box.height ? Math.min(box.width / box.height, box.height / box.width) : 1;
    video.style.transform = vedit.rotation ? `rotate(${vedit.rotation}deg) scale(${scale})` : "";
  }

  async function saveVideo() {
    const video = $("ve-video");
    video.pause();
    $("ve-save").disabled = true;
    try {
      const started = await post(`/api/media/${vedit.id}/export`, { rotation: vedit.rotation, start: vedit.start, end: vedit.end, mute: $("ve-mute").checked });
      vedit.job = started.job;
      $("ve-progress").hidden = false;
      $("ve-status").textContent = `Saving on ${state.name}…`;
      while (vedit.job === started.job) {
        await new Promise(r => setTimeout(r, 700));
        const job = await json(`/api/jobs/${started.job}`);
        $("ve-fill").style.width = Math.round(job.progress * 100) + "%";
        if (!job.done) continue;
        vedit.job = null;
        if (job.error) {
          $("ve-status").textContent = job.error;
          $("ve-save").disabled = false;
          return;
        }
        toast(job.message);
        closeVideoEditor(true);
        reloadList();
      }
    } catch (error) {
      fail(error);
      vedit.job = null;
      $("ve-save").disabled = false;
    }
  }

  async function closeVideoEditor(saved) {
    if (vedit.job && !saved) {
      if (!await ask({ title: "Stop saving?", text: "The video is still being saved on the photo computer.", ok: "Stop", danger: true })) return;
      await post(`/api/jobs/${vedit.job}/cancel`).catch(() => {});
      vedit.job = null;
    }
    const video = $("ve-video");
    video.pause();
    video.removeAttribute("src");
    video.load();
    $("vedit").hidden = true;
  }

  $("v-trim").addEventListener("click", openVideoEditor);
  $("ve-cancel").addEventListener("click", () => closeVideoEditor(false));
  $("ve-set-start").addEventListener("click", () => {
    vedit.start = $("ve-video").currentTime;
    if (vedit.end != null && vedit.end <= vedit.start) vedit.end = null;
    showTrim();
  });
  $("ve-set-end").addEventListener("click", () => {
    const at = $("ve-video").currentTime;
    if (at <= vedit.start) return toast("The end has to come after the start.");
    vedit.end = at;
    showTrim();
  });
  $("ve-reset").addEventListener("click", () => {
    vedit.start = 0;
    vedit.end = null;
    showTrim();
  });
  $("ve-rotl").addEventListener("click", () => turnVideo(false));
  $("ve-rotr").addEventListener("click", () => turnVideo(true));
  $("ve-save").addEventListener("click", saveVideo);
  $("ve-stop").addEventListener("click", () => vedit.job && post(`/api/jobs/${vedit.job}/cancel`).catch(() => {}));

  // ---------- Keys ----------

  document.addEventListener("keydown", event => {
    if (tileMenu) {
      if (event.key === "Escape") closeTileMenu();
      else if (/^[0-5]$/.test(event.key)) tileMenu.choose(Number(event.key));
      else return;
      event.preventDefault();
      return;
    }
    if (!$("dialog").hidden) return;
    if (!$("editor").hidden) {
      if (event.key === "Escape") cancelEditor();
      else if ((event.ctrlKey || event.metaKey) && (event.key === "s" || event.key === "S")) saveEdit("copy");
      else if (event.key === "[" && !event.target.closest?.("input")) rotateEdit(false);
      else if (event.key === "]" && !event.target.closest?.("input")) rotateEdit(true);
      else if (event.key === "Enter" && editor.cropping) finishCrop();
      else return;
      event.preventDefault();
      return;
    }
    if (!$("vedit").hidden) {
      if (event.key === "Escape") closeVideoEditor(false);
      else if (event.key === "i" || event.key === "I") $("ve-set-start").click();
      else if (event.key === "o" || event.key === "O") $("ve-set-end").click();
      else return;
      event.preventDefault();
      return;
    }
    if (event.target instanceof Element && event.target.closest("input, select, textarea")) {
      if (event.key === "Escape") event.target.blur();
      return;
    }
    if (!$("viewer").hidden) {
      switch (event.key) {
        case "Escape":
          if (viewer.zoom.scale > 1) resetZoom();
          else closeViewer(false);
          break;
        case "ArrowLeft": step(-1); break;
        case "ArrowRight": step(1); break;
        case "i": case "I": toggleInfo(); break;
        case "f": case "F": if (!$("v-faces").hidden) toggleOverlay("faces"); break;
        case "t": case "T": if (!$("v-text").hidden) toggleOverlay("text"); break;
        case "+": case "=": zoomAt(viewer.zoom.scale * 1.5, innerWidth / 2, innerHeight / 2); break;
        case "-": zoomAt(viewer.zoom.scale / 1.5, innerWidth / 2, innerHeight / 2); break;
        case "Delete": deleteCurrent(); break;
        case "e": case "E": if (!$("v-edit").hidden) openEditor(); break;
        case "s": case "S": if (!$("v-frame").hidden) saveFrame(); break;
        case ",": stepFrame(-1); break;
        case ".": stepFrame(1); break;
        case " ":
          if (!$("v-live").hidden) playLive();
          else return;
          break;
        default:
          if (/^[0-5]$/.test(event.key) && !event.ctrlKey && !event.altKey) rate(Number(event.key));
          else return;
      }
      event.preventDefault();
      return;
    }
    if (event.key === "Escape" && state.selected.size) {
      clearSelection();
      event.preventDefault();
    } else if (/^[0-5]$/.test(event.key) && state.selected.size && !event.ctrlKey && !event.altKey && !event.metaKey) {
      rateIds(selectedIds(), Number(event.key));
      event.preventDefault();
    } else if (event.key === "Delete" && state.selected.size && state.changes) {
      deleteIds(selectedIds());
      event.preventDefault();
    } else if ((event.ctrlKey || event.metaKey) && (event.key === "a" || event.key === "A") && !$("scroller").hidden && state.items.ids.length) {
      state.items.ids.forEach(id => state.selected.add(id));
      refreshRows();
      updateSelection();
      event.preventDefault();
    } else if (event.key === "/" || (event.ctrlKey && (event.key === "e" || event.key === "E"))) {
      event.preventDefault();
      $("search").focus();
      $("search").select();
    }
  });

  // For Photo Gallery's "Another computer" page: its title bar's search box drives this one, and it signs in with the
  // passphrase it was given (telling the app how that went, so it only remembers a passphrase that worked).
  const started = start();
  window.photoGallery = {
    search,
    clearSearch,
    async signIn(passphrase) {
      await started;
      if (!$("app").hidden) return report({ signedIn: true });
      try {
        await post("/api/login", { passphrase });
        showApp();
        report({ signedIn: true });
      } catch (error) {
        $("login-error").textContent = error.message;
        report({ signedIn: false, error: error.message });
      }
    },
  };
})();
