// Behaviour of content.js against a minimal page and browser stand-in.
import { test } from "node:test";
import assert from "node:assert/strict";
import { loadScript, plain } from "./browser-stub.mjs";

class Element {
  constructor(props) {
    Object.assign(this, props);
  }
  matches() {
    return this.tag === "a";
  }
}

// Just enough of a DOM node for the panel to be built and placed.
class Node {
  constructor(tag) {
    Object.assign(this, { tag, style: {}, attributes: {}, children: [], listeners: {}, hidden: false });
  }
  append(...nodes) {
    this.children.push(...nodes);
  }
  replaceChildren(...nodes) {
    this.children = nodes;
  }
  setAttribute(name, value) {
    this.attributes[name] = value;
  }
  addEventListener(type, fn) {
    (this.listeners[type] ||= []).push(fn);
  }
  attachShadow() {
    return new Node("#shadow-root");
  }
  getBoundingClientRect() {
    return { width: 180, height: 30 };
  }
  querySelector() {
    return null;
  }
  focus() {}
}

// A video in the page, fed through Media Source.
class Video extends Node {
  constructor(rect) {
    super("video");
    Object.assign(this, { rect, currentSrc: "blob:https://player.example/1", isConnected: true, duration: 60 });
  }
  getBoundingClientRect() {
    return this.rect;
  }
  closest() {
    return null;
  }
}

/** @param inFrame  runs in a frame below the top one, whose posts to the frame above are kept in `posts` */
async function loadContent({ rules, anchors = [], images = [], frames = [], href = "https://a.com/page#top", answer = () => undefined, inFrame = false } = {}) {
  const listeners = {};
  const sent = [];
  const posts = [];
  const chrome = {
    storage: { local: { get: async () => ({ rules }) }, onChanged: { addListener() {} } },
    runtime: {
      sendMessage: async (message) => {
        sent.push(message);
        return answer(message);
      },
      onMessage: { addListener: (fn) => (listeners.runtime = fn) },
    },
  };
  const document = {
    querySelectorAll: (selector) => (selector.includes("iframe") ? frames : anchors),
    images,
    title: "",
    documentElement: new Node("html"),
    createElement: (tag) => new Node(tag),
    createElementNS: (_ns, tag) => new Node(tag),
  };
  const window = { postMessage() {} };
  window.top = window.parent = window;
  if (inFrame) {
    window.parent = { postMessage: (data, origin) => posts.push({ data, origin }) };
    window.top = {};
  }
  const { admContent } = loadScript("content.js", {
    chrome,
    window,
    Element,
    HTMLVideoElement: Video,
    HTMLAudioElement: class {},
    addEventListener: (type, fn) => (listeners[type] ||= []).push(fn),
    location: { href },
    setTimeout,
    setInterval: () => 0,
    innerHeight: 900,
    getComputedStyle: () => ({ paddingLeft: "0px", paddingTop: "0px", paddingRight: "0px", paddingBottom: "0px" }),
    document,
    getSelection: () => ({ rangeCount: 0 }),
  }, ["admContent"]);
  await new Promise(setImmediate); // the saved rules load asynchronously
  return { listeners, sent, posts, document, panel: admContent };
}

const link = new Element({ tag: "a", href: "https://a.com/f.zip" });

function dispatch(listeners, type, props) {
  const e = {
    isTrusted: true,
    button: 0,
    ctrlKey: false,
    altKey: false,
    shiftKey: false,
    defaultPrevented: false,
    composedPath: () => [new Element({ tag: "span" }), link],
    preventDefault() {
      this.defaultPrevented = true;
    },
    stopImmediatePropagation() {},
    ...props,
  };
  for (const fn of listeners[type] || []) fn(e);
  return e;
}

test("clicks without special keys are not reported", async () => {
  const { listeners, sent } = await loadContent();
  dispatch(listeners, "click", {});
  dispatch(listeners, "click", { ctrlKey: true });
  assert.deepEqual(sent, []);
});

test("a click with the prevent keys is reported with the keys held", async () => {
  const { listeners, sent } = await loadContent();
  const e = dispatch(listeners, "click", { altKey: true });
  assert.deepEqual(plain(sent), [{ cmd: "linkClick", url: "https://a.com/f.zip", keys: ["alt"] }]);
  assert.equal(e.defaultPrevented, false);
});

