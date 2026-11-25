// Chrome/Edge and Firefox load the same scripts with their own manifests; these must not drift apart.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

const read =(name) => JSON.parse(readFileSync(new URL(`../../extension/${name}`, import.meta.url), "utf8"));
const chrome = read("manifest.json");
const firefox = read("manifest.firefox.json");

test("both manifests describe the same extension", () => {
  for (const field of ["manifest_version", "name", "description", "version", "icons", "action", "host_permissions", "content_scripts"])
    assert.deepEqual(firefox[field], chrome[field], field);
  // Firefox reports a request's proxy on the request itself, so it doesn't ask for the proxy permission.
  assert.deepEqual([...firefox.permissions, "proxy"].sort(), [...chrome.permissions].sort());
});

test("each icon a manifest names is there", () => {
  // Chrome won't load an extension with a missing icon. They sit beside the manifest because install.ps1 copies no subfolders for Firefox.
  for (const file of [...Object.values(chrome.icons), ...Object.values(chrome.action.default_icon)])
    assert.ok(existsSync(new URL(`../../extension/${file}`, import.meta.url)), file);
});

test("each browser gets the background it supports", () => {
  assert.deepEqual(chrome.background, { service_worker: "background.js" });
  assert.deepEqual(firefox.background, { scripts: ["background.js"] });
});

test("Firefox has a fixed add-on id for the native host to allow", () => {
  // install.ps1 reads this id into the host's allowed_extensions.
  assert.match(firefox.browser_specific_settings.gecko.id, /^[a-z0-9-._]+@[a-z0-9-._]+$/i);
  assert.equal(firefox.key, undefined, "the key pins Chrome's id only");
});

test("Firefox is told what the add-on collects", () => {
  // Mozilla's store refuses a new add-on without it. What the add-on reads goes only to ADM, on the same machine.
  assert.deepEqual(firefox.browser_specific_settings.gecko.data_collection_permissions, { required: ["none"] });
});

test("each script reaches the browser through browser, else chrome", () => {
  // Firefox's chrome.* is there to help porting, and isn't promised to return promises as browser.* does.
  for (const name of ["background.js", "content.js", "popup.js"]) {
    const source = readFileSync(new URL(`../../extension/${name}`, import.meta.url), "utf8");
    assert.match(source, /globalThis\.browser \?\? chrome/, name);
    assert.doesNotMatch(source, /\bchrome\.[a-z]/, name);
  }
});
