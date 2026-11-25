// Behaviour of background.js against a recording stand-in for the browser APIs.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { makeChrome, loadScript, plain } from "./browser-stub.mjs";

const NAMES = ["heldKeysFor", "rememberClick", "linksMessage", "refreshRules", "handleMessage", "streamKind", "siteChoices"];

function load(options = {}) {
  const stub = makeChrome(options);
  const fetch = async (url) => ({ text: async () => (options.pages || {})[url] ?? "" });
  return { ...stub, ...loadScript("background.js", { chrome: stub.chrome, fetch, atob }, NAMES) };
}

const zip = { id: 7, url: "https://a.com/f.zip", filename: "C:\\Users\\me\\Downloads\\f.zip", referrer: "https://a.com/", fileSize: -1 };
const downloads = (calls) => calls.native.filter((m) => m.type === "download");

function determineFilename(b, item) {
  return new Promise((resolve) => b.chrome.downloads.onDeterminingFilename.listeners[0](item, resolve));
}

test("Chrome capture hands the download over, then drops the browser's copy", async () => {
  const b = load();
  assert.equal(b.chrome.downloads.onCreated.listeners.length, 0);

  await determineFilename(b, zip);

  const [sent] = downloads(b.calls);
  assert.equal(sent.url, zip.url);
  assert.equal(sent.fileName, "f.zip");
  assert.equal(sent.captured, true);
  assert.deepEqual(b.calls.cancel, [7]);
  assert.equal(b.calls.erase.length, 1);
});

test("Firefox capture cancels only after the app accepts", async () => {
  const accepted = load({ firefox: true });
  await accepted.chrome.downloads.onCreated.fire(zip);
  assert.equal(downloads(accepted.calls)[0].captured, true);
  assert.deepEqual(accepted.calls.cancel, [7]);

  const refused = load({ firefox: true, host: () => ({ ok: false, error: "ADM is closed" }) });
  await refused.chrome.downloads.onCreated.fire(zip);
  assert.equal(downloads(refused.calls).length, 1);
  assert.deepEqual(refused.calls.cancel, []);
  assert.deepEqual(refused.calls.erase, []);
});

test("a click with the prevent keys leaves the download to the browser", async () => {
  const b = load();
  await b.handleMessage({ cmd: "linkClick", url: zip.url, keys: ["alt"] }, {});

  await determineFilename(b, zip);

  assert.equal(downloads(b.calls).length, 0);
  assert.deepEqual(b.calls.cancel, []);
});

test("a click with the force keys captures a type outside the list", async () => {
  const page = { ...zip, url: "https://a.com/report.html", filename: "report.html" };
  const b = load();
  await b.handleMessage({ cmd: "linkClick", url: page.url, keys: ["insert"] }, {});

  await determineFilename(b, page);

  assert.equal(downloads(b.calls)[0].url, page.url);
});

test("special keys apply only to a download that starts within ten seconds", () => {
  const b = load();
  b.rememberClick("https://a.com/f.zip", ["alt"], 1000);

  assert.deepEqual(plain(b.heldKeysFor("https://a.com/f.zip", 11_000)), ["alt"]);
  assert.deepEqual(plain(b.heldKeysFor("https://a.com/f.zip", 11_001)), []);
  assert.deepEqual(plain(b.heldKeysFor("https://a.com/other.zip", 1000)), []);
});

test("a forced link is sent with its page as referrer, not as a capture", async () => {
  const b = load();
  const reply = await b.handleMessage({ cmd: "forceLink", url: "https://a.com/watch?v=1" }, { url: "https://a.com/list" });

  assert.equal(reply.ok, true);
  const [sent] = downloads(b.calls);
  assert.equal(sent.url, "https://a.com/watch?v=1");
  assert.equal(sent.referrer, "https://a.com/list");
  assert.equal(sent.captured, false);
});

test("menus follow the config from ADM", async () => {
  let config = { menuDownloadLink: true, menuDownloadAllLinks: false };
  const b = load({ host: (m) => (m.type === "config" ? { ok: true, config } : { ok: true }) });

  await b.refreshRules();
  assert.deepEqual(b.calls.menus, ["adm-download"]);

  await b.refreshRules();
  assert.equal(b.calls.removeAll, 1, "unchanged menu settings do not rebuild the menu");

  config = { menuDownloadLink: false, menuDownloadAllLinks: true };
  await b.refreshRules();
  assert.deepEqual(b.calls.menus, ["adm-all-links"]);
});

