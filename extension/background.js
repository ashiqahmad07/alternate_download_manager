// ADM browser integration: hands downloads to the desktop app through the
// com.adm.host native-messaging host and lists media streams seen on each tab.
// The same code runs as Chrome's and Edge's service worker and as Firefox's event page.

const ext = globalThis.browser ?? chrome;
const HOST = "com.adm.host";
const DEFAULTS = { enabled: true };
const MIN_MEDIA_BYTES = 300 * 1024;
const MAX_MEDIA_PER_TAB = 50;
// A click reported by content.js applies to a download that starts within this time.
const KEY_WINDOW_MS = 10 * 1000;
// "Download all links" stays well under the native host's 8 MB message limit.
const MAX_LINKS = 5000;
const MAX_LINKS_BYTES = 6 * 1024 * 1024;
// A DASH manifest longer than this isn't sent to the native host, and is offered without a quality list.
const MAX_MANIFEST_BYTES = 6 * 1024 * 1024;

// Capture rules come from ADM's Options (File types). These built-in copies of its
// defaults apply only until ADM has answered once.
const DEFAULT_RULES = {
  extensions: (
    "zip rar 7z gz gzip tgz tar bz2 xz z lzh arj ace sit sitx sea r0* r1* iso img bin " +
    "exe msi msu msix apk dmg deb rpm pdf epub ppt pps plj tif tiff " +
    "mp3 aac m4a wav wma ogg opus flac aif ra mp4 m4v mkv avi mov wmv asf flv webm 3gp mpg mpeg mpe mpa ogv qt rm rmvb torrent"
  ).split(" "),
  exceptionSites: "*.update.microsoft.com download.windowsupdate.com *.download.windowsupdate.com",
  exceptionAddresses: "",
  minSizeKB: 0,
  preventKeys: ["alt"],
  forceKeys: ["insert"],
  forceOnLinkClick: true,
  menuDownloadLink: true,
  menuDownloadAllLinks: true,
  videoPanel: "full",
  videoPanelMinSizeKB: 300,
  videoPanelExceptionSites: "",
};
const RULES_MAX_AGE_MS = 5 * 60 * 1000;

function settings() {
  return ext.storage.local.get(DEFAULTS);
}

async function refreshRules() {
  const reply = await sendToHost({ type: "config" });
  if (!reply.ok || !reply.config) return;
  const { rules: old } = await ext.storage.local.get("rules");
  await ext.storage.local.set({ rules: reply.config, rulesFetchedAt: Date.now() });
  if (!old || old.menuDownloadLink !== reply.config.menuDownloadLink || old.menuDownloadAllLinks !== reply.config.menuDownloadAllLinks)
    await rebuildMenus(reply.config);
}

// Uses the cached rules straight away and refreshes stale ones in the background, so a
// capture decision never waits on ADM unless there is nothing cached yet.
async function rules() {
  const { rules: cached, rulesFetchedAt = 0 } = await ext.storage.local.get(["rules", "rulesFetchedAt"]);
  if (!cached) {
    await refreshRules();
    return (await ext.storage.local.get("rules")).rules || DEFAULT_RULES;
  }
  if (Date.now() - rulesFetchedAt > RULES_MAX_AGE_MS) refreshRules();
  return cached;
}