test("a click with Insert held sends the link to ADM instead of opening it", async () => {
  const { listeners, sent } = await loadContent();
  dispatch(listeners, "keydown", { key: "Insert" });
  const e = dispatch(listeners, "click", {});
  assert.deepEqual(plain(sent), [{ cmd: "forceLink", url: "https://a.com/f.zip" }]);
  assert.equal(e.defaultPrevented, true);

  dispatch(listeners, "keyup", { key: "Insert" });
  dispatch(listeners, "click", {});
  assert.equal(sent.length, 1);
});

test("without force on link click, the force keys are only reported", async () => {
  const { listeners, sent } = await loadContent({ rules: { preventKeys: ["alt"], forceKeys: ["ctrl", "shift"], forceOnLinkClick: false } });
  const e = dispatch(listeners, "click", { ctrlKey: true, shiftKey: true });
  assert.deepEqual(plain(sent), [{ cmd: "linkClick", url: "https://a.com/f.zip", keys: ["ctrl", "shift"] }]);
  assert.equal(e.defaultPrevented, false);
});

test("events a page makes up are ignored", async () => {
  const { listeners, sent } = await loadContent();
  dispatch(listeners, "keydown", { key: "Insert", isTrusted: false });
  dispatch(listeners, "click", {});
  dispatch(listeners, "click", { altKey: true, isTrusted: false });
  assert.deepEqual(sent, []);
});

test("collects each web link once, without fragments, and marks images", async () => {
  const { listeners } = await loadContent({
    anchors: [
      { href: "https://a.com/f.zip", textContent: "  Get \n it " },
      { href: "https://a.com/page#top", textContent: "Top" },
      { href: "https://a.com/f.zip#mirror", textContent: "again" },
      { href: "javascript:void(0)", textContent: "menu" },
      { href: "https://b.com/g.exe", textContent: "", title: "G" },
    ],
    images: [{ currentSrc: "https://a.com/i.png", alt: "pic" }],
  });
  let links;
  listeners.runtime({ cmd: "collectLinks", selection: false }, {}, (reply) => (links = reply));

  assert.deepEqual(plain(links), [
    { url: "https://a.com/f.zip", text: "Get it", image: false },
    { url: "https://b.com/g.exe", text: "G", image: false },
    { url: "https://a.com/i.png", text: "pic", image: true },
  ]);
});

test("the panel shows only over a large enough player on a site that is not an exception", async () => {
  const { panel } = await loadContent();
  const rules = { videoPanel: "full", videoPanelExceptionSites: "*.quiet.example" };
  const page = "https://a.com/watch";

  assert.equal(panel.panelAllowed(rules, page, { width: 640, height: 360 }, "video"), true);
  assert.equal(panel.panelAllowed(rules, page, { width: 180, height: 100 }, "video"), false);
  assert.equal(panel.panelAllowed(rules, page, { width: 300, height: 32 }, "audio"), true);
  assert.equal(panel.panelAllowed(rules, "https://www.quiet.example/v", { width: 640, height: 360 }, "video"), false);
  assert.equal(panel.panelAllowed({ ...rules, videoPanel: "off" }, page, { width: 640, height: 360 }, "video"), false);
  assert.equal(panel.panelAllowed({ ...rules, videoPanel: "mini" }, page, { width: 640, height: 360 }, "video"), true);
});

test("the panel menu lists each quality, the audio track, and files by name and size", async () => {
  const { panel } = await loadContent();
  const hls = { url: "https://a.com/m.m3u8", kind: "hls", qualities: [{ label: "1080p", bandwidth: 5 }, { label: "720p", bandwidth: 2 }], audioOnly: true };
  const file = { url: "https://a.com/films/clip%201.mp4", kind: "http", size: 5 * 1048576 };

  const list = plain(panel.choices([hls, file]));

  assert.deepEqual(list.map((c) => c.label), ["1080p", "720p", "Audio only", "clip 1.mp4 (5.0 MB)"]);
  assert.equal(list[1].message.bandwidth, 2);
  assert.equal(list[2].message.audioOnly, true);
  assert.equal(list[3].message.media.url, file.url);
});