test("All links message is capped and shares cookies per origin", async () => {
  const b = load({ cookies: (url) => [{ name: "s", value: new URL(url).host }] });
  const links = Array.from({ length: 6000 }, (_, i) => ({ url: `https://${i % 2 ? "a" : "b"}.com/f${i}.zip`, text: "", image: false }));

  const message = await b.linksMessage(links, "https://a.com/page");

  assert.equal(message.type, "links");
  assert.equal(message.links.length, 5000);
  assert.deepEqual(plain(message.cookies), { "https://b.com": "s=b.com", "https://a.com": "s=a.com" });
  assert.equal(b.calls.cookies.length, 2);

  const long = Array.from({ length: 5000 }, (_, i) => ({ url: `https://a.com/${"x".repeat(4000)}${i}`, text: "", image: false }));
  const capped = await b.linksMessage(long, "https://a.com/page");
  assert.ok(capped.links.length < 5000);
  assert.ok(JSON.stringify(capped).length < 8 * 1024 * 1024);
});

test("the panel offers a master playlist's qualities and hides its quality playlists and small files", async () => {
  const master = "https://a.com/v/master.m3u8";
  const b = load({
    pages: {
      [master]: readFileSync(new URL("../fixtures/master.m3u8", import.meta.url), "utf8"),
      "https://a.com/v/360p.m3u8": "#EXTM3U\n#EXTINF:4,\nseg0.ts\n#EXT-X-ENDLIST\n",
    },
  });
  await b.chrome.storage.session.set({
    "media:5": [
      { url: master, type: "application/vnd.apple.mpegurl", size: -1, kind: "hls" },
      { url: "https://a.com/v/360p.m3u8", type: "application/vnd.apple.mpegurl", size: -1, kind: "hls" },
      { url: "https://a.com/clip.mp4", type: "video/mp4", size: 100 * 1024, kind: "http" },
      { url: "https://a.com/film.mp4", type: "video/mp4", size: 5 * 1048576, kind: "http" },
    ],
  });

  const { media } = plain(await b.handleMessage({ cmd: "panelMedia" }, { tab: { id: 5 } }));

  assert.deepEqual(media.map((m) => m.url), [master, "https://a.com/film.mp4"]);
  assert.deepEqual(media[0].qualities.map((q) => q.label), ["1080p", "720p", "360p"]);
  assert.equal(media[0].qualities[1].bandwidth, 2500000);
  assert.equal(media[0].audioOnly, true);
});

test("a panel choice is handed over with its quality and the page's title", async () => {
  const b = load();
  const media = { url: "https://a.com/v/master.m3u8", type: "application/vnd.apple.mpegurl", size: -1, kind: "hls" };
  const tab = { id: 5, url: "https://a.com/watch", title: "A talk: part 1" };

  await b.handleMessage({ cmd: "panelDownload", media, bandwidth: 2500000 }, { tab });
  await b.handleMessage({ cmd: "panelDownload", media, audioOnly: true }, { tab });

  const [quality, audio] = downloads(b.calls);
  assert.deepEqual(
    [quality.url, quality.kind, quality.hlsBandwidth, quality.audioOnly, quality.referrer, quality.fileName],
    [media.url, "hls", 2500000, false, "https://a.com/watch", "A talk  part 1.ts"]
  );
  assert.deepEqual([audio.hlsBandwidth, audio.audioOnly], [null, true]);
});

// --- DASH ---

const mpd = "https://a.com/v/stream.mpd";
// A manifest is a small file, so it is offered whatever the panel's minimum size.
const dashMedia = { url: mpd, type: "application/dash+xml", size: 2048, kind: "dash" };
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

async function panelMediaOf(b, list) {
  await b.chrome.storage.session.set({ "media:5": list });
  return plain(await b.handleMessage({ cmd: "panelMedia" }, { tab: { id: 5 } })).media;
}

