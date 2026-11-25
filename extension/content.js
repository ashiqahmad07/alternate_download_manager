// ADM content script, in every frame: reports link clicks made with the special keys held
// (see CaptureRules.Decide in ADM.Core), sends a link straight to ADM when the force keys
// are held, and lists the page's links for "Download all links with ADM".

(() => {
  // The background injects this file again into pages opened before the extension was installed.
  if (globalThis.admContentLoaded) return;
  globalThis.admContentLoaded = true;

  const ext = globalThis.browser ?? chrome;
  let rules = { preventKeys: ["alt"], forceKeys: ["insert"], forceOnLinkClick: true, videoPanel: "full", videoPanelExceptionSites: "" };
  let insertHeld = false;

  ext.storage.local
    .get("rules")
    .then(({ rules: r }) => {
      if (r) rules = r;
    })
    .catch(() => {});
  ext.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && changes.rules?.newValue) rules = changes.rules.newValue;
  });

  // Only real input counts: a page can dispatch events with modifier flags set, but it cannot
  // make them trusted.
  addEventListener("keydown", (e) => e.isTrusted && e.key === "Insert" && (insertHeld = true), true);
  addEventListener("keyup", (e) => e.isTrusted && e.key === "Insert" && (insertHeld = false), true);
  addEventListener("blur", () => (insertHeld = false), true);

  const heldKeys = (e) => [e.ctrlKey && "ctrl", e.altKey && "alt", e.shiftKey && "shift", insertHeld && "insert"].filter(Boolean);
  const allHeld = (combo, held) => !!combo && combo.length > 0 && combo.every((k) => held.includes(k));
  const isWeb = (url) => /^https?:/i.test(url);

  function linkOf(e) {
    const node = e.composedPath().find((n) => n instanceof Element && n.matches("a[href], area[href]"));
    // SVG links carry an object, not an address.
    return node && typeof node.href === "string" && isWeb(node.href) ? node.href : null;
  }

  addEventListener(
    "click",
    (e) => {
      if (!e.isTrusted || e.button !== 0) return;
      const url = linkOf(e);
      if (!url) return;
      const keys = heldKeys(e);
      const prevent = allHeld(rules.preventKeys, keys);
      const force = !prevent && allHeld(rules.forceKeys, keys);
      // An ordinary click is not reported, so it never wakes the background.
      if (!prevent && !force) return;
      if (force && rules.forceOnLinkClick) {
        e.preventDefault();
        e.stopImmediatePropagation();
        ext.runtime.sendMessage({ cmd: "forceLink", url }).catch(() => {});
        return;
      }
      ext.runtime.sendMessage({ cmd: "linkClick", url, keys }).catch(() => {});
    },
    true
  );

  function collectLinks(selectionOnly) {
    let anchors = [...document.querySelectorAll("a[href], area[href]")];
    let images = [...document.images];
    if (selectionOnly) {
      const selection = getSelection();
      const selected = (node) => {
        for (let i = 0; i < selection.rangeCount; i++) if (selection.getRangeAt(i).intersectsNode(node)) return true;
        return false;
      };
      anchors = anchors.filter(selected);
      images = images.filter(selected);
    }

    const page = location.href.split("#")[0];
    const seen = new Set();
    const links = [];
    const add = (href, text, image) => {
      if (typeof href !== "string" || !isWeb(href)) return;
      // The fragment never reaches the server, and "#top" style links point back at this page.
      const url = href.split("#")[0];
      if (url === page || seen.has(url)) return;
      seen.add(url);
      links.push({ url, text: (text || "").replace(/\s+/g, " ").trim().slice(0, 200), image });
    };
    for (const a of anchors) add(a.href, a.textContent || a.title, false);
    for (const img of images) add(img.currentSrc || img.src, img.alt, true);
    return links;
  }

  // --- Download panel over web players ----------------------------------------------

  const MIN_SIZE = { video: { width: 200, height: 120 }, audio: { width: 150, height: 20 } };

  const wildcard = (pattern, text) =>
    new RegExp("^" + pattern.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*") + "$", "i").test(text);

  // Same matching as ADM's SiteList, as in background.js.
  function siteListMatches(list, url) {
    const host = new URL(url).hostname;
    return (list || "").split(/\s+/).filter(Boolean).some((p) => wildcard(p, p.includes("/") ? url : host));
  }

  /** Whether players on this page get a panel at all. */
  const panelOn = (r, pageUrl) => (r.videoPanel ?? "full") !== "off" && !siteListMatches(r.videoPanelExceptionSites, pageUrl);

  /** Whether a player of this size on this page gets a panel. */
  function panelAllowed(r, pageUrl, rect, kind) {
    if (!panelOn(r, pageUrl)) return false;
    const min = MIN_SIZE[kind];
    return rect.width >= min.width && rect.height >= min.height;
  }

  const kindOf = (el) => (el instanceof HTMLVideoElement ? "video" : el instanceof HTMLAudioElement ? "audio" : null);
  const dismissed = new WeakSet();
  let panel = null;
  // The top frame shows the panel for its own players and for those of the frames in it.
  const inTop = window.top === window;

  // --- The video's name ---

  // A feed post, and the post's text where the site marks it, as Facebook's feed does.
  const POST = '[role="article"], [aria-posinset], article';
  const POST_TEXT = '[data-ad-rendering-role="story_message"], [data-ad-preview="message"], [data-ad-comet-preview="message"]';
  const NAME_CHARS = 60;

  /** The site's name as its address gives it: "facebook" for www.facebook.com. */
  const siteNameOf = (host) => (host || "").split(".").find((label) => !/^(www\d*|m|web|mobile)$/i.test(label)) || "";
  const sameName = (text, site) => !!site && text.replace(/\s+/g, "").toLowerCase() === site.toLowerCase();

  /** A text's first line, cut at a word to at most 60 characters. */
  function shorten(text) {
    const line = (text || "").split("\n").map((l) => l.replace(/\s+/g, " ").trim()).find(Boolean) || "";
    const chars = [...line.replace(/\s*…?\s*See more$/i, "")];
    if (chars.length <= NAME_CHARS) return chars.join("");
    const cut = chars.slice(0, NAME_CHARS).join("");
    const space = cut.lastIndexOf(" ");
    return (space > NAME_CHARS / 2 ? cut.slice(0, space) : cut).replace(/[\s,.;:!?–—-]+$/, "") + "…";
  }

  /** The page's title without its count of notifications, the site's name, or a "By" byline: "A talk" for "(3) A talk | By Page | Facebook". */
  function titleWithoutSite(title, host) {
    const site = siteNameOf(host);
    const parts = (title || "").replace(/^\(\d+\+?\)\s*/, "").trim().split(/(\s+[|–—-]\s+)/);
    while (parts.length > 2 && (sameName(parts.at(-1), site) || /^By\s/i.test(parts.at(-1).trim()))) parts.splice(-2, 2);
    const rest = parts.join("").trim();
    return sameName(rest, site) ? "" : rest;
  }

  /** A short name for a player's video: its post's text, else the page's title; "" when neither names it. */
  function captionOf(el, title = document.title, host = new URL(location.href).hostname) {
    const text = el?.closest?.(POST)?.querySelector(POST_TEXT);
    return shorten((text && (text.innerText || text.textContent)) || titleWithoutSite(title, host));
  }

  // A file named by an id alone, as Facebook's CDN names them.
  const opaqueName = (name) => /^[\w-]{32,}\.\w{2,4}$/.test(name);
  const withTitle = (message, title) => (title ? { ...message, title } : message);

  /**
   * The menu's choices: each quality of an HLS or DASH stream and its audio track, or each media file, by the video's
   * caption when there is one, else by its file name, or the site's name when that is an id alone.
   */
  function choices(media, caption = "") {
    const list = [];
    for (const m of media) {
      if (m.kind === "joined") {
        list.push({ label: m.label, message: withTitle({ media: m }, caption) });
      } else if (m.qualities?.length) {
        for (const q of m.qualities) list.push({ label: q.label, message: withTitle({ media: m, bandwidth: q.bandwidth }, caption) });
        if (m.audioOnly) list.push({ label: "Audio only", message: withTitle({ media: m, audioOnly: true }, caption) });
      } else if (m.kind === "hls" || m.kind === "dash") {
        list.push({ label: "Best quality", message: withTitle({ media: m }, caption) });
      } else {
        let file = m.url;
        try {
          file = decodeURIComponent(new URL(m.url).pathname.split("/").pop()) || m.url;
        } catch {}
        let title = caption;
        if (!title && opaqueName(file)) {
          const site = siteNameOf(new URL(location.href).hostname);
          title = `${site.charAt(0).toUpperCase()}${site.slice(1)} ${(m.type || "").startsWith("audio/") ? "audio" : "video"}`.trim();
        }
        const size = m.size > 0 ? ` (${(m.size / 1048576).toFixed(1)} MB)` : "";
        list.push({ label: (title || file) + size, message: withTitle({ media: m }, title) });
      }
    }
    return list;
  }

  const megabytes = (bytes) => ` (${(bytes / 1048576).toFixed(1)} MB)`;

  /**
   * A player gets the panel when the tab's requests found media, or when it is fed through Media Source, as YouTube's,
   * Facebook's and most sites' players are: then ADM asks yt-dlp what the page offers.
   */
  function panelWanted(media, el, pageUrl) {
    return media.length > 0 || (typeof el.currentSrc === "string" && el.currentSrc.startsWith("blob:") && isWeb(pageUrl));
  }

  /**
   * The menu: the site's choices first, as yt-dlp read them, then what the tab's requests found. Until ADM answers,
   * "Finding qualities…"; with a tool missing, "Set up video downloads…"; and the site's error only when there is
   * nothing else to offer.
   */
  function menuItems(media, site, caption = "") {
    const found = choices(media, caption);
    if (!site) return withDownloadAll(found);
    if (!site.reply) return [{ label: "Finding qualities…", disabled: true, busy: true }, ...found];
    const { reply } = site;
    const items = (reply.ok ? reply.choices || [] : []).map((c) => ({
      label: c.label + (c.size > 0 ? megabytes(c.size) : ""),
      message: { site: { url: site.url, key: c.key, title: reply.title || caption || undefined } },
    }));
    if (reply.missingTools?.length) items.push({ label: "Set up video downloads…", setup: true });
    items.push(...found);
    if (items.length === 0) items.push({ label: reply.error || "This video can't be downloaded.", disabled: true });
    return withDownloadAll(items);
  }

  /** "Download all" heads a menu of two or more files, and starts each of them without asking about each. */
  function withDownloadAll(items) {
    const messages = items.filter((i) => i.message).map((i) => ({ ...i.message, all: true }));
    return messages.length > 1 ? [{ label: "Download all", messages }, ...items] : items;
  }

  /** The video a page's address names, as Facebook's do: /reel/ID, /watch/?v=ID, …/videos/…/ID; else null. */
  function pageVideoId(pageUrl) {
    const url = new URL(pageUrl);
    const v = url.searchParams.get("v");
    if (v && /^\d+$/.test(v)) return v;
    return /\/(?:reel|videos)\/(?:.*\/)?(\d+)\/?$/.exec(url.pathname)?.[1] ?? null;
  }

  /**
   * The tab's files of the video a player shows. Facebook's feed and reel pages load videos besides the one shown, and
   * each file names its video and its length in whole seconds: those as long as the player's video are kept, and of
   * them, the video the page's address names if it is among them. A file that names no video is kept, as nothing says
   * it is another's; and every file is, when nothing matches.
   */
  function ofThisVideo(media, pageUrl, duration) {
    let mine = media.filter((m) => m.videoId);
    const sameLength = mine.filter((m) => m.durationS > 0 && Math.abs(m.durationS - duration) < 1.5);
    if (sameLength.length) mine = sameLength;
    const id = pageVideoId(pageUrl);
    if (mine.some((m) => m.videoId === id)) mine = mine.filter((m) => m.videoId === id);
    return media.filter((m) => !m.videoId || mine.includes(m));
  }

  // --- Reading ahead ---

  const READ_AHEAD_MS = 0;
  const readAheadOf = new Set();
  // Page address -> ADM's answer and when it came, kept as long as the background keeps it.
  const answers = new Map();
  const ANSWER_MS = 30 * 60 * 1000;

  /**
   * Asks ADM to read a page's video as soon as the panel shows on its player, so its qualities are ready when the
   * menu opens rather than read only then: once a page, and not for a player the page has since left. A page's
   * choices are kept for the menu; a missing tool or an ADM that didn't answer is asked about again then.
   */
  function readAhead(url, stillShown, wait = READ_AHEAD_MS) {
    if (readAheadOf.has(url)) return;
    setTimeout(() => {
      if (readAheadOf.has(url) || !stillShown()) return;
      readAheadOf.add(url);
      ext.runtime
        .sendMessage({ cmd: "siteChoices", url })
        .then((reply) => {
          if (reply?.ok) answers.set(url, { at: Date.now(), reply });
        })
        .catch(() => {});
    }, wait);
  }

  /** What a read ahead found for this page, while it is fresh; else undefined. */
  function knownAnswer(url, now = Date.now()) {
    const kept = answers.get(url);
    return kept && now - kept.at < ANSWER_MS ? kept.reply : undefined;
  }

  /**
   * Reads ahead for the page the panel's player shows now: when the panel takes a player, and when a site like
   * YouTube moves to another video in the same player without loading a new page.
   */
  function readAheadShown() {
    const player = panel?.target;
    if (!player?.fedBySource) return;
    const page = player.page();
    if (isWeb(page)) readAhead(page, () => panel.target === player && player.page() === page && panel.host.style.display !== "none");
  }

  /** One file found and nothing to ask the site: downloaded at once, as before. */
  const downloadsAtOnce = (items, site) => !site && items.length === 1 && !!items[0].message;

  function setMenuOpen(open) {
    panel.menu.hidden = !open;
    panel.main.setAttribute("aria-expanded", String(open));
  }

  function download(choice) {
    ext.runtime.sendMessage({ cmd: "panelDownload", ...choice.message }).catch(() => {});
    setMenuOpen(false);
  }

  function pick(item) {
    if (item.setup) {
      ext.runtime.sendMessage({ cmd: "setupVideoTools" }).catch(() => {});
      setMenuOpen(false);
    } else if (item.messages) {
      for (const message of item.messages) ext.runtime.sendMessage({ cmd: "panelDownload", ...message }).catch(() => {});
      setMenuOpen(false);
    } else if (item.message) {
      download(item);
    }
  }

  // --- The panel's look ---

  // Drawn as lines on a 24-unit square, in the text's colour.
  const ICONS = {
    download: ["M12 3.5v11.5", "m6.5 10 5.5 5.5 5.5-5.5", "M4.5 20.5h15"],
    all: ["M12 2.5v10", "m8 8.5 4 4 4-4", "M4 14.5v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4"],
    chevron: ["m7 10 5 5 5-5"],
    close: ["M7 7l10 10", "M17 7 7 17"],
    setup: ["M14.7 6.3a4 4 0 0 0-5.4 5.4l-5.8 5.8a1.5 1.5 0 0 0 2 2l5.8-5.8a4 4 0 0 0 5.4-5.4l-2.6 2.6-2-2z"],
    info: ["M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z", "M12 11v5", "M12 7.5h.01"],
  };

  function icon(paths, className = "") {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    for (const [name, value] of Object.entries({
      viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", "stroke-width": "2.2", "stroke-linecap": "round",
      "stroke-linejoin": "round", "aria-hidden": "true", class: className,
    }))
      svg.setAttribute(name, value);
    for (const d of paths) {
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      path.setAttribute("d", d);
      svg.append(path);
    }
    return svg;
  }

  function textNode(tag, text, className) {
    const node = document.createElement(tag);
    node.className = className;
    node.textContent = text;
    return node;
  }

  /** A choice's name and its size apart, as the menu shows them: ["1080p MP4", "145.0 MB"] for "1080p MP4 (145.0 MB)". */
  function labelParts(label) {
    const size = /\s\((\d+(?:\.\d+)? MB)\)$/.exec(label);
    return size ? [label.slice(0, size.index), size[1]] : [label, ""];
  }

  /** The menu, under the video's name: each choice with its size apart, Download all first, and a turning ring while ADM reads the site. */
  function showMenu(items, heading) {
    const rows = [textNode("div", heading || "Download with ADM", "head")];
    for (const item of items) {
      const button = document.createElement("button");
      button.type = "button";
      button.setAttribute("role", "menuitem");
      button.disabled = !!item.disabled;
      if (item.messages) button.className = "all";
      const [name, size] = labelParts(item.label);
      const lead = item.busy
        ? textNode("span", "", "spin")
        : icon(item.messages ? ICONS.all : item.setup ? ICONS.setup : item.disabled ? ICONS.info : ICONS.download);
      button.append(lead, textNode("span", name, "name"));
      if (size) button.append(textNode("span", size, "size"));
      button.addEventListener("click", () => pick(item));
      rows.push(button);
    }
    panel.menu.replaceChildren(...rows);
  }

  const PANEL_STYLE = `
    .bar { display: inline-flex; align-items: stretch; position: relative; border-radius: 999px; color: #fff;
           font: 600 13px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif; letter-spacing: .1px;
           background: linear-gradient(135deg, #3b82f6 0%, #4f46e5 100%); -webkit-font-smoothing: antialiased; user-select: none;
           box-shadow: inset 0 1px 0 rgba(255, 255, 255, .28), 0 0 0 1px rgba(30, 64, 175, .5), 0 6px 18px -6px rgba(37, 99, 235, .75),
                       0 2px 6px rgba(0, 0, 0, .25);
           animation: adm-in .18s ease-out; }
    button { all: unset; box-sizing: border-box; display: inline-flex; align-items: center; gap: 7px; cursor: pointer; font: inherit; color: inherit; }
    .bar > button { height: 30px; transition: background-color .12s; }
    .bar > button:hover { background: rgba(255, 255, 255, .16); }
    .bar > button:active { background: rgba(15, 23, 42, .14); }
    .bar > button:focus-visible { outline: 2px solid #fff; outline-offset: -3px; }
    .main { padding: 0 10px 0 11px; border-radius: 999px 0 0 999px; }
    .mini .main { padding: 0 8px 0 10px; }
    .close { padding: 0 10px 0 9px; border-radius: 0 999px 999px 0; border-left: 1px solid rgba(255, 255, 255, .25); }
    .bar svg { width: 16px; height: 16px; flex: none; }
    .bar .chevron { width: 13px; height: 13px; margin-left: -1px; opacity: .85; transition: transform .15s; }
    .main[aria-expanded="true"] .chevron { transform: rotate(180deg); }
    .close svg { width: 12px; height: 12px; opacity: .9; }

    .menu { position: absolute; top: calc(100% + 8px); right: 0; box-sizing: border-box; min-width: 248px;
            max-width: min(380px, calc(100vw - 16px)); max-height: min(60vh, 420px); overflow-y: auto; padding: 6px;
            border-radius: 12px; background: #fff; color: #0f172a; font-weight: 400; letter-spacing: 0; transform-origin: top right;
            box-shadow: 0 0 0 1px rgba(15, 23, 42, .08), 0 16px 36px -8px rgba(15, 23, 42, .38), 0 4px 10px -4px rgba(15, 23, 42, .18);
            animation: adm-drop .14s ease-out; }
    .menu[hidden] { display: none; }
    .head { margin-bottom: 4px; padding: 6px 10px 9px; border-bottom: 1px solid #eef2f7; font-size: 12px; font-weight: 600;
            color: #64748b; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .menu button { display: flex; width: 100%; gap: 10px; padding: 8px 10px; border-radius: 8px; line-height: 1.3; }
    .menu button svg { width: 16px; height: 16px; flex: none; color: #94a3b8; }
    .menu button:hover, .menu button:focus-visible { background: #eef4ff; color: #1d4ed8; }
    .menu button:hover svg, .menu button:focus-visible svg { color: #2563eb; }
    .menu button:disabled { cursor: default; background: none; color: #64748b; }
    .menu button:disabled svg { color: #94a3b8; }
    .name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .size { flex: none; padding: 3px 7px; border-radius: 999px; background: #f1f5f9; color: #475569; font-size: 11.5px; font-weight: 600;
            font-variant-numeric: tabular-nums; }
    .menu button:hover .size, .menu button:focus-visible .size { background: #dbe7ff; color: #1d4ed8; }
    .menu .all { position: relative; margin-bottom: 7px; font-weight: 600; color: #1d4ed8; }
    .menu .all svg { color: #2563eb; }
    .menu .all::after { content: ""; position: absolute; left: 10px; right: 10px; bottom: -4px; height: 1px; background: #eef2f7; }
    .spin { flex: none; box-sizing: border-box; width: 14px; height: 14px; margin: 1px; border-radius: 50%; border: 2px solid #cbd5e1;
            border-top-color: #2563eb; animation: adm-spin .7s linear infinite; }

    @keyframes adm-in { from { opacity: 0; transform: translateY(4px) scale(.97); } }
    @keyframes adm-drop { from { opacity: 0; transform: translateY(-4px) scale(.98); } }
    @keyframes adm-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) {
      .bar, .menu { animation: none; }
      .bar .chevron, .bar > button { transition: none; }
    }
    @media (prefers-color-scheme: dark) {
      .menu { background: #1b2230; color: #e2e8f0;
              box-shadow: 0 0 0 1px rgba(255, 255, 255, .09), 0 16px 36px -8px rgba(0, 0, 0, .7), 0 4px 10px -4px rgba(0, 0, 0, .4); }
      .head { border-color: rgba(255, 255, 255, .08); color: #94a3b8; }
      .menu button svg { color: #64748b; }
      .menu button:hover, .menu button:focus-visible { background: rgba(96, 165, 250, .14); color: #bfdbfe; }
      .menu button:hover svg, .menu button:focus-visible svg { color: #93c5fd; }
      .menu button:disabled { color: #94a3b8; }
      .size { background: rgba(255, 255, 255, .07); color: #cbd5e1; }
      .menu button:hover .size, .menu button:focus-visible .size { background: rgba(96, 165, 250, .2); color: #dbeafe; }
      .menu .all, .menu .all svg { color: #93c5fd; }
      .menu .all::after { background: rgba(255, 255, 255, .08); }
      .spin { border-color: #334155; border-top-color: #60a5fa; }
    }`;

  function createPanel() {
    const host = document.createElement("adm-panel");
    host.style.cssText = "all: initial; position: fixed; z-index: 2147483647; top: 0; left: 0; display: none;";
    // Closed, so the page can neither read nor restyle the panel.
    const root = host.attachShadow({ mode: "closed" });
    const style = document.createElement("style");
    style.textContent = PANEL_STYLE;
    const bar = document.createElement("div");
    bar.className = "bar";
    const main = document.createElement("button");
    main.type = "button";
    main.className = "main";
    main.setAttribute("aria-haspopup", "menu");
    main.setAttribute("aria-expanded", "false");
    const close = document.createElement("button");
    close.type = "button";
    close.className = "close";
    close.append(icon(ICONS.close));
    close.setAttribute("aria-label", "Hide the ADM panel for this player");
    close.setAttribute("title", "Hide");
    const menu = document.createElement("div");
    menu.className = "menu";
    menu.setAttribute("role", "menu");
    menu.hidden = true;
    bar.append(main, close, menu);
    root.append(style, bar);
    document.documentElement.append(host);

    main.addEventListener("click", async () => {
      if (!menu.hidden) return setMenuOpen(false);
      const player = panel.target;
      if (!player) return;
      // The page's address now, as a site like YouTube changes it without loading a new page.
      const page = player.page();
      const site = player.fedBySource ? { url: page } : null;
      const caption = player.caption();
      const media = ofThisVideo(panel.media, page, player.duration());
      // What the read ahead found opens the menu straight on its qualities.
      const known = site ? knownAnswer(site.url) : undefined;
      const items = menuItems(media, known ? { ...site, reply: known } : site, caption);
      if (downloadsAtOnce(items, site)) return download(items[0]);
      showMenu(items, caption);
      setMenuOpen(true);
      menu.querySelector("button:not(:disabled)")?.focus();
      if (!site || known) return;
      const reply = await ext.runtime.sendMessage({ cmd: "siteChoices", url: site.url }).catch(() => null);
      // Closed meanwhile, or opened again for another page.
      if (menu.hidden || panel.target !== player || player.page() !== site.url) return;
      showMenu(menuItems(media, { ...site, reply: reply || { ok: false, error: "ADM did not answer." } }, caption), caption);
      menu.querySelector("button:not(:disabled)")?.focus();
    });
    close.addEventListener("click", () => {
      const player = panel.target;
      if (player?.el) dismissed.add(player.el);
      else if (player) dismissedFramePlayers.add(player.id);
      host.style.display = "none";
      panel.target = null;
    });
    // Escape, or a click anywhere else, closes the menu.
    root.addEventListener("keydown", (e) => {
      if (e.key !== "Escape" || menu.hidden) return;
      e.stopPropagation();
      setMenuOpen(false);
      main.focus();
    });
    addEventListener("pointerdown", (e) => !menu.hidden && !e.composedPath().includes(host) && setMenuOpen(false), true);
    return { host, bar, main, menu, target: null, media: [] };
  }

  const PANEL_GAP = 4;

  /** Where the panel goes: just above the player, its right edge on the player's, and never out of the window. */
  function panelSpot(player, size) {
    return { left: Math.max(0, player.right - size.width), top: Math.max(0, player.top - size.height - PANEL_GAP) };
  }

  // Keeps the panel above the player's top-right corner, and out of sight when the player is, or while the page shows
  // something full screen, as YouTube's player: it comes back when the page leaves full screen.
  function place() {
    if (!panel?.target) return;
    const rect = panel.target.rect();
    const visible = !document.fullscreenElement && !!rect && rect.width > 0 && rect.bottom > 0 && rect.top < innerHeight;
    panel.host.style.display = visible ? "block" : "none";
    if (!visible) return;
    const { left, top } = panelSpot(rect, panel.host.getBoundingClientRect());
    panel.host.style.left = `${left}px`;
    panel.host.style.top = `${top}px`;
  }

  /** A player in this frame, as the panel reads it. */
  function localPlayer(el, kind) {
    return {
      el,
      kind,
      fedBySource: el.currentSrc?.startsWith("blob:") === true,
      page: () => location.href.split("#")[0],
      caption: () => captionOf(el),
      duration: () => el.duration,
      rect: () => (el.isConnected ? el.getBoundingClientRect() : null),
    };
  }

  /** Puts the panel on a player, of this frame or of a frame in it. */
  function show(player, media) {
    if (!panel) {
      panel = createPanel();
      keepPlaced();
    }
    panel.target = player;
    panel.media = media;
    const full = (rules.videoPanel ?? "full") !== "mini";
    const label = player.kind === "video" ? "Download this video" : "Download this audio";
    panel.bar.className = full ? "bar" : "bar mini";
    panel.main.replaceChildren(icon(ICONS.download), ...(full ? [textNode("span", label, "label"), icon(ICONS.chevron, "chevron")] : []));
    panel.main.setAttribute("aria-label", `${label} with ADM`);
    setMenuOpen(false);
    place();
    readAheadShown();
  }

  async function consider(el) {
    const kind = kindOf(el);
    if (!kind || dismissed.has(el) || !panelAllowed(rules, location.href, el.getBoundingClientRect(), kind)) return;
    const reply = await ext.runtime.sendMessage({ cmd: "panelMedia" }).catch(() => null);
    const media = reply?.media || [];
    if (!panelWanted(media, el, location.href)) return;
    if (!inTop && (await handUp(el, kind))) {
      // The top frame's panel shows it now.
      if (panel) {
        panel.target = null;
        panel.host.style.display = "none";
      }
      return;
    }
    show(localPlayer(el, kind), media);
  }

  // --- A player in a frame ---
  //
  // An embedded player fills its frame, and nothing a frame draws shows outside it, so the frame's own panel could only
  // sit over the video. A frame below the top one hands its player to the top frame's panel instead, which sits above
  // the frame. Where the player is goes up by posts from frame to frame: each frame finds the frame element a post came
  // from, places the player in its own page, and passes it on. What the player is goes through the background, which a
  // page can't speak for. A frame whose player the top frame can't place keeps its own panel.

  const RECT_POST = "adm-player-rect";
  const LOCATE_MS = 1000;
  const MAX_FRAME_PLAYERS = 16;

  /** Where a frame's page shows in this window: inside the frame's border and padding. */
  function contentBox(frame) {
    const r = frame.getBoundingClientRect();
    const s = getComputedStyle(frame);
    const px = (value) => parseFloat(value) || 0;
    return {
      left: r.left + frame.clientLeft + px(s.paddingLeft),
      top: r.top + frame.clientTop + px(s.paddingTop),
      width: frame.clientWidth - px(s.paddingLeft) - px(s.paddingRight),
      height: frame.clientHeight - px(s.paddingTop) - px(s.paddingBottom),
    };
  }

  /** A rectangle in a frame's page, placed in the window holding the frame and cut to what the frame shows; null when it shows none of it. */
  function inFrame(box, rect) {
    const left = box.left + Math.max(0, rect.left);
    const top = box.top + Math.max(0, rect.top);
    const right = box.left + Math.min(box.width, rect.right);
    const bottom = box.top + Math.min(box.height, rect.bottom);
    return right > left && bottom > top ? { left, top, right, bottom, width: right - left, height: bottom - top } : null;
  }

  const frameOf = (source) => (source ? [...document.querySelectorAll("iframe, frame")].find((f) => f.contentWindow === source) : undefined);

  /** Keeps at most a few entries, dropping the oldest that isn't the one in use. */
  function keepFew(map, inUse) {
    if (map.size < MAX_FRAME_PLAYERS) return;
    for (const [key, value] of map) if (value !== inUse) return void map.delete(key);
  }

  // Each player's rectangle as last posted, so an unchanged one isn't posted again.
  const posted = new Map();

  function postUp(id, rect) {
    const r = rect && { left: Math.round(rect.left), top: Math.round(rect.top), right: Math.round(rect.right), bottom: Math.round(rect.bottom) };
    const text = JSON.stringify(r);
    if (posted.get(id) === text) return;
    posted.set(id, text);
    window.parent.postMessage({ adm: RECT_POST, id, rect: r }, "*");
  }

  // --- In a frame below the top one ---

  const playerIds = new WeakMap();
  let handedUp = null; // { el, id, url }: this frame's player, shown by the top frame's panel
  const passing = new Map(); // id -> { frame, rect }: a player in a frame below this one, passed on to the frame above

  const postOwn = () => handedUp && postUp(handedUp.id, handedUp.el.isConnected ? handedUp.el.getBoundingClientRect() : null);

  function passOn() {
    for (const [id, p] of passing) postUp(id, p.frame.isConnected && p.rect ? inFrame(contentBox(p.frame), p.rect) : null);
  }

  /** Hands this frame's player to the top frame's panel; false when the top frame doesn't take it. */
  async function handUp(el, kind) {
    if (!playerIds.has(el)) playerIds.set(el, Math.random().toString(36).slice(2));
    const id = playerIds.get(el);
    handedUp = { el, id, url: location.href.split("#")[0] };
    posted.delete(id);
    keepPlaced();
    postOwn();
    const player = { id, kind, fedBySource: el.currentSrc?.startsWith("blob:") === true, caption: captionOf(el), duration: el.duration };
    const reply = await ext.runtime.sendMessage({ cmd: "framePlayer", player }).catch(() => null);
    if (reply?.ok) return true;
    if (handedUp?.id === id) handedUp = null;
    return false;
  }

  // A frame that leaves its page takes its players' panel with it.
  addEventListener("pagehide", () => {
    if (handedUp) postUp(handedUp.id, null);
    for (const id of passing.keys()) postUp(id, null);
  });

  // --- In the top frame ---

  const framePlayers = new Map(); // id -> a player in a frame below, as the top frame's panel reads it
  const dismissedFramePlayers = new Set();

  function framePlayerOf(id) {
    let player = framePlayers.get(id);
    if (player) return player;
    keepFew(framePlayers, panel?.target);
    player = { id, kind: "video", fedBySource: false, info: {}, frame: null, inPage: null, located: null };
    Object.assign(player, {
      page: () => player.info.url,
      caption: () => player.info.caption || "",
      duration: () => player.info.duration,
      rect: () => (player.frame?.isConnected && player.inPage ? inFrame(contentBox(player.frame), player.inPage) : null),
    });
    framePlayers.set(id, player);
    return player;
  }

  addEventListener("message", (e) => {
    const data = e.data;
    if (data?.adm !== RECT_POST || typeof data.id !== "string") return;
    const frame = frameOf(e.source);
    if (!frame) return;
    const r = data.rect;
    const rect = r && [r.left, r.top, r.right, r.bottom].every(Number.isFinite) ? { left: r.left, top: r.top, right: r.right, bottom: r.bottom } : null;
    if (!inTop) {
      if (!passing.has(data.id)) keepFew(passing);
      passing.set(data.id, { frame, rect });
      keepPlaced();
      return passOn();
    }
    const player = framePlayerOf(data.id);
    Object.assign(player, { frame, inPage: rect });
    player.located?.();
    if (panel?.target === player) place();
  });

  /**
   * The top frame takes a frame's player for its panel once the frame's posts have placed it; { ok: false } when none
   * has, and the frame shows its own panel. One the user closed, or on a page whose panel is off, is taken and not shown.
   */
  async function framePlayer(info, wait = LOCATE_MS) {
    if (!inTop || typeof info?.id !== "string") return { ok: false };
    const player = framePlayerOf(info.id);
    if (!player.frame) await new Promise((resolve) => ((player.located = resolve), setTimeout(resolve, wait)));
    player.located = null;
    if (!player.frame) return { ok: false };
    Object.assign(player, { info, kind: info.kind === "audio" ? "audio" : "video", fedBySource: info.fedBySource === true });
    if (dismissedFramePlayers.has(player.id) || !panelOn(rules, location.href)) return { ok: true };
    const reply = await ext.runtime.sendMessage({ cmd: "panelMedia" }).catch(() => null);
    show(player, reply?.media || []);
    return { ok: true };
  }

  // --- Keeping up ---

  let placing = false;

  /** Keeps the panel on its player, and this frame's players placed for the frames above, as pages scroll and change. */
  function keepPlaced() {
    if (placing) return;
    placing = true;
    const update = () => {
      place();
      postOwn();
      passOn();
    };
    addEventListener("scroll", update, true);
    addEventListener("resize", update);
    addEventListener("fullscreenchange", update, true);
    setInterval(() => {
      update();
      readAheadShown();
      // A site that moves to another video without loading a page: the top frame learns the player's new address.
      const page = location.href.split("#")[0];
      if (handedUp && handedUp.url !== page) {
        handedUp.url = page;
        consider(handedUp.el);
      }
    }, 500);
  }

  // A player that starts or loads media is a candidate; so is the largest one when the browser
  // reports media for this tab.
  for (const type of ["play", "loadedmetadata"]) addEventListener(type, (e) => consider(e.target), true);

  function largestPlayer() {
    let best = null;
    let bestArea = 0;
    for (const el of document.querySelectorAll("video, audio")) {
      const r = el.getBoundingClientRect();
      if (r.width * r.height > bestArea) [best, bestArea] = [el, r.width * r.height];
    }
    return best;
  }

  // Reachable from the tests; the page cannot see a content script's globals.
  globalThis.admContent = {
    panelAllowed, choices, panelWanted, menuItems, downloadsAtOnce, captionOf, ofThisVideo, readAhead, knownAnswer, panelSpot,
    inFrame, framePlayer, labelParts,
  };

  ext.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message.cmd === "mediaFound") {
      const player = largestPlayer();
      if (player) consider(player);
      return;
    }
    if (message.cmd === "framePlayer") {
      framePlayer(message.player).then(sendResponse);
      return true;
    }
    if (message.cmd !== "collectLinks") return;
    sendResponse(collectLinks(message.selection));
  });
})();
