// parseMaster mirrors HlsMaster.Parse; these match tests/ADM.Core.Tests/HlsMasterTests.cs on the same fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { makeChrome, loadScript, plain } from "./browser-stub.mjs";

const { parseMaster } = loadScript("background.js", { chrome: makeChrome().chrome }, ["parseMaster"]);
const fixture = readFileSync(new URL("../fixtures/master.m3u8", import.meta.url), "utf8");
const master = plain(parseMaster(fixture, "https://a.com/v/master.m3u8"));

test("parses variants in playlist order without iframe streams", () => {
  assert.deepEqual(
    master.variants.map((v) => v.uri),
    ["https://a.com/v/360p.m3u8", "https://cdn.example.com/1080p.m3u8", "https://a.com/v/720p.m3u8", "https://a.com/v/audio-only.m3u8"]
  );
  const [first] = master.variants;
  assert.equal(first.bandwidth, 800000);
  assert.deepEqual([first.width, first.height], [640, 360]);
  assert.equal(first.codecs, "avc1.4d401e,mp4a.40.2");
  assert.equal(first.audioGroup, "aud");
  assert.equal(first.subtitleGroup, "subs");
  assert.equal(master.variants[2].subtitleGroup, null);
});

test("labels name the height or audio", () => {
  assert.deepEqual(master.variants.map((v) => v.label), ["360p", "1080p", "720p", "Audio"]);
  assert.equal(master.variants[3].audioOnly, true);
  assert.equal(master.variants[0].audioOnly, false);
});

test("parses audio and subtitle renditions", () => {
  assert.deepEqual(master.audio.map((a) => a.language), ["en", "de"]);
  assert.equal(master.audio[0].isDefault, true);
  assert.equal(master.audio[0].uri, "https://a.com/v/audio/en.m3u8");
  assert.equal(master.subtitles.length, 1);
  assert.equal(master.subtitles[0].name, "English");
});