test("stream kinds follow the address as ADM's do, and the type before it", () => {
  const b = load();
  for (const c of JSON.parse(readFileSync(new URL("../fixtures/stream-kinds.json", import.meta.url), "utf8")))
    assert.equal(b.streamKind(c.url) ?? "http", c.kind, c.name);
  assert.equal(b.streamKind("https://a.com/play", "application/dash+xml"), "dash");
  assert.equal(b.streamKind("https://a.com/play", "application/x-mpegurl"), "hls");
  assert.equal(b.streamKind("https://a.com/play.m3u8", "application/dash+xml"), "dash");
  assert.equal(b.streamKind("https://a.com/play", "video/mp4"), null);
});

test("a DASH manifest is listed by its type or its address, and its parts are not", async () => {
  const b = load();
  for (const [url, type] of [
    ["https://a.com/api/manifest", "application/dash+xml; charset=utf-8"],
    ["https://a.com/v/stream.mpd", "text/xml"],
    ["https://a.com/v/seg-1.m4s", "video/mp4"],
  ]) {
    await b.chrome.webRequest.onHeadersReceived.fire({ tabId: 5, url, responseHeaders: [{ name: "Content-Type", value: type }] });
    await settle();
  }

  assert.deepEqual(
    plain(b.chrome.storage.session.data["media:5"]).map((m) => [m.url, m.kind]),
    [["https://a.com/api/manifest", "dash"], ["https://a.com/v/stream.mpd", "dash"]]
  );
});

test("the panel asks ADM for a DASH manifest's qualities", async () => {
  const text = readFileSync(new URL("../fixtures/dash/template-number.mpd", import.meta.url), "utf8");
  const qualities = [{ label: "720p", bandwidth: 1500000 }, { label: "360p", bandwidth: 500000 }];
  const b = load({
    pages: { [mpd]: text },
    host: (m) => (m.type === "dashChoices" ? { ok: true, qualities, audioOnly: true } : { ok: false, error: "closed" }),
  });

  const media = await panelMediaOf(b, [dashMedia]);

  const asked = b.calls.native.find((m) => m.type === "dashChoices");
  assert.deepEqual([asked.url, asked.text], [mpd, text]);
  assert.deepEqual(media[0].qualities, qualities);
  assert.equal(media[0].audioOnly, true);
});

test("a DASH manifest ADM refuses, or that ADM can't be asked about, is still offered", async () => {
  const refused = () => ({ ok: false, error: "Streams protected with DRM are not supported." });
  const unreachable = () => {
    throw new Error("Specified native messaging host not found.");
  };
  for (const host of [refused, unreachable]) {
    const media = await panelMediaOf(load({ pages: { [mpd]: "<MPD/>" }, host }), [dashMedia]);

    assert.deepEqual(media.map((m) => [m.url, m.qualities.length]), [[mpd, 0]]);
  }
});

test("a manifest too long for the native host is offered without asking", async () => {
  const b = load({ pages: { [mpd]: `<MPD>${" ".repeat(7 * 1024 * 1024)}</MPD>` } });

  const media = await panelMediaOf(b, [dashMedia]);

  assert.equal(b.calls.native.filter((m) => m.type === "dashChoices").length, 0);
  assert.deepEqual(media.map((m) => [m.url, m.qualities.length]), [[mpd, 0]]);
});

test("a DASH manifest with qualities doesn't hide an HLS playlist on the same page", async () => {
  const hls = { url: "https://a.com/live/index.m3u8", type: "application/vnd.apple.mpegurl", size: -1, kind: "hls" };
  const b = load({
    pages: { [mpd]: "<MPD/>", [hls.url]: "#EXTM3U\n#EXTINF:4,\nseg0.ts\n" },
    host: (m) => (m.type === "dashChoices" ? { ok: true, qualities: [{ label: "720p", bandwidth: 1 }], audioOnly: false } : { ok: false }),
  });

  const media = await panelMediaOf(b, [dashMedia, hls]);

  assert.deepEqual(media.map((m) => m.url), [mpd, hls.url]);
});