test("the panel menu offers a DASH stream as it does an HLS one", async () => {
  const { panel } = await loadContent();
  const dash = { url: "https://a.com/v/stream.mpd", kind: "dash", qualities: [{ label: "720p", bandwidth: 7 }], audioOnly: true };
  const refused = { url: "https://a.com/v/drm.mpd", kind: "dash", size: 2048, qualities: [] };

  const list = plain(panel.choices([dash, refused]));

  assert.deepEqual(list.map((c) => c.label), ["720p", "Audio only", "Best quality"]);
  assert.equal(list[0].message.bandwidth, 7);
  assert.equal(list[2].message.media.url, refused.url);
});

// --- A site's video ---

const watch = "https://www.youtube.com/watch?v=abc";
const sniffed = { url: "https://a.com/films/clip.mp4", kind: "http", size: 5 * 1048576 };
const choicesReply = {
  ok: true,
  title: "A video",
  choices: [{ key: "mp4:1080", label: "1080p MP4", size: 145 * 1048576 }, { key: "original", label: "Original" }],
  missingTools: [],
};

test("the panel shows over a blob player with no media found", async () => {
  const { panel } = await loadContent();

  assert.equal(panel.panelWanted([], { currentSrc: "blob:https://www.youtube.com/1" }, watch), true);
  assert.equal(panel.panelWanted([], { currentSrc: "https://a.com/clip.mp4" }, watch), false);
  assert.equal(panel.panelWanted([sniffed], { currentSrc: "https://a.com/clip.mp4" }, watch), true);
  assert.equal(panel.panelWanted([], { currentSrc: "blob:file:///x" }, "file:///C:/page.html"), false);
});

test("the menu says Finding qualities, then lists the site's choices before the tab's", async () => {
  const { panel } = await loadContent();

  const waiting = plain(panel.menuItems([sniffed], { url: watch }));
  assert.deepEqual(waiting.map((i) => [i.label, !!i.disabled]), [["Finding qualities…", true], ["clip.mp4 (5.0 MB)", false]]);

  const found = plain(panel.menuItems([sniffed], { url: watch, reply: choicesReply }));
  assert.deepEqual(found.map((i) => i.label), ["Download all", "1080p MP4 (145.0 MB)", "Original", "clip.mp4 (5.0 MB)"]);
  assert.deepEqual(found[1].message, { site: { url: watch, key: "mp4:1080", title: "A video" } });
});

test("Download all heads the menu, and sends every choice to start at once", async () => {
  const { panel } = await loadContent();

  const [all, ...rest] = plain(panel.menuItems([sniffed], { url: watch, reply: choicesReply }));

  assert.equal(all.label, "Download all");
  assert.deepEqual(all.messages, rest.map((i) => ({ ...i.message, all: true })));
});

test("Download all is offered only once there are two choices to download", async () => {
  const { panel } = await loadContent();
  const labels = (items) => plain(items).map((i) => i.label);

  assert.deepEqual(labels(panel.menuItems([sniffed], null)), ["clip.mp4 (5.0 MB)"]);
  assert.deepEqual(labels(panel.menuItems([sniffed, { ...sniffed, url: "https://a.com/two.mp4" }], { url: watch })), [
    "Finding qualities…",
    "clip.mp4 (5.0 MB)",
    "two.mp4 (5.0 MB)",
  ]);
  const oneAndSetup = { ...choicesReply, choices: choicesReply.choices.slice(0, 1), missingTools: ["ffmpeg"] };
  assert.deepEqual(labels(panel.menuItems([], { url: watch, reply: oneAndSetup })), ["1080p MP4 (145.0 MB)", "Set up video downloads…"]);
});

test("the panel sits just above the player, at its right edge", async () => {
  const { panel } = await loadContent();
  const size = { width: 180, height: 30 };

  assert.deepEqual(plain(panel.panelSpot({ top: 200, right: 900 }, size)), { left: 720, top: 166 });
});

