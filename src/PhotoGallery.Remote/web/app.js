"use strict";
// Photo Gallery, opened from another computer: the library's timeline, search, people and albums, and a viewer.
// Talks only to the computer it was loaded from (see RemoteServer.cs for the API).
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

  const state = {
    name: "",
    listKey: null,
    loadToken: 0,
    items: { ids: [], dates: [], flags: [] },
    group: "month",
    yearsAgo: false,
    rows: [],
    rendered: new Map(),
    cols: 0,
    tile: 0,
    tileSize: store.get("tileSize", window.innerWidth < 700 ? 110 : 180),
    people: null,
    albums: null,
    scrollMemory: new Map(),
    lastList: "#/",
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

  const thumbUrl = id => `/api/media/${id}/thumb`;
  const displaySize = () => {
    const pixels = Math.max(screen.width, screen.height) * (window.devicePixelRatio || 1);
    return Math.min(4096, Math.max(1024, Math.ceil(pixels / 512) * 512));
  };

  async function getPeople() {
    state.people ??= await json("/api/people");
    return state.people;
  }

  async function getAlbums() {
    state.albums ??= await json("/api/albums");
    return state.albums;
  }

  // ---------- Formatting (dates are the local time the photo was taken, sent as if UTC) ----------

  const formats = {
    month: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", month: "long", year: "numeric" }),
    day: new Intl.DateTimeFormat(undefined, { timeZone: "UTC", weekday: "short", month: "short", day: "numeric", year: "numeric" }),
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

  function toast(message) {
    const node = $("toast");
    node.textContent = message;
    node.hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => (node.hidden = true), 5000);
  }

  // ---------- Sign in ----------

  async function start() {
    try {
      const hello = await json("/api/hello");
      state.name = hello.name;
      document.title = `Photos on ${hello.name}`;
      $("brand-name").textContent = `Photos on ${hello.name}`;
      if (hello.signedIn) showApp();
      else showLogin();
    } catch (error) {
      showLogin(error.message);
    }
  }

  function showLogin(message) {
    closeViewer(true);
    $("app").hidden = true;
    $("login").hidden = false;
    state.listKey = null;
    state.people = state.albums = null;
    $("login-host").textContent = state.name ? `on ${state.name}` : "";
    $("login-error").textContent = message || "";
    $("passphrase").focus();
  }

  $("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    const button = event.submitter;
    if (button) button.disabled = true;
    $("login-error").textContent = "";
    try {
      await json("/api/login", { method: "POST", body: { passphrase: $("passphrase").value } });
      $("passphrase").value = "";
      showApp();
    } catch (error) {
      $("login-error").textContent = error.message;
      $("passphrase").select();
    } finally {
      if (button) button.disabled = false;
    }
  });

  $("sign-out").addEventListener("click", async () => {
    await api("/api/logout", { method: "POST" }).catch(() => {});
    showLogin();
  });

  function showApp() {
    $("login").hidden = true;
    $("app").hidden = false;
    updateSuggestions();
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

  window.addEventListener("hashchange", routeNow);

  async function routeNow() {
    if ($("app").hidden) return;
    const current = parseHash();
    const key = listKeyOf(current);
    if (key !== state.listKey) {
      if (state.listKey != null && !$("scroller").hidden) state.scrollMemory.set(state.listKey, $("scroller").scrollTop);
      state.listKey = key;
      if (current.section !== "search") state.lastList = key;
      $("app").classList.remove("nav-open");
      await showSection(current, key);
    }
    const view = Number(current.params.get("view"));
    if (view) openViewerById(view);
    else closeViewer(true);
  }

  // ---------- Sections ----------

  const sections = {
    photos: { title: "Photos", query: { section: "timeline" }, empty: "No photos yet. They're indexed on the photo computer; check back soon." },
    onthisday: { title: "On this day", group: "year" },
    favorites: { title: "Favorites", query: { section: "favorites" }, subtitle: "Rated 4 stars or more", empty: "Rate photos with the stars in the viewer (or keys 1–5), and your 4 and 5 star photos show up here." },
    live: { title: "Live Photos", query: { section: "live" }, subtitle: "iPhone Live Photos and Android motion photos", empty: "No Live Photos found." },
    videos: { title: "Videos", query: { section: "videos" }, empty: "No videos found." },
  };

  function setNav(section) {
    const selected = section === "person" ? "people" : section === "album" ? "albums" : section;
    for (const link of document.querySelectorAll(".nav a")) link.classList.toggle("selected", link.dataset.section === selected);
  }

  async function showSection(route, key) {
    const token = ++state.loadToken;
    setNav(route.section);
    closePeoplePop();
    const p = route.params;
    if (route.section !== "search") setSearchText("");
    if (route.section === "people" || route.section === "albums") return showCards(route.section, token);

    $("cards-page").hidden = true;
    $("scroller").hidden = false;
    const query = new URLSearchParams();
    const known = sections[route.section] || sections.photos;
    let title = known.title, subtitle = known.subtitle || "", empty = known.empty || "Nothing here.";
    let group = p.get("group") || known.group || "month";
    const search = route.section === "search";
    Object.entries(known.query || {}).forEach(([k, v]) => query.set(k, v));

    if (search) {
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
    } else if (route.section === "onthisday") {
      const today = new Date();
      query.set("section", "onthisday");
      query.set("day", `${today.getMonth() + 1}-${today.getDate()}`);
      subtitle = today.toLocaleDateString(undefined, { month: "long", day: "numeric" }) + " in years past";
      empty = "Nothing from this day in other years.";
    } else if (route.section === "person") {
      query.set("section", "person");
      query.set("id", route.id);
      const person = (await getPeople().catch(() => [])).find(x => String(x.id) === route.id);
      title = person?.name || "Unnamed person";
      subtitle = person ? plural(person.count, "photo", "photos") : "";
    } else if (route.section === "album") {
      query.set("section", "album");
      query.set("id", route.id);
      const album = (await getAlbums().catch(() => [])).find(x => String(x.id) === route.id);
      title = album?.name || "Album";
      group = p.get("group") || "none";
      empty = "This album is empty.";
    }
    if (p.get("kind") && route.section !== "videos") query.set("kind", p.get("kind"));
    if (p.get("people")) query.set("people", p.get("people"));
    if (token !== state.loadToken) return;

    state.group = group;
    state.yearsAgo = route.section === "onthisday";
    $("title").textContent = title;
    $("subtitle").textContent = subtitle || "Loading…";
    updateControls(route, group);
    showItems({ ids: [], dates: [], flags: [] }, "");
    try {
      const data = await json("/api/items?" + query);
      if (token !== state.loadToken) return;
      if (search) {
        $("subtitle").textContent = (p.get("exact") === "1"
          ? "Exact words in names, folders, tags, people, places, text in photos and what's said in videos"
          : data.pictures
            ? "Best matches: what's in the picture, and the words in names, tags, people, places and text"
            : "Words in names, tags, people, places and text") + " · " + countText(data);
      } else {
        $("subtitle").textContent = [subtitle, countText(data)].filter(Boolean).join(" · ");
      }
      showItems(data, empty);
      const remembered = state.scrollMemory.get(key);
      $("scroller").scrollTop = remembered || 0;
      render();
    } catch (error) {
      if (token === state.loadToken && error.status !== 401) {
        $("subtitle").textContent = "";
        showItems({ ids: [], dates: [], flags: [] }, error.message);
      }
    }
  }

  function countText(data) {
    let videos = 0;
    for (const f of data.flags) if (f & 1) videos++;
    const photos = data.ids.length - videos;
    if (!data.ids.length) return "none";
    return [photos ? plural(photos, "photo", "photos") : "", videos ? plural(videos, "video", "videos") : ""].filter(Boolean).join(" and ");
  }

  // ---------- Controls: match, sort, group, kind, people, tile size ----------

  function updateControls(route, group) {
    const p = route.params;
    const search = route.section === "search";
    $("controls").hidden = false;
    $("exact").hidden = !search;
    $("sort").hidden = !search;
    for (const b of $("exact").children) b.classList.toggle("on", (p.get("exact") === "1") === (b.dataset.exact === "1"));
    $("sort").value = p.get("sort") || "best";
    $("group").value = group;
    $("group").disabled = search && $("sort").value === "best";
    $("kind").hidden = route.section === "videos";
    $("kind").value = p.get("kind") || "";
    renderChips(route);
  }

  function changeParam(name, value, extra) {
    const current = parseHash();
    current.params.delete("view");
    if (value == null || value === "") current.params.delete(name);
    else current.params.set(name, value);
    if (extra) extra(current.params);
    replaceRoute(current);
  }

  for (const button of $("exact").children)
    button.addEventListener("click", () => changeParam("exact", button.dataset.exact === "1" ? "1" : null));
  $("sort").addEventListener("change", () => changeParam("sort", $("sort").value === "best" ? null : $("sort").value));
  $("group").addEventListener("change", () => changeParam("group", $("group").value));
  $("kind").addEventListener("change", () => changeParam("kind", $("kind").value));

  function zoom(factor) {
    state.tileSize = Math.round(Math.min(400, Math.max(72, state.tileSize * factor)));
    store.set("tileSize", state.tileSize);
    layout();
  }
  $("zoom-in").addEventListener("click", () => zoom(1.25));
  $("zoom-out").addEventListener("click", () => zoom(0.8));

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

  async function openPeoplePop() {
    $("people-pop").hidden = false;
    $("people-find").value = "";
    await fillPeopleList();
    $("people-find").focus();
  }

  function closePeoplePop() {
    $("people-pop").hidden = true;
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

  $("people-button").addEventListener("click", event => {
    event.stopPropagation();
    if ($("people-pop").hidden) openPeoplePop();
    else closePeoplePop();
  });
  $("people-find").addEventListener("input", fillPeopleList);
  $("people-clear").addEventListener("click", () => {
    closePeoplePop();
    changeParam("people", null);
  });
  $("people-pop").addEventListener("click", event => event.stopPropagation());
  document.addEventListener("click", closePeoplePop);

  // ---------- Search ----------

  function setSearchText(text) {
    $("search").value = text;
    $("search-clear").hidden = !text;
  }

  function remember(text) {
    const recent = store.get("recent", []).filter(x => x.toLowerCase() !== text.toLowerCase());
    recent.unshift(text);
    store.set("recent", recent.slice(0, 12));
    updateSuggestions();
  }

  async function updateSuggestions() {
    const list = $("suggestions");
    const values = [...store.get("recent", [])];
    try {
      for (const person of await getPeople()) if (person.name && values.length < 400) values.push(person.name);
    } catch {
      // suggestions are optional
    }
    list.replaceChildren(...[...new Set(values)].map(v => {
      const option = el("option");
      option.value = v;
      return option;
    }));
  }

  /** Runs a search, keeping the current match, sort and filters if already searching. */
  function search(text) {
    text = (text || "").trim();
    if (!text) return clearSearch();
    const current = parseHash();
    const next = new URLSearchParams();
    if (current.section === "search") {
      for (const key of ["exact", "sort", "group", "kind", "people"]) if (current.params.get(key)) next.set(key, current.params.get(key));
    }
    next.set("q", text);
    location.hash = hashOf({ section: "search", id: null, params: next });
  }

  function clearSearch() {
    setSearchText("");
    if (parseHash().section === "search") location.hash = state.lastList || "#/";
  }

  $("search-form").addEventListener("submit", event => {
    event.preventDefault();
    $("search").blur();
    search($("search").value);
  });
  $("search").addEventListener("input", () => ($("search-clear").hidden = !$("search").value));
  $("search-clear").addEventListener("click", () => {
    clearSearch();
    $("search").focus();
  });
  $("menu").addEventListener("click", event => {
    event.stopPropagation();
    $("app").classList.toggle("nav-open");
  });
  $("main").addEventListener("click", () => $("app").classList.remove("nav-open"));

  // ---------- People and albums ----------

  async function showCards(kind, token) {
    $("scroller").hidden = true;
    $("controls").hidden = true;
    $("chips").replaceChildren();
    $("cards-page").hidden = false;
    $("title").textContent = kind === "people" ? "People" : "Albums";
    $("subtitle").textContent = "Loading…";
    $("cards").replaceChildren();
    $("cards-find").value = "";
    $("cards-find").placeholder = kind === "people" ? "Find a person" : "Find an album";
    try {
      const rows = kind === "people" ? await getPeople() : await getAlbums();
      if (token !== state.loadToken) return;
      $("subtitle").textContent = kind === "people"
        ? `${plural(rows.length, "person", "people")} OneDrive recognised in your photos`
        : plural(rows.length, "album", "albums");
      const cards = rows.map(row => {
        const card = el("a", "card" + (kind === "people" ? " person" : ""));
        card.href = `#/${kind === "people" ? "person" : "album"}/${row.id}`;
        card.dataset.name = (row.name || "").toLowerCase();
        const cover = el("div", "cover");
        const src = kind === "people" ? `/api/people/${row.id}/face` : row.cover ? thumbUrl(row.cover) : null;
        const initials = (row.name || "?").split(/\s+/).map(w => w[0]).join("").slice(0, 2).toUpperCase();
        if (src) {
          const img = el("img");
          img.loading = "lazy";
          img.alt = "";
          img.src = src;
          img.addEventListener("error", () => img.replaceWith(document.createTextNode(initials)));
          cover.append(img);
        } else {
          cover.textContent = initials;
        }
        card.append(cover, el("span", "name", row.name || (kind === "people" ? "Unnamed person" : "Untitled")),
          el("span", "muted", plural(row.count, "photo", "photos")));
        return card;
      });
      $("cards").replaceChildren(...cards);
      if (!cards.length) $("cards").append(el("p", "muted", kind === "people" ? "No people yet." : "No albums yet."));
    } catch (error) {
      if (token === state.loadToken && error.status !== 401) $("subtitle").textContent = error.message;
    }
  }

  $("cards-find").addEventListener("input", () => {
    const find = $("cards-find").value.trim().toLowerCase();
    for (const card of $("cards").children) card.hidden = !!find && !(card.dataset.name || "").includes(find);
  });

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
    const sizer = $("sizer");
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
    sizer.style.height = top + 24 + "px";
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
    const img = el("img");
    img.alt = "";
    img.loading = "lazy";
    img.decoding = "async";
    img.draggable = false;
    img.src = thumbUrl(id);
    img.addEventListener("error", () => tile.classList.add("broken"));
    tile.append(img);
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

  function refreshTile(index) {
    for (const [i, node] of state.rendered) {
      const row = state.rows[i];
      if (row.start != null && index >= row.start && index < row.end) {
        const fresh = buildRow(row);
        fresh.style.transform = node.style.transform;
        node.replaceWith(fresh);
        state.rendered.set(i, fresh);
      }
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
  $("sizer").addEventListener("click", event => {
    const tile = event.target.closest(".tile");
    if (tile) openViewer(Number(tile.dataset.index));
  });

  // ---------- Viewer ----------

  const viewer = { index: -1, id: 0, token: 0, pushed: false, details: null };

  function openViewer(index) {
    const current = parseHash();
    current.params.set("view", state.items.ids[index]);
    viewer.pushed = true;
    location.hash = hashOf(current); // a history entry, so Back closes the viewer
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
    state.lastViewed = index;
    $("viewer").hidden = false;
    stopVideo();
    $("v-message").hidden = true;
    $("v-prev").hidden = index === 0;
    $("v-next").hidden = index === ids.length - 1;
    $("v-live").hidden = !(f & 2);
    $("v-download").href = `/api/media/${id}/original`;
    $("v-download").setAttribute("download", "");
    $("v-date").textContent = dates[index] ? formats.full.format(new Date(dates[index] * 1000)) : "";
    $("v-place").textContent = "";
    renderStars((f >> 2) & 7);

    const img = $("v-img"), video = $("v-video");
    if (f & 1) {
      img.hidden = true;
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
      img.hidden = false;
      img.src = thumbUrl(id);
      img.classList.add("preview");
      const full = new Image();
      full.onload = () => {
        if (token !== viewer.token) return;
        img.src = full.src;
        img.classList.remove("preview");
      };
      full.onerror = () => {
        if (token === viewer.token) {
          img.classList.remove("preview");
          message("This photo can't be shown here.", true);
        }
      };
      full.src = `/api/media/${id}/display?size=${displaySize()}`;
      preload(index + 1);
      preload(index - 1);
    }
    loadDetails(id, token);
  }

  function preload(index) {
    const { ids, flags } = state.items;
    if (index < 0 || index >= ids.length || flags[index] & 1) return;
    new Image().src = `/api/media/${ids[index]}/display?size=${displaySize()}`;
  }

  async function playLive() {
    const id = viewer.id, token = viewer.token;
    const button = $("v-live");
    if (button.hidden || button.classList.contains("busy")) return;
    const url = `/api/media/${id}/motion`;
    button.classList.add("busy");
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
      const video = $("v-video");
      video.controls = false;
      video.classList.add("live");
      video.hidden = false;
      video.onended = () => {
        if (token === viewer.token) {
          video.hidden = true;
          video.classList.remove("live");
        }
      };
      video.onerror = () => {
        if (token === viewer.token) {
          video.hidden = true;
          toast("This browser can't play the Live Photo's video.");
        }
      };
      video.src = url;
      await video.play();
    } catch (error) {
      if (error.name !== "AbortError" && error.message !== "Signed out.") toast(error.message);
    } finally {
      button.classList.remove("busy");
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
  }

  async function rate(rating) {
    const index = viewer.index, id = viewer.id;
    try {
      await json(`/api/media/${id}/rating`, { method: "POST", body: { rating } });
      const flags = state.items.flags;
      if (state.items.ids[index] === id) {
        flags[index] = (flags[index] & ~(7 << 2)) | (rating << 2);
        refreshTile(index);
      }
      if (viewer.id === id) renderStars(rating);
    } catch (error) {
      toast(error.message);
    }
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
    renderPanel(details);
  }

  function renderPanel(d) {
    const panel = $("v-panel");
    panel.hidden = !store.get("info", false);
    panel.replaceChildren();
    const section = (title, ...content) => {
      panel.append(el("h3", null, title), ...content);
    };
    section("Taken", el("p", null, formats.long.format(new Date(d.taken + "Z"))));
    if (d.place || d.latitude != null) {
      const parts = [];
      if (d.place) parts.push(el("p", null, d.place));
      if (d.latitude != null) {
        const map = el("a", null, `${d.latitude.toFixed(5)}, ${d.longitude.toFixed(5)}`);
        map.href = `https://www.openstreetmap.org/?mlat=${d.latitude}&mlon=${d.longitude}#map=17/${d.latitude}/${d.longitude}`;
        map.target = "_blank";
        map.rel = "noopener noreferrer";
        const line = el("p");
        line.append(map);
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
    const file = [fileSize(d.size), d.width ? `${number(d.width)} × ${number(d.height)}` : "", d.video && d.durationMs ? duration(d.durationMs / 1000) : ""]
      .filter(Boolean).join(" · ");
    section("File", el("p", null, d.name), el("p", "muted", file));
    if (d.camera) section("Camera", el("p", null, d.camera));
    if (d.edited) panel.append(el("p", "muted", "Shown with the edits made in Photo Gallery."));
    if (d.text) section("Text in this photo", el("pre", null, d.text));
    if (d.transcript?.length) {
      const transcript = el("div", "transcript");
      for (const paragraph of d.transcript) {
        const p = el("p");
        const time = el("time", null, duration(paragraph.start));
        p.append(time, document.createTextNode(paragraph.text));
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
    const download = el("a", "download");
    download.href = `/api/media/${d.id}/original`;
    download.setAttribute("download", "");
    download.append(icon("download"), document.createTextNode("Download the original"));
    panel.append(download);
  }

  function toggleInfo() {
    const open = !store.get("info", false);
    store.set("info", open);
    $("v-panel").hidden = !open;
  }

  $("v-close").addEventListener("click", () => closeViewer(false));
  $("v-prev").addEventListener("click", () => step(-1));
  $("v-next").addEventListener("click", () => step(1));
  $("v-info").addEventListener("click", toggleInfo);
  $("v-live").addEventListener("click", playLive);
  $("v-panel").addEventListener("click", event => {
    if (event.target.closest(".people a")) viewer.pushed = false; // following a person replaces the viewer's entry
  });

  // Swipe between photos on touch screens.
  let swipe = null;
  $("v-stage").addEventListener("pointerdown", event => {
    if (event.pointerType !== "mouse") swipe = { x: event.clientX, y: event.clientY };
  });
  $("v-stage").addEventListener("pointerup", event => {
    if (!swipe) return;
    const dx = event.clientX - swipe.x, dy = event.clientY - swipe.y;
    swipe = null;
    if (Math.abs(dx) > 60 && Math.abs(dy) < 80) step(dx < 0 ? 1 : -1);
  });

  document.addEventListener("keydown", event => {
    if (event.target instanceof Element && event.target.closest("input, select, textarea")) {
      if (event.key === "Escape") event.target.blur();
      return;
    }
    if (!$("viewer").hidden) {
      switch (event.key) {
        case "Escape": closeViewer(false); break;
        case "ArrowLeft": step(-1); break;
        case "ArrowRight": step(1); break;
        case "i": case "I": toggleInfo(); break;
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
    if (event.key === "/" || (event.ctrlKey && (event.key === "e" || event.key === "E"))) {
      event.preventDefault();
      $("search").focus();
      $("search").select();
    }
  });

  // For Photo Gallery's "Another computer" page: its title bar's search box drives this one, and it signs in with the
  // passphrase it was given (telling the app how that went, so it only remembers a passphrase that worked).
  const started = start();
  const report = message => window.chrome?.webview?.postMessage(message);
  window.photoGallery = {
    search,
    clearSearch,
    async signIn(passphrase) {
      await started;
      if (!$("app").hidden) return report({ signedIn: true });
      try {
        await json("/api/login", { method: "POST", body: { passphrase } });
        showApp();
        report({ signedIn: true });
      } catch (error) {
        $("login-error").textContent = error.message;
        report({ signedIn: false, error: error.message });
      }
    },
  };
})();