test("a DASH choice is handed over as DASH, with its quality, named for an MP4", async () => {
  const b = load();
  const tab = { id: 5, url: "https://a.com/watch", title: "A talk" };

  await b.handleMessage({ cmd: "panelDownload", media: dashMedia, bandwidth: 500000 }, { tab });
  await b.handleMessage({ cmd: "download", media: dashMedia, tab }, {});

  const [panel, popup] = downloads(b.calls);
  assert.deepEqual([panel.url, panel.kind, panel.hlsBandwidth, panel.fileName], [mpd, "dash", 500000, "A talk.mp4"]);
  assert.deepEqual([popup.kind, popup.fileName], ["dash", "A talk.mp4"]);
});

test("a manifest's link sent from the menu or with the force keys goes as DASH", async () => {
  const b = load();

  await b.chrome.contextMenus.onClicked.fire({ menuItemId: "adm-download", linkUrl: mpd, pageUrl: "https://a.com/" }, { id: 5 });
  await b.handleMessage({ cmd: "forceLink", url: mpd }, { url: "https://a.com/" });

  assert.deepEqual(downloads(b.calls).map((m) => m.kind), ["dash", "dash"]);
});

test("Download all links asks the clicked frame, injecting the content script when it is missing", async () => {
  const found = [{ url: "https://a.com/f.zip", text: "f", image: false }];
  let asked = 0;
  const b = load({
    tabMessage: async () => {
      if (asked++ === 0) throw new Error("Receiving end does not exist.");
      return found;
    },
  });

  await b.chrome.contextMenus.onClicked.fire({ menuItemId: "adm-all-links", frameId: 3, pageUrl: "https://a.com/" }, { id: 5 });

  assert.deepEqual(plain(b.calls.scripts), [{ target: { tabId: 5, frameIds: [3] }, files: ["content.js"] }]);
  assert.deepEqual(plain(b.calls.tabMessages[1]), [5, { cmd: "collectLinks", selection: false }, { frameId: 3 }]);
  const sent = b.calls.native.find((m) => m.type === "links");
  assert.deepEqual(plain(sent.links), found);
  assert.equal(sent.referrer, "https://a.com/");
});

// --- A site's video, and Facebook's pieces ---

// As Facebook's addresses were on 2026-11-19: efg is base64 JSON naming the video and saying which file is the sound.
// Written out as text, so an id past 2^53 keeps every digit.
const efg = (tag, bitrate, videoId, duration) =>
  encodeURIComponent(btoa(`{"vencode_tag":"${tag}","video_id":${videoId},"duration_s":${duration},"bitrate":${bitrate}}`));
const fbFile = (path, tag, bitrate, extra, videoId = "2960280377674081", duration = 60) =>
  `https://video.xx.fbcdn.net/o1/v/t2/f2/m366/${path}.mp4?_nc_cat=1&efg=${efg(tag, bitrate, videoId, duration)}&${extra}`;

async function receive(b, url, type = "video/mp4", length = "1830") {
  await b.chrome.webRequest.onHeadersReceived.fire({
    tabId: 5,
    url,
    responseHeaders: [{ name: "Content-Type", value: type }, { name: "Content-Length", value: length }],
  });
  await settle();
}

test("a piece address is recorded as its whole file, once", async () => {
  const b = load();
  await receive(b, fbFile("AQO", "dash_r2av1_q20", 165138, "_nc_gid=a&oh=1&bytestart=0&byteend=1830"));
  await receive(b, fbFile("AQO", "dash_r2av1_q20", 165138, "_nc_gid=b&oh=2&bytestart=1831&byteend=115508"), "video/mp4", "113678");

  const stored = plain(b.chrome.storage.session.data["media:5"]);

  assert.equal(stored.length, 1);
  assert.ok(!/bytestart|byteend/.test(stored[0].url), stored[0].url);
  assert.ok(stored[0].url.includes("_nc_gid=a"));
});

test("pieces of one video pair the picture with the sound", async () => {
  const b = load();
  await receive(b, fbFile("AQO", "dash_r2av1_q20", 165138, "bytestart=0&byteend=1830"));
  await receive(b, fbFile("AQX", "dash_r2av1_q50", 479019, "bytestart=0&byteend=1830"));
  await receive(b, fbFile("AQN", "dash_ln_heaac_vbr3_audio", 79008, "bytestart=0&byteend=3279"));

  const { media } = plain(await b.handleMessage({ cmd: "panelMedia" }, { tab: { id: 5 } }));

  assert.deepEqual(media.map((m) => [m.kind, m.label]), [
    ["joined", "Video, 479 kbps, with sound"],
    ["joined", "Video, 165 kbps, with sound"],
  ]);
  assert.ok(media[0].url.includes("/AQX.mp4"));
  assert.ok(media.every((m) => m.audioUrl.includes("/AQN.mp4") && !m.audioUrl.includes("bytestart")));
});

