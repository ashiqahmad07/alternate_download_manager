// The extension mirrors CaptureRules.ShouldCapture from ADM.Core. These cases match
// tests/ADM.Core.Tests/CaptureRulesTests.cs so the two copies cannot drift apart.
// Run with: node --test tests/extension/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

function loadBackground() {
  const noop = new Proxy(function () {}, { get: () => noop, apply: () => noop });
  const source = readFileSync(new URL("../../extension/background.js", import.meta.url), "utf8");
  const context = { chrome: noop, navigator: { userAgent: "test" }, URL, console };
  vm.createContext(context);
  vm.runInContext(source + "\n;globalThis.__exports = { shouldCapture, decide, DEFAULT_RULES };", context);
  return context.__exports;
}

const { shouldCapture, decide, DEFAULT_RULES } = loadBackground();
const rules = (overrides = {}) => ({ ...DEFAULT_RULES, ...overrides });

test("captures the listed file types", () => {
  assert.equal(shouldCapture(rules(), "https://a.com/f.zip", "f.zip", -1), true);
  assert.equal(shouldCapture(rules(), "https://a.com/F.ZIP", "F.ZIP", -1), true);
  assert.equal(shouldCapture(rules(), "https://a.com/movie.part.r01", "movie.part.r01", -1), true);
  assert.equal(shouldCapture(rules(), "https://a.com/page.html", "page.html", -1), false);
  assert.equal(shouldCapture(rules(), "https://a.com/noext", "noext", -1), false);
});

test("falls back to the url path when the name is unknown", () => {
  assert.equal(shouldCapture(rules(), "https://a.com/dl/setup.exe?x=1", "", -1), true);
});

test("uses the edited type list", () => {
  const r = rules({ extensions: ["epub"] });
  assert.equal(shouldCapture(r, "https://a.com/b.epub", "b.epub", -1), true);
  assert.equal(shouldCapture(r, "https://a.com/b.zip", "b.zip", -1), false);
});

test("skips exception sites and addresses", () => {
  const r = rules({ exceptionSites: "*.update.microsoft.com", exceptionAddresses: "https://a.com/keep/*" });
  assert.equal(shouldCapture(r, "https://dl.update.microsoft.com/kb.exe", "kb.exe", -1), false);
  assert.equal(shouldCapture(r, "https://a.com/keep/f.zip", "f.zip", -1), false);
  assert.equal(shouldCapture(r, "https://a.com/other/f.zip", "f.zip", -1), true);
});

test("skips files known to be smaller than the minimum", () => {
  const r = rules({ minSizeKB: 100 });
  assert.equal(shouldCapture(r, "https://a.com/f.zip", "f.zip", 50 * 1024), false);
  assert.equal(shouldCapture(r, "https://a.com/f.zip", "f.zip", 200 * 1024), true);
  assert.equal(shouldCapture(r, "https://a.com/f.zip", "f.zip", -1), true);
});

test("only http and https are captured", () => {
  for (const url of ["ftp://a.com/f.zip", "blob:https://a.com/1234", "data:application/zip;base64,AAAA"])
    assert.equal(shouldCapture(rules(), url, "f.zip", -1), false, url);
});

test("built-in defaults match ADM's CaptureRules defaults", () => {
  const cs = readFileSync(new URL("../../src/ADM.Core/CaptureRules.cs", import.meta.url), "utf8");
  const block = /DefaultExtensions =([\s\S]*?);/.exec(cs)[1];
  const extensions = [...block.matchAll(/"([^"]*)"/g)].map((m) => m[1]).join("");
  const sites = /DefaultExceptionSites = "([^"]*)"/.exec(cs)[1];
  assert.deepEqual([...DEFAULT_RULES.extensions], extensions.split(" ").filter(Boolean));
  assert.equal(DEFAULT_RULES.exceptionSites, sites);

  const models = readFileSync(new URL("../../src/ADM.Core/Models.cs", import.meta.url), "utf8");
  const keys = (name) => new RegExp(`${name} \\{ get; set; \\} = "([^"]*)"`).exec(models)[1].toLowerCase().split("+");
  assert.deepEqual([...DEFAULT_RULES.preventKeys], keys("PreventCaptureKeys"));
  assert.deepEqual([...DEFAULT_RULES.forceKeys], keys("ForceCaptureKeys"));
});

// decide() mirrors CaptureRules.Decide; these match the Decide cases in CaptureRulesTests.cs.
test("without special keys the capture rules decide", () => {
  assert.equal(decide(rules(), "https://a.com/f.zip", "f.zip", -1, []), true);
});

test("prevent keys skip a download of a listed type", () => {
  assert.equal(decide(rules(), "https://a.com/f.zip", "f.zip", -1, ["alt"]), false);
});

test("force keys take any type and exception sites", () => {
  const r = rules({ exceptionSites: "a.com", minSizeKB: 100 });
  assert.equal(decide(r, "https://b.com/page.html", "page.html", -1, ["insert"]), true);
  assert.equal(decide(r, "https://a.com/f.zip", "f.zip", 10, ["insert"]), true);
});

test("prevent wins when both are held", () => {
  assert.equal(decide(rules(), "https://a.com/f.zip", "f.zip", -1, ["alt", "insert"]), false);
});

test("force keys do not capture other schemes", () => {
  assert.equal(decide(rules(), "ftp://a.com/f.zip", "f.zip", -1, ["insert"]), false);
});