test("the panel stays in the window when the player's top or left edge is out of it", async () => {
  const { panel } = await loadContent();
  const size = { width: 180, height: 30 };

  assert.deepEqual(plain(panel.panelSpot({ top: 20, right: 900 }, size)), { left: 720, top: 0 });
  assert.deepEqual(plain(panel.panelSpot({ top: -300, right: 120 }, size)), { left: 0, top: 0 });
});

// --- A player in a frame ---

// An embedded player's frame, its page showing from (100, 300) in the window, 640 by 360.
function playerFrame({ left = 100, top = 300, width = 640, height = 360 } = {}) {
  return {
    contentWindow: {},
    isConnected: true,
    clientLeft: 0,
    clientTop: 0,
    clientWidth: width,
    clientHeight: height,
    getBoundingClientRect: () => ({ left, top, right: left + width, bottom: top + height, width, height }),
  };
}

const placeIn = (listeners, frame, id, rect) => {
  for (const fn of listeners.message || []) fn({ source: frame.contentWindow, data: { adm: "adm-player-rect", id, rect } });
};

test("a rectangle in a frame's page is placed in the window holding the frame", async () => {
  const { panel } = await loadContent();
  const box = { left: 100, top: 300, width: 640, height: 360 };

  assert.deepEqual(plain(panel.inFrame(box, { left: 0, top: 0, right: 640, bottom: 360 })), {
    left: 100, top: 300, right: 740, bottom: 660, width: 640, height: 360,
  });
});

test("a player scrolled partly out of its frame is placed by the part the frame shows", async () => {
  const { panel } = await loadContent();
  const box = { left: 100, top: 300, width: 640, height: 360 };

  assert.deepEqual(plain(panel.inFrame(box, { left: -20, top: -100, right: 700, bottom: 200 })), {
    left: 100, top: 300, right: 740, bottom: 500, width: 640, height: 200,
  });
  assert.equal(panel.inFrame(box, { left: 0, top: 400, right: 640, bottom: 700 }), null);
});

test("a frame's player gets the window's panel, just above the frame's video", async () => {
  const frame = playerFrame();
  const { listeners, document, panel } = await loadContent({ frames: [frame] });

  placeIn(listeners, frame, "p1", { left: 0, top: 0, right: 640, bottom: 360 });
  const reply = await panel.framePlayer({ id: "p1", kind: "video", url: "https://player.example/embed/1", caption: "A talk" });

  assert.deepEqual(plain(reply), { ok: true });
  const [host] = document.documentElement.children;
  assert.equal(host.style.display, "block");
  // Right edge on the video's at 740, 180 wide; 30 high and 4 above the video's top at 300.
  assert.deepEqual([host.style.left, host.style.top], ["560px", "266px"]);
});

test("a frame's player that no frame of the window places is left to its frame's own panel", async () => {
  const frame = playerFrame();
  const { listeners, document, panel } = await loadContent({ frames: [frame] });

  placeIn(listeners, { contentWindow: {} }, "p2", { left: 0, top: 0, right: 640, bottom: 360 });
  const reply = await panel.framePlayer({ id: "p2", kind: "video", url: "https://player.example/embed/2" }, 10);

  assert.deepEqual(plain(reply), { ok: false });
  assert.equal(document.documentElement.children.length, 0);
});

test("a frame's player is not shown on a page in the exception list, nor offered back to its frame", async () => {
  const frame = playerFrame();
  const rules = { videoPanel: "full", videoPanelExceptionSites: "a.com" };
  const { listeners, document, panel } = await loadContent({ rules, frames: [frame] });

  placeIn(listeners, frame, "p3", { left: 0, top: 0, right: 640, bottom: 360 });
  const reply = await panel.framePlayer({ id: "p3", kind: "video", url: "https://player.example/embed/3" });

  assert.deepEqual(plain(reply), { ok: true });
  assert.equal(document.documentElement.children.length, 0);
});

const embedded = { left: 0, top: 0, right: 640, bottom: 360, width: 640, height: 360 };
const play = async (listeners, video) => {
  for (const fn of listeners.play) await fn({ target: video });
};