// As a Facebook reel page was on 2026-11-19: beside the reel shown, it loads the reels around it, each file naming its video.
test("each video offered names its video and its length, as the panel tells the one shown from those around it", async () => {
  const b = load();
  const shown = "1128943972873365";
  const next = "90071992547409931"; // past 2^53, where a number loses its last digits
  await receive(b, fbFile("AQM", "dash_h264-basic-gen2_720p", 1354725, "bytestart=0&byteend=1338", shown, 197));
  await receive(b, fbFile("AQO", "dash_ln_heaac_vbr3_audio", 54190, "bytestart=0&byteend=2043", shown, 197));
  await receive(b, fbFile("AQN", "dash_h264-basic-gen2_720p", 340829, "bytestart=0&byteend=1494", next, 260));
  await receive(b, fbFile("AQP", "dash_ln_heaac_vbr3_audio", 59270, "bytestart=0&byteend=2427", next, 260));
  await receive(b, fbFile("AQL", "dash_h264-basic-gen2_720p", 435451, "bytestart=0&byteend=900", "2030249154335733", 27));
  await receive(b, "https://cdn.example/v/clip.mp4?bytestart=0&byteend=99");

  const { media } = plain(await b.handleMessage({ cmd: "panelMedia" }, { tab: { id: 5 } }));

  assert.deepEqual(media.map((m) => [m.kind, m.videoId, m.durationS]), [
    ["joined", shown, 197],
    ["joined", next, 260],
    ["http", "2030249154335733", 27],
    ["http", null, 0],
  ]);
});

test("a piece without efg is offered as its whole file", async () => {
  const b = load();
  await receive(b, "https://cdn.example/v/clip.mp4?bytestart=0&byteend=99");

  const { media } = plain(await b.handleMessage({ cmd: "panelMedia" }, { tab: { id: 5 } }));

  assert.deepEqual(media.map((m) => [m.kind, m.url]), [["http", "https://cdn.example/v/clip.mp4"]]);
});

test("site choices are asked once per address for 30 minutes", async () => {
  const page = "https://www.youtube.com/watch?v=abc";
  const reply = { ok: true, title: "T", choices: [{ key: "mp4:720", label: "720p MP4" }], missingTools: [] };
  const b = load({ host: (m) => (m.type === "videoChoices" ? reply : { ok: true }) });
  const asked = () => b.calls.native.filter((m) => m.type === "videoChoices");

  assert.deepEqual(plain(await b.siteChoices(page, 0)), reply);
  await b.siteChoices(page, 29 * 60 * 1000);
  assert.deepEqual(asked().map((m) => m.url), [page]);

  await b.siteChoices(page, 31 * 60 * 1000);
  assert.equal(asked().length, 2);
  assert.deepEqual(plain(await b.handleMessage({ cmd: "siteChoices", url: "file:///C:/x" }, {})).ok, false);
});

test("site choices asked while ADM is still reading the page wait for that read", async () => {
  // The panel reads ahead as it shows, so a click soon after must not start yt-dlp a second time.
  const page = "https://www.youtube.com/watch?v=abc";
  const reply = { ok: true, title: "T", choices: [{ key: "mp4:720", label: "720p MP4" }], missingTools: [] };
  let answer;
  const b = load({ host: (m) => (m.type === "videoChoices" ? new Promise((resolve) => (answer = () => resolve(reply))) : { ok: true }) });

  const first = b.siteChoices(page, 0);
  const second = b.siteChoices(page, 0);
  await new Promise(setImmediate);
  answer();

  assert.deepEqual([plain(await first), plain(await second)], [reply, reply]);
  assert.equal(b.calls.native.filter((m) => m.type === "videoChoices").length, 1);
});