const wildcard = (pattern, text) =>
  new RegExp("^" + pattern.replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*") + "$", "i").test(text);

// Same matching as ADM's SiteList: "*.example.com" matches the host, a pattern with "/" the whole URL.
function siteListMatches(list, url) {
  const host = new URL(url).hostname;
  return (list || "").split(/\s+/).filter(Boolean).some((p) => wildcard(p, p.includes("/") ? url : host));
}

function isWeb(url) {
  try {
    const { protocol } = new URL(url);
    return protocol === "http:" || protocol === "https:";
  } catch {
    return false;
  }
}

// Mirrors CaptureRules.ShouldCapture in ADM.Core; size is -1 when unknown.
function shouldCapture(r, url, name, size) {
  if (!isWeb(url)) return false;
  let path = new URL(url).pathname;
  try {
    path = decodeURIComponent(path);
  } catch {}
  const ext = extensionOf(name || path);
  if (!ext || !r.extensions.some((p) => wildcard(p, ext))) return false;
  if (siteListMatches(r.exceptionSites, url) || siteListMatches(r.exceptionAddresses, url)) return false;
  return size < 0 || size >= r.minSizeKB * 1024;
}

const allHeld = (combo, held) => !!combo && combo.length > 0 && combo.every((k) => held.includes(k));

// Mirrors CaptureRules.Decide: the prevent keys leave the download to the browser and win over
// the force keys, which take any http or https link.
function decide(r, url, name, size, keys) {
  if (!isWeb(url)) return false;
  if (allHeld(r.preventKeys, keys)) return false;
  if (allHeld(r.forceKeys, keys)) return true;
  return shouldCapture(r, url, name, size);
}

async function sendToHost(message) {
  try {
    return (await ext.runtime.sendNativeMessage(HOST, message)) || { ok: false, error: "No reply from ADM" };
  } catch (e) {
    return { ok: false, error: e?.message || String(e) };
  }
}

async function cookieHeader(url) {
  try {
    const cookies = await ext.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

// --- The browser's proxy -----------------------------------------------------------

// ADM falls back on this when a download gets no answer, or a 403, on its own route. Each string is one
// BrowserProxy.Parse in ADM.Core understands; tests/fixtures/browser-proxy.json checks both. "" says nothing to try.
const PROXY_PORTS = { http: 80, https: 443, socks4: 1080, socks5: 1080 };

function proxyServer({ scheme = "http", host, port } = {}) {
  return host && scheme in PROXY_PORTS ? `${scheme}://${host}:${port || PROXY_PORTS[scheme]}` : "";
}

// Chrome's bypass entries: [scheme://]host[:port], with wildcards, or <local> for names without a dot.
function proxyBypassed(list, url) {
  const host = new URL(url).hostname;
  return (list || []).some((entry) =>
    entry === "<local>" ? !host.includes(".") : wildcard(entry.replace(/^[a-z]+:\/\//i, "").replace(/:\d+$/, ""), host)
  );
}

// Chrome's proxy.settings value, for one address.
function browserProxyFor(config, url) {
  if (config?.mode === "pac_script") {
    const script = config.pacScript?.url || "";
    return /^https?:\/\/[^/@]+(\/|$)/i.test(script) ? `pac:${script}` : "";
  }
  if (config?.mode !== "fixed_servers") return "";
  const rules = config.rules || {};
  if (proxyBypassed(rules.bypassList, url)) return "";
  const forScheme = { "http:": rules.proxyForHttp, "https:": rules.proxyForHttps }[new URL(url).protocol];
  return proxyServer(rules.singleProxy || forScheme || rules.fallbackProxy);
}

// Firefox's description of the proxy a request went through; there, "socks" is SOCKS5.
function proxyInfoString(info) {
  const scheme = info?.type === "socks" ? "socks5" : info?.type;
  return info?.host ? proxyServer({ scheme, host: info.host, port: info.port }) : "";
}

// Firefox only: address -> the proxy its request went through. Kept in memory, as a download follows within seconds.
const requestProxies = new Map();

function rememberRequestProxy(url, info) {
  requestProxies.delete(url);
  requestProxies.set(url, info);
  if (requestProxies.size > 50) requestProxies.delete(requestProxies.keys().next().value);
}

async function browserProxy(url) {
  if (requestProxies.has(url)) return proxyInfoString(requestProxies.get(url));
  try {
    return ext.proxy?.settings ? browserProxyFor((await ext.proxy.settings.get({})).value, url) : "";
  } catch {
    return "";
  }
}

// A site's page goes without cookies: yt-dlp is never given them, and the site's files are elsewhere.
async function handOff({ url, referrer = "", fileName = "", kind = "http", mode = "prompt", captured = false, hlsBandwidth = null, audioOnly = false, siteChoice = null, audioUrl = null }) {
  return sendToHost({
    type: "download",
    url,
    referrer,
    cookies: kind === "site" ? "" : await cookieHeader(url),
    userAgent: navigator.userAgent,
    browserProxy: await browserProxy(url),
    fileName,
    kind,
    mode,
    captured,
    hlsBandwidth,
    audioOnly,
    siteChoice,
    audioUrl,
  });
}

// --- HLS master playlists ---------------------------------------------------------

const VIDEO_CODECS = ["avc1", "avc3", "hvc1", "hev1", "dvh1", "dvhe", "vp09", "vp8", "av01"];

function hlsAttributes(line) {
  const attributes = {};
  for (const [, name, value] of line.slice(line.indexOf(":") + 1).matchAll(/([A-Z0-9-]+)=("[^"]*"|[^,]*)/g))
    if (!(name in attributes)) attributes[name] = value.replace(/^"|"$/g, "");
  return attributes;
}

// Mirrors HlsMaster.Parse in ADM.Core; tests/fixtures/master.m3u8 checks both.
function parseMaster(text, base) {
  const lines = text.split("\n").map((l) => l.trim());
  const variants = [];
  const audio = [];
  const subtitles = [];
  lines.forEach((line, i) => {
    if (line.startsWith("#EXT-X-MEDIA:")) {
      const a = hlsAttributes(line);
      const rendition = {
        groupId: a["GROUP-ID"] || "",
        name: a.NAME || "",
        language: a.LANGUAGE ?? null,
        isDefault: a.DEFAULT === "YES",
        uri: a.URI ? new URL(a.URI, base).href : null,
      };
      if (a.TYPE === "AUDIO") audio.push(rendition);
      else if (a.TYPE === "SUBTITLES") subtitles.push(rendition);
    } else if (line.startsWith("#EXT-X-STREAM-INF:")) {
      const next = lines.slice(i + 1).find((l) => l && !l.startsWith("#"));
      if (!next) return;
      const a = hlsAttributes(line);
      const [width = 0, height = 0] = (a.RESOLUTION || "").split("x").map((n) => Number(n) || 0);
      const bandwidth = Number(a.BANDWIDTH) || 0;
      const codecs = a.CODECS || "";
      const audioOnly = codecs.length > 0 && !codecs.split(",").some((c) => VIDEO_CODECS.some((v) => c.trim().toLowerCase().startsWith(v)));
      variants.push({
        uri: new URL(next, base).href,
        bandwidth,
        width,
        height,
        codecs,
        audioGroup: a.AUDIO ?? null,
        subtitleGroup: a.SUBTITLES ?? null,
        audioOnly,
        label: height ? `${height}p` : audioOnly ? "Audio" : `${Math.floor(bandwidth / 1000)} kbps`,
      });
    }
  });
  return { variants, audio, subtitles };
}

// Playlist or manifest address -> the choices it offers, so the panel's menu opens without fetching again.
const masterChoices = new Map();
const NO_CHOICES = { qualities: [], audioOnly: false };

// ADM.Core reads a DASH manifest, through the native host, which answers without starting ADM. So the panel and
// the download read it alike. A manifest ADM refuses, such as one with DRM, is offered with no choices, and the
// download says why.
async function dashChoices(url, text) {
  const message = { type: "dashChoices", url, text };
  if (JSON.stringify(message).length > MAX_MANIFEST_BYTES) return NO_CHOICES;
  const reply = await sendToHost(message);
  return reply.ok && Array.isArray(reply.qualities) ? { qualities: reply.qualities, audioOnly: reply.audioOnly === true } : NO_CHOICES;
}

async function choicesFor(url, kind) {
  if (masterChoices.has(url)) return masterChoices.get(url);
  let choices = NO_CHOICES;
  try {
    const text = await (await fetch(url, { credentials: "include" })).text();
    if (kind === "dash") {
      choices = await dashChoices(url, text);
    } else if (text.includes("#EXT-X-STREAM-INF")) {
      const master = parseMaster(text, url);
      choices = {
        qualities: master.variants
          .filter((v) => !v.audioOnly)
          .sort((a, b) => b.bandwidth - a.bandwidth)
          .map((v) => ({ label: v.label, bandwidth: v.bandwidth })),
        audioOnly: master.audio.some((a) => a.uri) || master.variants.some((v) => v.audioOnly),
      };
    }
  } catch {
    // Offered without a quality list; ADM picks the best.
  }
  masterChoices.set(url, choices);
  if (masterChoices.size > 50) masterChoices.delete(masterChoices.keys().next().value);
  return choices;
}

const isStream = (media) => media.kind === "hls" || media.kind === "dash";

// The tab's media for the download panel: each video whose pieces were seen, with its sound; files above the minimum
// size; and HLS and DASH streams with their qualities. Once an HLS master playlist is known, the quality playlists it
// lists are not offered again.
async function panelMedia(tabId, r) {
  const key = mediaKey(tabId);
  const minBytes = (r.videoPanelMinSizeKB ?? 300) * 1024;
  const seen = (await ext.storage.session.get(key))[key] || [];
  const media = seen.filter((m) => m.kind !== "piece" && (isStream(m) || m.size < 0 || m.size >= minBytes));
  const withChoices = await Promise.all(media.map(async (m) => (isStream(m) ? { ...m, ...(await choicesFor(m.url, m.kind)) } : m)));
  const hasMaster = withChoices.some((m) => m.kind === "hls" && m.qualities.length);
  const streams = hasMaster ? withChoices.filter((m) => m.kind !== "hls" || m.qualities.length) : withChoices;
  return [...pairedPieces(seen.filter((m) => m.kind === "piece")), ...streams];
}

// --- Files fetched a piece at a time --------------------------------------------------

// A player that fetches a file a piece at a time with the range in the address (bytestart and byteend, as Facebook's
// does) makes each piece look like a small file. Without the two, the address gives the whole file.
function wholeFileOf(url) {
  const address = new URL(url);
  if (!address.searchParams.has("bytestart") || !address.searchParams.has("byteend")) return null;
  address.searchParams.delete("bytestart");
  address.searchParams.delete("byteend");
  return address.href;
}

// Facebook's efg parameter is base64 JSON naming the video, its bitrate, and a tag that says "audio" for the sound. The
// video's id, the one a reel's address carries, is read as text: as a number, one past 2^53 would lose its last digits.
function efgOf(url) {
  try {
    const value = new URL(url).searchParams.get("efg");
    if (!value) return null;
    const text = atob(value);
    const id = /"video_id"\s*:\s*"?(\d+)/.exec(text);
    return { ...JSON.parse(text), video_id: id ? id[1] : null };
  } catch {
    return null;
  }
}

// One entry per file, whatever piece of it was seen first: a file's address changes from piece to piece in more than its range.
function pieceEntry(url, type) {
  const efg = efgOf(url);
  const path = new URL(url).pathname;
  const audio = efg ? /audio/i.test(efg.vencode_tag || "") : type.startsWith("audio/");
  const videoId = efg?.video_id ?? null;
  const group = videoId ?? path;
  const durationS = Number(efg?.duration_s) || 0;
  return { url, type, size: -1, kind: "piece", group, videoId, durationS, audio, bitrate: Number(efg?.bitrate) || 0, key: `${group}|${audio ? "a" : "v"}|${path}` };
}

// Each video file with its sound, best bitrate first, for ADM to fetch both and join; a video with no sound is offered
// as its whole file. Each names its video and its length where the file did, as a page loads videos besides the one it
// shows.
function pairedPieces(pieces) {
  const offered = [];
  for (const group of new Set(pieces.map((p) => p.group))) {
    const files = pieces.filter((p) => p.group === group);
    const sound = files.filter((p) => p.audio).sort((a, b) => b.bitrate - a.bitrate)[0];
    for (const video of files.filter((p) => !p.audio).sort((a, b) => b.bitrate - a.bitrate)) {
      const which = { videoId: video.videoId ?? null, durationS: video.durationS || 0 };
      if (!sound) {
        offered.push({ url: video.url, type: video.type, size: -1, kind: "http", ...which });
        continue;
      }
      const label = video.bitrate ? `Video, ${Math.round(video.bitrate / 1000)} kbps, with sound` : "Video with sound";
      offered.push({ url: video.url, audioUrl: sound.url, type: video.type, size: -1, kind: "joined", label, ...which });
    }
  }
  return offered;
}

// --- A site's video, read by yt-dlp through the native host ------------------------------

// Page address -> ADM's answer and when it came, so opening the menu again doesn't run yt-dlp again.
const siteAnswers = new Map();
const SITE_ANSWER_MS = 30 * 60 * 1000;
// Page address -> ADM's answer on its way: the panel reads ahead as it shows, and a click meanwhile waits for that.
const siteReads = new Map();

function siteChoices(url, now = Date.now()) {
  const kept = siteAnswers.get(url);
  if (kept && now - kept.at < SITE_ANSWER_MS) return Promise.resolve(kept.reply);
  if (siteReads.has(url)) return siteReads.get(url);
  const read = sendToHost({ type: "videoChoices", url })
    .then((reply) => {
      // Only a page's choices are kept: a missing tool or an ADM that didn't answer may be put right in a minute.
      if (reply.ok) {
        siteAnswers.set(url, { at: now, reply });
        if (siteAnswers.size > 50) siteAnswers.delete(siteAnswers.keys().next().value);
      }
      return reply;
    })
    .finally(() => siteReads.delete(url));
  siteReads.set(url, read);
  return read;
}

// --- Reading a video's page as soon as a tab opens it ----------------------------------

// Addresses that name one video, on sites yt-dlp reads without a login. A tab that opens one has its page read at once,
// before its player has loaded, and while the tab waits behind another: the panel's menu then opens on the video's
// qualities instead of "Finding qualities…". Any other page is read when the panel shows on its player.
const VIDEO_PAGES = [
  /^https:\/\/(www\.|m\.|music\.)?youtube\.com\/(watch\/?\?([^#]*&)?v=|shorts\/|live\/)[\w-]/i,
  /^https:\/\/(www\.|m\.|web\.)?facebook\.com\/(reel\/\d|watch\/?\?([^#]*&)?v=\d|([^?#]*\/)?videos\/([^?#]*\/)?\d+\/?(\?|$))/i,
  /^https:\/\/(www\.)?vimeo\.com\/\d+\/?(\?|$)/i,
  /^https:\/\/(www\.)?dailymotion\.com\/video\/\w/i,
  /^https:\/\/(www\.)?tiktok\.com\/@[^/?#]+\/video\/\d/i,
];
// yt-dlp is a heavy program to start, so a browser that restores twenty such tabs reads two at a time.
const EARLY_READS = 2;
let earlyReading = 0;
// Tab -> the video page it shows, still to be read. The tab put here last is read first.
const earlyWaiting = new Map();
// Tab -> the page it moved to last.
const tabPages = new Map();

const isVideoPage = (url) => VIDEO_PAGES.some((page) => page.test(url));

/** Reads the page a tab has moved to, if it names one video and the panel is on there; a page left meanwhile isn't read. */
async function readEarly(tabId, url) {
  tabPages.set(tabId, url);
  earlyWaiting.delete(tabId);
  if (!isVideoPage(url)) return;
  const r = await rules();
  if (tabPages.get(tabId) !== url || (r.videoPanel ?? "full") === "off" || siteListMatches(r.videoPanelExceptionSites, url)) return;
  earlyWaiting.set(tabId, url);
  nextEarlyRead();
}

function nextEarlyRead() {
  while (earlyReading < EARLY_READS && earlyWaiting.size > 0) {
    const [tabId, url] = [...earlyWaiting].at(-1);
    earlyWaiting.delete(tabId);
    earlyReading++;
    siteChoices(url).finally(() => {
      earlyReading--;
      nextEarlyRead();
    });
  }
}

/** The tab brought to the front is read next. */
function readFrontFirst(tabId) {
  const url = earlyWaiting.get(tabId);
  if (url === undefined) return;
  earlyWaiting.delete(tabId);
  earlyWaiting.set(tabId, url);
}

const fileStem = (title) => (title || "video").replace(/[\\/:*?"<>|]+/g, " ").trim().slice(0, 120) || "video";

const extensionOf = (name) => (/\.([a-z0-9]{1,8})$/i.exec(name || "") || [, ""])[1].toLowerCase();
const baseName = (path) => (path || "").split(/[\\/]/).pop();

// An HLS playlist or a DASH manifest, by its type, else by its address; null for anything else. The address
// rule mirrors Links.StreamKind in ADM.Core, and tests/fixtures/stream-kinds.json checks both.
function streamKind(url, type = "") {
  if (/mpegurl/i.test(type)) return "hls";
  if (/dash\+xml/i.test(type)) return "dash";
  const match = /\.(m3u8|mpd)(?=[?#]|$)/i.exec(url);
  return match ? (match[1].toLowerCase() === "mpd" ? "dash" : "hls") : null;
}

// --- Special keys -----------------------------------------------------------------

// Link address -> the keys content.js saw held when it was clicked. Kept in memory: a click
// message wakes the background, and the download it starts follows within seconds.
const clicks = new Map();

function rememberClick(url, keys, now = Date.now()) {
  clicks.delete(url);
  clicks.set(url, { keys, at: now });
  if (clicks.size > 50) clicks.delete(clicks.keys().next().value);
}

function heldKeysFor(url, now = Date.now()) {
  const click = clicks.get(url);
  return click && now - click.at <= KEY_WINDOW_MS ? click.keys : [];
}

// --- Download capture -------------------------------------------------------------

// True when ADM took the download over, after which the browser's copy is dropped. If
// ADM is unreachable the browser download simply continues.
async function takeOver(item) {
  const s = await settings();
  if (!s.enabled) return false;
  const url = item.finalUrl || item.url;
  const name = baseName(item.filename);
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : -1;
  const keys = heldKeysFor(item.url).length ? heldKeysFor(item.url) : heldKeysFor(url);
  if (!decide(await rules(), url, name, size, keys)) return false;

  const reply = await handOff({ url, referrer: item.referrer, fileName: name, captured: true });
  if (!reply.ok) return false;
  await ext.downloads.cancel(item.id).catch(() => {});
  await ext.downloads.erase({ id: item.id }).catch(() => {});
  return true;
}

if (ext.downloads.onDeterminingFilename) {
  ext.downloads.onDeterminingFilename.addListener((item, suggest) => {
    takeOver(item)
      .catch(() => false)
      .finally(() => {
        try {
          suggest();
        } catch {
          // Already cancelled.
        }
      });
    return true; // suggest() is called asynchronously
  });
} else {
  // Firefox has no onDeterminingFilename: the download has already started when it is seen,
  // so it is cancelled only once ADM has accepted it.
  ext.downloads.onCreated.addListener((item) => takeOver(item).catch(() => false));
}

// --- Context menu -----------------------------------------------------------------

let menus = Promise.resolve();

// Options in ADM choose the items; rebuilds run one after another so no id is created twice.
function rebuildMenus(r) {
  menus = menus
    .then(async () => {
      await ext.contextMenus.removeAll();
      if (r.menuDownloadLink !== false)
        ext.contextMenus.create({ id: "adm-download", title: "Download with ADM", contexts: ["link", "video", "audio", "image"] });
      if (r.menuDownloadAllLinks !== false)
        ext.contextMenus.create({
          id: "adm-all-links",
          title: "Download all links with ADM",
          contexts: ["page", "frame", "selection", "link"],
        });
    })
    .catch(() => {});
  return menus;
}

ext.runtime.onInstalled.addListener(async () => rebuildMenus(await rules()));
ext.runtime.onStartup.addListener(async () => rebuildMenus(await rules()));

// The page's links with one cookie header per origin, trimmed to what the native host accepts.
async function linksMessage(links, referrer) {
  const kept = [];
  let bytes = 0;
  for (const link of links) {
    bytes += JSON.stringify(link).length;
    if (kept.length >= MAX_LINKS || bytes > MAX_LINKS_BYTES) break;
    kept.push(link);
  }
  const cookies = {};
  const proxies = {};
  for (const origin of new Set(kept.map((l) => new URL(l.url).origin))) {
    const header = await cookieHeader(origin + "/");
    if (header) cookies[origin] = header;
    const proxy = await browserProxy(origin + "/");
    if (proxy) proxies[origin] = proxy;
  }
  return { type: "links", links: kept, referrer, cookies, proxies, userAgent: navigator.userAgent };
}

async function collectLinks(tabId, frameId, selection) {
  const ask = () => ext.tabs.sendMessage(tabId, { cmd: "collectLinks", selection }, { frameId });
  try {
    return await ask();
  } catch {
    // Pages open since before the extension was installed have no content script yet.
    await ext.scripting.executeScript({ target: { tabId, frameIds: [frameId] }, files: ["content.js"] });
    return await ask();
  }
}

async function downloadAllLinks(info, tab) {
  const links = await collectLinks(tab.id, info.frameId ?? 0, !!info.selectionText).catch(() => []);
  if (!links?.length) return { ok: false, error: "No links found on this page." };
  return sendToHost(await linksMessage(links, info.frameUrl || info.pageUrl || tab.url));
}

ext.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === "adm-all-links") return downloadAllLinks(info, tab);
  const url = info.linkUrl || info.srcUrl;
  if (!url || !isWeb(url)) return;
  return handOff({ url, referrer: info.pageUrl, kind: streamKind(url) || "http" });
});

// --- Media detection --------------------------------------------------------------

const mediaKey = (tabId) => `media:${tabId}`;

async function rememberMedia(tabId, entry) {
  const key = mediaKey(tabId);
  const list = (await ext.storage.session.get(key))[key] || [];
  if (list.some((m) => (m.key ?? m.url) === (entry.key ?? entry.url))) return;
  list.push(entry);
  if (list.length > MAX_MEDIA_PER_TAB) list.shift();
  await ext.storage.session.set({ [key]: list });
  ext.action.setBadgeBackgroundColor({ tabId, color: "#2563EB" }).catch(() => {});
  ext.action.setBadgeText({ tabId, text: String(list.length) }).catch(() => {});
  // The page's players can now offer the download panel.
  ext.tabs.sendMessage(tabId, { cmd: "mediaFound" }).catch(() => {});
}

async function forgetMedia(tabId) {
  await ext.storage.session.remove(mediaKey(tabId));
  ext.action.setBadgeText({ tabId, text: "" }).catch(() => {});
}

ext.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.proxyInfo) rememberRequestProxy(details.url, details.proxyInfo);
    if (details.tabId < 0) return;
    const headers = {};
    for (const h of details.responseHeaders || []) headers[h.name.toLowerCase()] = h.value || "";
    const type = (headers["content-type"] || "").split(";")[0].trim().toLowerCase();
    const url = details.url;

    // A piece of a file is recorded as the whole file, never as the piece, whatever its size.
    const whole = /^(video|audio)\//.test(type) ? wholeFileOf(url) : null;
    if (whole) return rememberMedia(details.tabId, pieceEntry(whole, type));

    const kind = streamKind(url, type);
    if (!kind && !/^(video|audio)\//.test(type)) return;
    // Individual HLS/DASH fragments are not useful on their own.
    if (!kind && (type === "video/mp2t" || /\.(ts|m4s)(\?|$)/i.test(url))) return;

    const range = /\/(\d+)\s*$/.exec(headers["content-range"] || "");
    const size = range ? Number(range[1]) : headers["content-length"] ? Number(headers["content-length"]) : -1;
    if (!kind && size >= 0 && size < MIN_MEDIA_BYTES) return;

    rememberMedia(details.tabId, { url, type, size, kind: kind || "http" });
  },
  { urls: ["<all_urls>"] },
  ["responseHeaders"]
);

ext.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading" && change.url) forgetMedia(tabId);
  // Also when a site like YouTube moves to another video without loading a page. As the panel does, without the fragment.
  if (change.url) return readEarly(tabId, change.url.split("#")[0]);
});
ext.tabs.onActivated.addListener(({ tabId }) => readFrontFirst(tabId));
ext.tabs.onRemoved.addListener((tabId) => {
  earlyWaiting.delete(tabId);
  tabPages.delete(tabId);
  return forgetMedia(tabId);
});

// --- Popup and content-script messages --------------------------------------------

// The extensions a file the page played may be saved under. The page picks its address and its type, and Download all
// saves it without a dialog showing the name, so nothing else is taken from either: not .exe, nor .php.
const MEDIA_EXTENSIONS = new Set([
  "mp4", "m4v", "m4a", "webm", "weba", "mkv", "mka", "mov", "avi", "flv", "wmv", "wma", "3gp", "mpg", "mpeg",
  "ogg", "ogv", "oga", "opus", "mp3", "aac", "wav", "flac", "ts",
]);
// A type's name for its files where it isn't the type's own.
const TYPE_EXTENSIONS = {
  "audio/mpeg": "mp3", "video/quicktime": "mov", "video/x-matroska": "mkv", "audio/x-matroska": "mka", "video/x-flv": "flv",
  "video/x-msvideo": "avi", "video/x-ms-wmv": "wmv", "audio/x-ms-wma": "wma", "video/mp2t": "ts", "video/3gpp": "3gp",
};

/** A video or sound file's extension by its type; a type that names none is an MP4's, or for sound an MP3's. */
function typeExtension(type = "") {
  const ext = TYPE_EXTENSIONS[type] ?? type.split("/")[1] ?? "";
  return MEDIA_EXTENSIONS.has(ext) ? ext : type.startsWith("audio/") ? "mp3" : "mp4";
}

function mediaFileName(title, media) {
  const fromUrl = baseName(new URL(media.url).pathname);
  let ext = extensionOf(fromUrl);
  if (media.kind === "hls") ext = "ts";
  // ADM changes it to what the stream's container calls for, such as WebM.
  else if (media.kind === "dash") ext = "mp4";
  else if (!MEDIA_EXTENSIONS.has(ext)) ext = typeExtension(media.type);
  const stem = (title || fromUrl || "media").replace(/[\\/:*?"<>|]+/g, " ").trim().slice(0, 120);
  return `${stem}.${ext}`;
}

async function handleMessage(message, sender) {
  switch (message.cmd) {
    case "list": {
      refreshRules(); // opening the popup picks up rule changes made in ADM right away
      const key = mediaKey(message.tabId);
      return { media: (await ext.storage.session.get(key))[key] || [], settings: await settings() };
    }
    case "download": {
      const { media, tab } = message;
      return handOff({ url: media.url, referrer: tab.url, fileName: mediaFileName(tab.title, media), kind: media.kind });
    }
    case "setEnabled":
      await ext.storage.local.set({ enabled: message.enabled });
      return { ok: true };
    case "show":
      return sendToHost({ type: "show" });
    case "linkClick":
      if (!isWeb(message.url) || !Array.isArray(message.keys)) return { ok: false };
      rememberClick(message.url, message.keys);
      return { ok: true };
    case "forceLink":
      if (!isWeb(message.url) || !(await settings()).enabled) return { ok: false };
      return handOff({ url: message.url, referrer: sender?.url || "", kind: streamKind(message.url) || "http" });
    case "panelMedia":
      return sender?.tab ? { media: await panelMedia(sender.tab.id, await rules()) } : { media: [] };
    case "framePlayer": {
      // An embedded player fills its frame, so the frame's own panel could only sit over the video: the top frame
      // shows it above the frame instead. The frame's address is the browser's, never what the frame says.
      if (!sender?.tab || !sender.frameId || !isWeb(sender.url)) return { ok: false };
      const player = { ...message.player, url: sender.url.split("#")[0] };
      const reply = await ext.tabs.sendMessage(sender.tab.id, { cmd: "framePlayer", player }, { frameId: 0 }).catch(() => null);
      return { ok: reply?.ok === true };
    }
    case "siteChoices":
      return isWeb(message.url) ? siteChoices(message.url) : { ok: false, error: "Only a web page's video can be read." };
    case "setupVideoTools":
      return sendToHost({ type: "setupVideoTools" });
    case "panelDownload": {
      // Download all starts each choice at once: a dialog apiece would ask the same question over and over.
      const mode = message.all === true ? "now" : "prompt";
      if (message.site) {
        const { url, key, title } = message.site;
        if (!isWeb(url) || typeof key !== "string") return { ok: false };
        // ADM names it for the container it writes, such as WebM for the original.
        const fileName = `${fileStem(title || sender?.tab?.title)}.${key === "audio" ? "m4a" : "mp4"}`;
        return handOff({ url, referrer: sender?.tab?.url || "", fileName, kind: "site", siteChoice: key, mode });
      }
      const { media, bandwidth = null, audioOnly = false } = message;
      if (!isWeb(media?.url)) return { ok: false };
      // The video's caption, as the panel read it from the page, else the tab's title.
      const title = typeof message.title === "string" && message.title.trim() ? message.title : sender?.tab?.title;
      if (media.kind === "joined") {
        if (!isWeb(media.audioUrl)) return { ok: false };
        const fileName = `${fileStem(title)}.mp4`;
        return handOff({ url: media.url, audioUrl: media.audioUrl, referrer: sender?.tab?.url || "", fileName, kind: "joined", mode });
      }
      return handOff({
        url: media.url,
        referrer: sender?.tab?.url || "",
        fileName: mediaFileName(title, media),
        kind: isStream(media) ? media.kind : "http",
        hlsBandwidth: typeof bandwidth === "number" ? bandwidth : null,
        audioOnly: audioOnly === true,
        mode,
      });
    }
    default:
      return { ok: false, error: "unknown command" };
  }
}

ext.runtime.onMessage.addListener((message, sender, sendResponse) => {
  handleMessage(message, sender).then(sendResponse, (e) => sendResponse({ ok: false, error: String(e) }));
  return true;
});
