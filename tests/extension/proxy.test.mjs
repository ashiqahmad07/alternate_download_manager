// What the extension tells ADM about the browser's proxy. tests/fixtures/browser-proxy.json is shared with
// ProxyTests in ADM.Core.Tests, which expects BrowserProxy.Parse to understand every string made here.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { makeChrome, loadScript, plain } from "./browser-stub.mjs";

const fixture = JSON.parse(readFileSync(new URL("../fixtures/browser-proxy.json", import.meta.url), "utf8"));
const NAMES = ["browserProxyFor", "proxyInfoString", "linksMessage"];

function load(options = {}) {
  const stub = makeChrome(options);
  return { ...stub, ...loadScript("background.js", { chrome: stub.chrome, fetch: async () => ({ text: async () => "" }) }, NAMES) };
}

const { browserProxyFor, proxyInfoString } = load();
const downloads = (calls) => calls.native.filter((m) => m.type === "download");
const zip = { id: 7, url: "https://a.com/f.zip", filename: "f.zip", referrer: "https://a.com/", fileSize: -1 };
const fixedProxy = { mode: "fixed_servers", rules: { singleProxy: { scheme: "http", host: "p.local", port: 3128 } } };

for (const c of fixture.chrome)
  test(`Chrome: ${c.name}`, () => {
    assert.equal(browserProxyFor(c.config, c.url), c.proxy);
  });

for (const c of fixture.firefox)
  test(`Firefox: ${c.name}`, () => {
    assert.equal(proxyInfoString(c.info), c.proxy);
  });

test("A missing config or proxy info says nothing", () => {
  assert.equal(browserProxyFor(undefined, zip.url), "");
  assert.equal(proxyInfoString(undefined), "");
});

test("Chrome capture sends the browser's proxy with the download", async () => {
  const b = load({ proxyConfig: fixedProxy });

  await new Promise((resolve) => b.chrome.downloads.onDeterminingFilename.listeners[0](zip, resolve));

  assert.equal(downloads(b.calls)[0].browserProxy, "http://p.local:3128");
});

test("Chrome capture without a browser proxy sends none", async () => {
  const b = load({ proxyConfig: { mode: "system" } });

  await new Promise((resolve) => b.chrome.downloads.onDeterminingFilename.listeners[0](zip, resolve));

  assert.equal(downloads(b.calls)[0].browserProxy, "");
});

test("A failure to read the proxy settings doesn't stop the capture", async () => {
  const b = load({ proxyConfig: () => Promise.reject(new Error("no")) });

  await new Promise((resolve) => b.chrome.downloads.onDeterminingFilename.listeners[0](zip, resolve));

  assert.equal(downloads(b.calls)[0].browserProxy, "");
  assert.deepEqual(b.calls.cancel, [7]);
});

test("Firefox sends the proxy its request for the download used", async () => {
  const b = load({ firefox: true });

  await b.chrome.webRequest.onHeadersReceived.listeners[0]({
    tabId: -1,
    url: zip.url,
    responseHeaders: [],
    proxyInfo: { type: "socks", host: "s.local", port: 1080 },
  });
  await b.chrome.downloads.onCreated.fire(zip);

  assert.equal(downloads(b.calls)[0].browserProxy, "socks5://s.local:1080");
});

test("Firefox sends nothing for a request that went direct", async () => {
  const b = load({ firefox: true });

  await b.chrome.webRequest.onHeadersReceived.listeners[0]({ tabId: 1, url: zip.url, responseHeaders: [] });
  await b.chrome.downloads.onCreated.fire(zip);

  assert.equal(downloads(b.calls)[0].browserProxy, "");
});

test("All links message says the proxy for each origin", async () => {
  const b = load({
    proxyConfig: { mode: "fixed_servers", rules: { singleProxy: { scheme: "http", host: "p.local", port: 3128 }, bypassList: ["b.com"] } },
  });

  const message = await b.linksMessage(
    [
      { url: "https://a.com/1.zip", text: "", image: false },
      { url: "https://b.com/2.zip", text: "", image: false },
    ],
    "https://a.com/"
  );

  assert.deepEqual(plain(message.proxies), { "https://a.com": "http://p.local:3128" });
});