test("a read that failed is asked again", async () => {
  const page = "https://www.youtube.com/watch?v=abc";
  const b = load({ host: (m) => (m.type === "videoChoices" ? { ok: false, error: "no", missingTools: ["yt-dlp"] } : { ok: true }) });

  await b.siteChoices(page, 0);
  await b.siteChoices(page, 0);

  assert.equal(b.calls.native.filter((m) => m.type === "videoChoices").length, 2);
});

const videoChoices = (b) => b.calls.native.filter((m) => m.type === "videoChoices").map((m) => m.url);
const choicesReply = { ok: true, title: "T", choices: [{ key: "mp4:720", label: "720p MP4" }], missingTools: [] };

test("a tab that opens a video's page has it read at once, and the panel's menu answers from that", async () => {
  const b = load({ host: (m) => (m.type === "videoChoices" ? choicesReply : { ok: true }) });

  await b.chrome.tabs.onUpdated.fire(5, { status: "loading", url: "https://www.youtube.com/watch?v=abc#top" });
  // YouTube moving to another video without loading a page.
  await b.chrome.tabs.onUpdated.fire(5, { url: "https://www.youtube.com/watch?v=def&pp=x" });
  const reply = await b.handleMessage({ cmd: "siteChoices", url: "https://www.youtube.com/watch?v=abc" }, {});

  assert.deepEqual(plain(reply), choicesReply);
  assert.deepEqual(videoChoices(b), ["https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=def&pp=x"]);
});

test("only an address that names one video is read before its player shows", async () => {
  const b = load({ host: (m) => (m.type === "videoChoices" ? choicesReply : { ok: true }) });
  const read = [
    "https://m.youtube.com/watch?feature=share&v=abc",
    "https://www.youtube.com/shorts/abc",
    "https://www.youtube.com/live/abc",
    "https://www.facebook.com/reel/123",
    "https://www.facebook.com/watch/?v=123",
    "https://www.facebook.com/SomePage/videos/a-talk/123/",
    "https://vimeo.com/123",
    "https://www.dailymotion.com/video/x8abc",
    "https://www.tiktok.com/@someone/video/123",
  ];
  const notRead = [
    "https://www.youtube.com/",
    "https://www.youtube.com/results?search_query=v=abc",
    "https://www.youtube.com/@channel/videos",
    "https://www.facebook.com/",
    "https://www.facebook.com/SomePage/videos",
    "https://www.facebook.com/login/?next=/videos/123",
    "https://vimeo.com/channels/staffpicks",
    "https://example.com/watch?v=abc",
    "http://www.youtube.com.example.com/watch?v=abc",
    "file:///C:/watch?v=abc",
  ];

  let tab = 1;
  for (const url of [...read, ...notRead]) await b.chrome.tabs.onUpdated.fire(tab++, { url });
  await new Promise(setImmediate);

  assert.deepEqual(videoChoices(b), read);
});

test("early reads run two at a time, the tab in front first, and skip a tab closed or moved on", async () => {
  const answers = [];
  const b = load({ host: (m) => (m.type === "videoChoices" ? new Promise((resolve) => answers.push(() => resolve(choicesReply))) : { ok: true }) });
  const page = (n) => `https://www.youtube.com/watch?v=v${n}`;
  const finishOne = async () => {
    answers.shift()();
    await new Promise(setImmediate);
  };

  for (const n of [1, 2, 3, 4, 5, 6]) await b.chrome.tabs.onUpdated.fire(n, { url: page(n) });
  assert.deepEqual(videoChoices(b), [page(1), page(2)]);

  await b.chrome.tabs.onActivated.fire({ tabId: 3, windowId: 1 });
  await b.chrome.tabs.onRemoved.fire(6);
  await b.chrome.tabs.onUpdated.fire(5, { url: "https://www.youtube.com/" });
  await finishOne();
  await finishOne();
  await finishOne();
  await finishOne();

  assert.deepEqual(videoChoices(b), [page(1), page(2), page(3), page(4)]);
  assert.equal(answers.length, 0);
});

test("a video's page the tab has left before its read could start isn't read", async () => {
  const b = load({ host: (m) => (m.type === "videoChoices" ? choicesReply : { ok: true }) });

  await Promise.all([
    b.chrome.tabs.onUpdated.fire(5, { url: "https://www.youtube.com/watch?v=abc" }),
    b.chrome.tabs.onUpdated.fire(5, { url: "https://www.youtube.com/" }),
  ]);

  assert.deepEqual(videoChoices(b), []);
});