test("a frame hands its player to the top frame, posting where it is, and draws no panel of its own", async () => {
  const { listeners, sent, posts, document } = await loadContent({
    inFrame: true,
    href: "https://player.example/embed/1",
    answer: (m) => (m.cmd === "framePlayer" ? { ok: true } : undefined),
  });

  await play(listeners, new Video(embedded));

  const handed = plain(sent.find((m) => m.cmd === "framePlayer"));
  assert.deepEqual(handed.player, { id: handed.player.id, kind: "video", fedBySource: true, caption: "", duration: 60 });
  assert.deepEqual(plain(posts), [
    { data: { adm: "adm-player-rect", id: handed.player.id, rect: { left: 0, top: 0, right: 640, bottom: 360 } }, origin: "*" },
  ]);
  assert.equal(document.documentElement.children.length, 0);
});

test("the panel hides while the page shows its video full screen, and comes back when it leaves full screen", async () => {
  const { listeners, document } = await loadContent();
  await play(listeners, new Video({ left: 100, top: 300, right: 740, bottom: 660, width: 640, height: 360 }));
  const [host] = document.documentElement.children;
  const fullscreen = (element) => {
    document.fullscreenElement = element;
    for (const fn of listeners.fullscreenchange || []) fn({});
  };

  assert.equal(host.style.display, "block");
  fullscreen(new Node("div"));
  assert.equal(host.style.display, "none");
  fullscreen(null);
  assert.equal(host.style.display, "block");
});

test("a frame whose player the top frame doesn't take shows its own panel", async () => {
  const { listeners, document } = await loadContent({
    inFrame: true,
    href: "https://player.example/embed/1",
    answer: (m) => (m.cmd === "framePlayer" ? { ok: false } : undefined),
  });

  await play(listeners, new Video(embedded));

  const [host] = document.documentElement.children;
  assert.equal(host.style.display, "block");
});

test("a frame between passes a player on, placed in its own page", async () => {
  const frame = playerFrame();
  const { listeners, posts } = await loadContent({ inFrame: true, frames: [frame] });

  placeIn(listeners, frame, "p4", { left: 0, top: 0, right: 640, bottom: 360 });
  placeIn(listeners, frame, "p4", { left: 0, top: 0, right: 640, bottom: 360 });

  // Once: an unchanged place isn't posted again.
  assert.deepEqual(plain(posts.map((p) => p.data)), [{ adm: "adm-player-rect", id: "p4", rect: { left: 100, top: 300, right: 740, bottom: 660 } }]);
});

// --- The menu's look ---

test("a choice's size is shown apart from its name", async () => {
  const { panel } = await loadContent();

  assert.deepEqual(plain(panel.labelParts("1080p MP4 (145.0 MB)")), ["1080p MP4", "145.0 MB"]);
  assert.deepEqual(plain(panel.labelParts("Sunset (day 2)")), ["Sunset (day 2)", ""]);
  assert.deepEqual(plain(panel.labelParts("Original")), ["Original", ""]);
});

test("Finding qualities is marked as busy", async () => {
  const { panel } = await loadContent();

  const [finding] = plain(panel.menuItems([], { url: watch }));

  assert.equal(finding.busy, true);
});

test("missing tools add Set up video downloads", async () => {
  const { panel } = await loadContent();

  const some = plain(panel.menuItems([], { url: watch, reply: { ...choicesReply, missingTools: ["ffmpeg"] } }));
  assert.deepEqual(some.map((i) => i.label), ["Download all", "1080p MP4 (145.0 MB)", "Original", "Set up video downloads…"]);
  assert.equal(some[3].setup, true);

  const none = plain(panel.menuItems([], { url: watch, reply: { ok: false, error: "yt-dlp is needed", missingTools: ["yt-dlp"] } }));
  assert.deepEqual(none.map((i) => i.label), ["Set up video downloads…"]);
});

test("a site error is shown when there is nothing else", async () => {
  const { panel } = await loadContent();
  const refused = { ok: false, error: "[generic] Unsupported URL", missingTools: [] };

  const alone = plain(panel.menuItems([], { url: watch, reply: refused }));
  assert.deepEqual(alone.map((i) => [i.label, i.disabled]), [["[generic] Unsupported URL", true]]);

  assert.deepEqual(plain(panel.menuItems([sniffed], { url: watch, reply: refused })).map((i) => i.label), ["clip.mp4 (5.0 MB)"]);
});

