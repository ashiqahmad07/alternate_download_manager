const ext = globalThis.browser ?? chrome;
const send = (message) => ext.runtime.sendMessage(message);
const status = document.getElementById("status");

function formatSize(bytes) {
  if (bytes < 0) return "";
  const units = ["B", "KB", "MB", "GB"];
  let i = 0;
  while (bytes >= 1024 && i < units.length - 1) {
    bytes /= 1024;
    i++;
  }
  return `${bytes.toFixed(i ? 1 : 0)} ${units[i]}`;
}

function describe(media) {
  if (media.kind === "hls") return "HLS stream";
  if (media.kind === "dash") return "DASH stream";
  return [media.type, formatSize(media.size)].filter(Boolean).join(" · ");
}

function showError(reply) {
  status.textContent = reply && !reply.ok ? `ADM: ${reply.error}` : "";
}

(async () => {
  const [tab] = await ext.tabs.query({ active: true, currentWindow: true });
  const { media, settings } = await send({ cmd: "list", tabId: tab.id });

  const enabled = document.getElementById("enabled");
  enabled.checked = settings.enabled;
  enabled.addEventListener("change", () => send({ cmd: "setEnabled", enabled: enabled.checked }));
  document.getElementById("open").addEventListener("click", async () => showError(await send({ cmd: "show" })));

  const list = document.getElementById("list");
  if (!media.length) {
    list.innerHTML = '<div class="empty">No video or audio detected on this page yet. Start playback, then reopen this popup.</div>';
    return;
  }
  for (const m of media) {
    const row = document.createElement("div");
    row.className = "item";
    const meta = document.createElement("div");
    meta.className = "meta";
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = decodeURIComponent(new URL(m.url).pathname.split("/").pop() || m.url);
    name.title = m.url;
    const sub = document.createElement("div");
    sub.className = "sub";
    sub.textContent = describe(m);
    meta.append(name, sub);

    const button = document.createElement("button");
    button.textContent = "Download";
    button.addEventListener("click", async () => {
      const reply = await send({ cmd: "download", media: m, tab: { url: tab.url, title: tab.title } });
      showError(reply);
      if (reply.ok) window.close();
    });
    row.append(meta, button);
    list.append(row);
  }
})();