test("no early read where the panel is off, or on a site it is turned off for", async () => {
  const cases = [
    [{ videoPanel: "off" }, []],
    [{ videoPanelExceptionSites: "*.youtube.com" }, ["https://vimeo.com/123"]],
  ];
  for (const [changed, read] of cases) {
    const b = load({ host: (m) => (m.type === "videoChoices" ? choicesReply : { ok: true }) });
    const rules = { videoPanel: "full", videoPanelExceptionSites: "", ...changed };
    await b.chrome.storage.local.set({ rules, rulesFetchedAt: Date.now() });

    await b.chrome.tabs.onUpdated.fire(5, { url: "https://www.youtube.com/watch?v=abc" });
    await b.chrome.tabs.onUpdated.fire(6, { url: "https://vimeo.com/123" });

    assert.deepEqual(videoChoices(b), read);
  }
});

test("a site choice is handed over with its key and the page's title", async () => {
  const b = load({ cookies: () => [{ name: "SID", value: "secret" }] });
  const page = "https://www.youtube.com/watch?v=abc";
  const tab = { id: 5, url: page, title: "Something - YouTube" };

  await b.handleMessage({ cmd: "panelDownload", site: { url: page, key: "mp4:1080", title: "ভিডিও: পরীক্ষা/১" } }, { tab });
  await b.handleMessage({ cmd: "panelDownload", site: { url: page, key: "audio" } }, { tab });

  const [video, audio] = downloads(b.calls);
  assert.deepEqual(
    [video.url, video.kind, video.siteChoice, video.fileName, video.referrer, video.cookies],
    [page, "site", "mp4:1080", "ভিডিও  পরীক্ষা ১.mp4", page, ""]
  );
  assert.deepEqual([audio.siteChoice, audio.fileName], ["audio", "Something - YouTube.m4a"]);
});

test("a choice sent by Download all starts at once, and one picked alone asks first", async () => {
  const b = load();
  const page = "https://www.youtube.com/watch?v=abc";
  const tab = { id: 5, url: page, title: "Something - YouTube" };
  const media = { url: "https://a.com/v/master.m3u8", kind: "hls", type: "application/vnd.apple.mpegurl", size: 2048 };

  await b.handleMessage({ cmd: "panelDownload", site: { url: page, key: "mp4:1080" }, all: true }, { tab });
  await b.handleMessage({ cmd: "panelDownload", media, bandwidth: 2500000, all: true }, { tab });
  await b.handleMessage({ cmd: "panelDownload", site: { url: page, key: "mp4:720" } }, { tab });

  assert.deepEqual(downloads(b.calls).map((m) => m.mode), ["now", "now", "prompt"]);
});

// The page picks the address and the type of what it plays, and Download all starts without a dialog showing the name:
// a file is never saved under an extension that isn't a video's or a sound's.
test("a file the panel offers keeps a media extension from its address, else takes its type's", async () => {
  const b = load();
  const tab = { id: 5, url: "https://a.example/", title: "Clips" };
  const offered = [
    { url: "https://a.example/payload.exe", kind: "http", type: "video/mp4", size: 5 },
    { url: "https://a.example/stream.php?id=1", kind: "http", type: "video/webm", size: 5 },
    { url: "https://a.example/clip.MKV", kind: "http", type: "video/x-matroska", size: 5 },
    { url: "https://a.example/song.mp3", kind: "http", type: "audio/mpeg", size: 5 },
    { url: "https://a.example/get", kind: "http", type: "audio/mpeg", size: 5 },
  ];

  for (const media of offered) await b.handleMessage({ cmd: "panelDownload", media, all: true }, { tab });

  assert.deepEqual(downloads(b.calls).map((d) => d.fileName), ["Clips.mp4", "Clips.webm", "Clips.mkv", "Clips.mp3", "Clips.mp3"]);
});