test("one choice still downloads at once when the player isn't fed by Media Source", async () => {
  const { panel } = await loadContent();
  const one = panel.menuItems([sniffed], null);

  assert.equal(panel.downloadsAtOnce(one, null), true);
  assert.equal(panel.downloadsAtOnce(panel.menuItems([sniffed], { url: watch }), { url: watch }), false);
});

test("a joined choice is offered by its label", async () => {
  const { panel } = await loadContent();
  const joined = { url: "https://v.example/v.mp4", audioUrl: "https://v.example/a.mp4", kind: "joined", label: "Video, 479 kbps, with sound" };

  const list = plain(panel.choices([joined]));

  assert.deepEqual(list.map((c) => c.label), ["Video, 479 kbps, with sound"]);
  assert.equal(list[0].message.media.audioUrl, joined.audioUrl);
});

// --- Which video's files ---

// As on Facebook on 2026-11-19: the video shown (197.4 s long in its player, 197 in its files), a reel loaded beside it,
// a video seen earlier without its sound, and a file that names no video.
const reel = "1128943972873365";
const fbVideo = (videoId, durationS, label, kind = "joined") =>
  ({ url: `https://video.xx.fbcdn.net/o1/${videoId}.mp4`, kind, size: -1, label, videoId, durationS });
const shown = fbVideo(reel, 197, "Video, 1355 kbps, with sound");
const beside = fbVideo("1068015576299802", 260, "Video, 341 kbps, with sound");
const earlier = fbVideo("2030249154335733", 27, undefined, "http");
const all = [shown, beside, earlier, sniffed];

test("a player in the feed offers only the files as long as its video", async () => {
  const { panel } = await loadContent();

  assert.deepEqual(plain(panel.ofThisVideo(all, "https://www.facebook.com/", 197.4)), [shown, sniffed]);
  assert.deepEqual(plain(panel.ofThisVideo(all, "https://www.facebook.com/", 26.9)), plain([earlier, sniffed]));
});

test("a page whose address names its video offers only that video's files", async () => {
  const { panel } = await loadContent();

  for (const page of [
    `https://www.facebook.com/reel/${reel}`,
    `https://www.facebook.com/watch/?v=${reel}`,
    `https://www.facebook.com/SomePage/videos/a-title/${reel}/`,
  ])
    assert.deepEqual(plain(panel.ofThisVideo(all, page, NaN)), [shown, sniffed], page);
});

test("of two videos as long as the player's, the one the address names is offered", async () => {
  const { panel } = await loadContent();
  const twin = fbVideo("1445743753401008", 197, "Video, 456 kbps, with sound");

  assert.deepEqual(plain(panel.ofThisVideo([twin, shown], `https://www.facebook.com/reel/${reel}`, 197.4)), [shown]);
  assert.deepEqual(plain(panel.ofThisVideo([twin, shown], "https://www.facebook.com/", 197.4)), [twin, shown]);
});

test("a player whose length and page match no file offers every file", async () => {
  const { panel } = await loadContent();

  assert.equal(panel.ofThisVideo(all, "https://www.facebook.com/", 75).length, 4);
  assert.equal(panel.ofThisVideo(all, "https://www.facebook.com/reel/555", NaN).length, 4);
  assert.equal(panel.ofThisVideo(all, watch, undefined).length, 4);
});

// --- The video's name ---

// A player inside a feed post whose text the site marks, as Facebook's does.
function playerInPost(text) {
  const message = { innerText: text };
  const post = { querySelector: (selector) => (selector.includes("story_message") ? message : null) };
  return { closest: (selector) => (selector.includes('[role="article"]') ? post : null) };
}
const loosePlayer = { closest: () => null };

test("a player in a post is named by the post's first line, cut short at a word", async () => {
  const { panel } = await loadContent();

  assert.equal(panel.captionOf(playerInPost("Sunset at the beach 🌅\nSecond line"), "(3) Facebook", "www.facebook.com"), "Sunset at the beach 🌅");
  assert.equal(panel.captionOf(playerInPost("  A short   one … See more"), "", "www.facebook.com"), "A short one");
  assert.equal(
    panel.captionOf(playerInPost("We drove all the way up the mountain road to watch the sunrise over the valley"), "", "www.facebook.com"),
    "We drove all the way up the mountain road to watch the…"
  );
});

