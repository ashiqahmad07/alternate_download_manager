// A recording stand-in for the chrome.* APIs the extension uses, and a loader that runs an
// extension script against it in its own context.
import { readFileSync } from "node:fs";
import vm from "node:vm";

function event() {
  const listeners = [];
  return { listeners, addListener: (fn) => listeners.push(fn), fire: (...args) => Promise.all(listeners.map((fn) => fn(...args))) };
}

function storageArea() {
  const data = {};
  return {
    data,
    get: async (keys) => {
      if (keys == null) return { ...data };
      if (typeof keys === "string") keys = [keys];
      if (Array.isArray(keys)) return Object.fromEntries(keys.filter((k) => k in data).map((k) => [k, data[k]]));
      return Object.fromEntries(Object.entries(keys).map(([k, fallback]) => [k, k in data ? data[k] : fallback]));
    },
    set: async (items) => void Object.assign(data, items),
    remove: async (keys) => {
      for (const k of [].concat(keys)) delete data[k];
    },
  };
}

/**
 * @param firefox  leaves out downloads.onDeterminingFilename, as Firefox does
 * @param host     answers native messages; by default the app accepts downloads and has no config
 * @param cookies  the cookies for an address
 * @param tabMessage  answers tabs.sendMessage
 * @param proxyConfig  the value of proxy.settings, or a function giving it; Firefox has no such API here, as it lacks the permission
 */
export function makeChrome({
  firefox = false,
  host = (m) => (m.type === "config" ? { ok: false, error: "closed" } : { ok: true }),
  cookies = () => [],
  tabMessage = async () => [],
  proxyConfig = { mode: "system" },
} = {}) {
  const calls = { native: [], cancel: [], erase: [], menus: [], removeAll: 0, cookies: [], tabMessages: [], scripts: [] };
  const chrome = {
    runtime: {
      id: "adm-test",
      sendNativeMessage: async (_name, message) => {
        calls.native.push(message);
        return host(message);
      },
      onMessage: event(),
      onInstalled: event(),
      onStartup: event(),
    },
    downloads: {
      onCreated: event(),
      cancel: async (id) => void calls.cancel.push(id),
      erase: async (query) => void calls.erase.push(query),
    },
    storage: { local: storageArea(), session: storageArea(), onChanged: event() },
    cookies: {
      getAll: async ({ url }) => {
        calls.cookies.push(url);
        return cookies(url);
      },
    },
    contextMenus: {
      removeAll: async () => {
        calls.removeAll++;
        calls.menus.length = 0;
      },
      create: (item) => void calls.menus.push(item.id),
      onClicked: event(),
    },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
    tabs: {
      onUpdated: event(),
      onActivated: event(),
      onRemoved: event(),
      sendMessage: async (...args) => {
        calls.tabMessages.push(args);
        return tabMessage(...args);
      },
    },
    webRequest: { onHeadersReceived: event() },
    scripting: { executeScript: async (details) => void calls.scripts.push(details) },
  };
  if (!firefox) {
    chrome.downloads.onDeterminingFilename = event();
    chrome.proxy = {
      settings: { get: async () => ({ value: await (typeof proxyConfig === "function" ? proxyConfig() : proxyConfig) }) },
    };
  }
  return { chrome, calls };
}

/** Runs extension/<file> with the given globals and returns the named top-level bindings. */
export function loadScript(file, globals, names = []) {
  const source = readFileSync(new URL(`../../extension/${file}`, import.meta.url), "utf8");
  const context = { URL, console, navigator: { userAgent: "test" }, ...globals };
  vm.createContext(context);
  vm.runInContext(`${source}\n;globalThis.__exports = { ${names.join(", ")} };`, context);
  return context.__exports;
}

/** Copies a value made in the script's context into this one, so deepEqual compares plain data. */
export const plain = (value) => JSON.parse(JSON.stringify(value));