test("a type that names no media extension saves as an MP4, or for sound an MP3", async () => {
  const b = load();
  const tab = { id: 5, url: "https://a.example/", title: "Clips" };

  await b.handleMessage({ cmd: "panelDownload", media: { url: "https://a.example/a", kind: "http", type: "video/x-msdownload", size: 5 }, all: true }, { tab });
  await b.handleMessage({ cmd: "panelDownload", media: { url: "https://a.example/b.bat", kind: "http", type: "audio/exe", size: 5 }, all: true }, { tab });

  assert.deepEqual(downloads(b.calls).map((d) => d.fileName), ["Clips.mp4", "Clips.mp3"]);
});

test("a joined choice is handed over with both addresses", async () => {
  const b = load();
  const media = { url: "https://v.example/v.mp4", audioUrl: "https://v.example/a.mp4", kind: "joined", type: "video/mp4", size: -1 };

  await b.handleMessage({ cmd: "panelDownload", media }, { tab: { id: 5, url: "https://www.facebook.com/watch", title: "A talk: part 1" } });

  const [sent] = downloads(b.calls);
  assert.deepEqual([sent.url, sent.audioUrl, sent.kind, sent.fileName], [media.url, media.audioUrl, "joined", "A talk  part 1.mp4"]);
});

test("a file chosen in the panel is named by the video's caption the panel sends", async () => {
  const b = load();
  const tab = { id: 5, url: "https://www.facebook.com/", title: "(3) Facebook" };
  const file = { url: "https://video.fbcdn.net/v/AQMRCHs3YlsImF0O0D4r7VzGVhKz.mp4?_nc=1", kind: "http", type: "video/mp4", size: 5 };
  const joined = { url: "https://v.example/v.mp4", audioUrl: "https://v.example/a.mp4", kind: "joined", type: "video/mp4", size: -1 };

  await b.handleMessage({ cmd: "panelDownload", media: file, title: "Sunset at the beach" }, { tab });
  await b.handleMessage({ cmd: "panelDownload", media: joined, title: "Sunset: day 2" }, { tab });
  await b.handleMessage({ cmd: "panelDownload", media: file, title: 7 }, { tab });

  assert.deepEqual(downloads(b.calls).map((d) => d.fileName), ["Sunset at the beach.mp4", "Sunset  day 2.mp4", "(3) Facebook.mp4"]);
});

test("setting up the tools asks ADM", async () => {
  const b = load();

  await b.handleMessage({ cmd: "setupVideoTools" }, {});

  assert.deepEqual(b.calls.native.map((m) => m.type), ["setupVideoTools"]);
});

// --- A player in a frame ---

const embed = { tab: { id: 5, url: "https://a.com/course" }, frameId: 3, url: "https://player.example/embed/1#t=5" };
const framePlayer = { id: "p1", kind: "video", fedBySource: true, caption: "A talk", duration: 60 };

test("a frame's player goes to the top frame, with the frame's address", async () => {
  const b = load({ tabMessage: async () => ({ ok: true }) });

  const reply = await b.handleMessage({ cmd: "framePlayer", player: framePlayer }, embed);

  assert.deepEqual(plain(reply), { ok: true });
  assert.deepEqual(plain(b.calls.tabMessages), [
    [5, { cmd: "framePlayer", player: { ...framePlayer, url: "https://player.example/embed/1" } }, { frameId: 0 }],
  ]);
});

test("a frame's player stays with its frame when the top frame doesn't take it", async () => {
  const refused = load({ tabMessage: async () => ({ ok: false }) });
  assert.deepEqual(plain(await refused.handleMessage({ cmd: "framePlayer", player: framePlayer }, embed)), { ok: false });

  const absent = load({ tabMessage: async () => Promise.reject(new Error("Receiving end does not exist.")) });
  assert.deepEqual(plain(await absent.handleMessage({ cmd: "framePlayer", player: framePlayer }, embed)), { ok: false });
});

test("only a frame below the top frame hands its player up", async () => {
  const b = load({ tabMessage: async () => ({ ok: true }) });

  const fromTop = await b.handleMessage({ cmd: "framePlayer", player: framePlayer }, { ...embed, frameId: 0 });
  const notWeb = await b.handleMessage({ cmd: "framePlayer", player: framePlayer }, { ...embed, url: "about:blank" });

  assert.deepEqual(plain([fromTop, notWeb]), [{ ok: false }, { ok: false }]);
  assert.deepEqual(b.calls.tabMessages, []);
});