test("without a post's text, a player is named by the page's title without the site's name", async () => {
  const { panel } = await loadContent();

  assert.equal(panel.captionOf(loosePlayer, "(12) Sunset reel | By Some Page | Facebook", "www.facebook.com"), "Sunset reel");
  assert.equal(panel.captionOf(loosePlayer, "Spider-Man - Trailer - YouTube", "www.youtube.com"), "Spider-Man - Trailer");
  assert.equal(panel.captionOf(loosePlayer, "(3) Facebook", "www.facebook.com"), "");
});

test("files are listed by the video's name, and a file named by an id alone by the site", async () => {
  const { panel } = await loadContent({ href: "https://www.facebook.com/" });
  const hashed = {
    url: "https://video.fbcdn.net/v/t42/AQMRCHs3YlsImF0O0D4r7VzGVhKzTAsoIZ1UoJiev2Hl27B-UYDUr8fLNErxXy3bTS217asSrvD7mRaEojh3o0mddRTqW-9WQTrgk5QRkBpiYQ.mp4?_nc=1",
    kind: "http",
    type: "video/mp4",
    size: 1.1 * 1048576,
  };

  const named = plain(panel.choices([hashed], "Sunset reel"));
  assert.deepEqual([named[0].label, named[0].message.title], ["Sunset reel (1.1 MB)", "Sunset reel"]);

  const unnamed = plain(panel.choices([hashed, sniffed]));
  assert.deepEqual(unnamed.map((c) => c.label), ["Facebook video (1.1 MB)", "clip.mp4 (5.0 MB)"]);
  assert.deepEqual(unnamed.map((c) => c.message.title), ["Facebook video", undefined]);
});

test("the video's name goes with a site choice when the site gave none", async () => {
  const { panel } = await loadContent();

  const items = plain(panel.menuItems([], { url: watch, reply: { ...choicesReply, title: "" } }, "Sunset reel"));

  assert.equal(items.find((i) => i.message).message.site.title, "Sunset reel");
});

test("a page's video is read ahead once, and not for a player the page has since left", async () => {
  // So its qualities are ready when the menu opens, rather than read only then.
  const { sent, panel } = await loadContent();
  const other = "https://www.youtube.com/watch?v=other";

  panel.readAhead(watch, () => true, 0);
  panel.readAhead(watch, () => true, 0);
  panel.readAhead(other, () => false, 0);
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.deepEqual(plain(sent.filter((m) => m.cmd === "siteChoices")), [{ cmd: "siteChoices", url: watch }]);
});

test("a page's video is read as soon as its panel shows, not a moment later", async () => {
  const { sent, panel } = await loadContent();

  panel.readAhead(watch, () => true);
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.deepEqual(plain(sent.filter((m) => m.cmd === "siteChoices")), [{ cmd: "siteChoices", url: watch }]);
});

test("what a read ahead found is kept, so the menu opens on its qualities", async () => {
  const { panel } = await loadContent({ answer: (m) => (m.cmd === "siteChoices" ? choicesReply : undefined) });

  panel.readAhead(watch, () => true, 0);
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.deepEqual(plain(panel.knownAnswer(watch)), choicesReply);
  assert.equal(panel.knownAnswer("https://www.youtube.com/watch?v=other"), undefined);
});

test("a read that failed is not kept, and one kept goes stale after half an hour", async () => {
  const failed = { ok: false, error: "yt-dlp is missing", missingTools: ["yt-dlp"] };
  const { panel } = await loadContent({ answer: (m) => (m.url === watch ? failed : choicesReply) });
  const other = "https://www.youtube.com/watch?v=other";

  panel.readAhead(watch, () => true, 0);
  panel.readAhead(other, () => true, 0);
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.equal(panel.knownAnswer(watch), undefined);
  assert.ok(panel.knownAnswer(other, Date.now() + 29 * 60 * 1000));
  assert.equal(panel.knownAnswer(other, Date.now() + 31 * 60 * 1000), undefined);
});
