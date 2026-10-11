import { connectionStatusStorage, type ConnectionStatus } from "@/utils/connection-status.ts";
const baseUrl = "http://127.0.0.1:5124";
const reportPath = "/api/screen-time/tracking/track-website-usage/record-activity";
export default defineBackground(() => {
  const action = browser.action ?? browser.browserAction;
  let token: string | null = null;
  let busy = false;
  let lastReport = 0;
  const metadataAttempts = new Map<string, number>();
  const ready = (async () => {
    // Chromium exposes this switch; Firefox storage is unavailable to our content script.
    const area = browser.storage.local as typeof browser.storage.local & { setAccessLevel?: (x: { accessLevel: "TRUSTED_CONTEXTS" }) => Promise<void> };
    await area.setAccessLevel?.({ accessLevel: "TRUSTED_CONTEXTS" });
    const saved = (await browser.storage.local.get("extensionToken")).extensionToken;
    token = typeof saved === "string" ? saved : null;
    await status(token ? "disconnected" : "unpaired");
  })();
  async function status(value: ConnectionStatus) {
    await connectionStatusStorage.setValue(value);
    await action.setTitle({ title: `STT: ${value}` });
    await action.setIcon({ path: Object.fromEntries([16,32,48,128].map(n => [n, `/icons/${value === "connected" ? "connected" : "disconnected"}-${n}.png`])) });
  }
  async function request(path: string, body?: unknown) {
    const response = await fetch(baseUrl + path, { method: body === undefined ? "GET" : "POST", credentials: "omit", redirect: "error", signal: AbortSignal.timeout(5000), headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) {
      await status(response.status === 401 || response.status === 403 ? "expired" : "http-error");
      throw new Error("Local API rejected request");
    }
    await status("connected");
    return response;
  }
  action.onClicked.addListener(() => browser.runtime.openOptionsPage());
  browser.runtime.onInstalled.addListener(async () => {
    await browser.contextMenus.removeAll();
    browser.contextMenus.create({ id: "open-settings", title: "Pair / Settings", contexts: ["action"] });
  });
  browser.contextMenus.onClicked.addListener(() => browser.runtime.openOptionsPage());
  browser.runtime.onMessage.addListener(async (message: unknown, sender) => {
    await ready;
    if (!message || typeof message !== "object") return;
    const msg = message as { type?: string; code?: string; baseUrl?: string; name?: string };
    if (msg.type === "pair") {
      // Only the packaged options page may redeem a code. Never return the token.
      if (sender.id !== browser.runtime.id || sender.url !== browser.runtime.getURL("/options.html")) return { ok: false };
      if (msg.baseUrl !== baseUrl || typeof msg.code !== "string" || !/^[0-9a-f]{16}$/i.test(msg.code)) return { ok: false, error: "Invalid local address or pairing code" };
      try {
        const r = await fetch(baseUrl + "/api/auth/pair", { method: "POST", credentials: "omit", redirect: "error", signal: AbortSignal.timeout(5000), headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code: msg.code }) });
        if (!r.ok) { await status("unpaired"); return { ok: false, error: "Pairing code rejected or expired" }; }
        token = (await r.json()).token;
        await browser.storage.local.set({ extensionToken: token });
        await status("connected"); return { ok: true };
      } catch { await status("disconnected"); return { ok: false, error: "Local service unavailable" }; }
    }
    if (msg.type !== "activity" || sender.id !== browser.runtime.id || sender.frameId !== 0 || sender.tab?.id === undefined || busy) return;
    if (!token) { await status("unpaired"); return; }
    busy = true;
    try {
      const tab = await browser.tabs.get(sender.tab.id);
      const window = await browser.windows.get(tab.windowId);
      const active = await browser.tabs.query({ active: true, windowId: tab.windowId });
      if (!window.focused || !tab.active || active[0]?.id !== tab.id || !tab.url || sender.url !== tab.url) return;
      const url = new URL(tab.url);
      if (url.protocol !== "https:" && url.protocol !== "http:") return;
      const now = performance.now();
      const elapsed = lastReport ? now - lastReport : 1500;
      if (elapsed < 250) return;
      lastReport = now;
      const name = typeof msg.name === "string" && msg.name.trim() ? msg.name.trim().slice(0,256) : url.host;
      await request(reportPath, { host: url.host, name, durationMilliseconds: Math.min(1500, Math.floor(elapsed)) });
      // URL and favicon come from browser APIs, not the content message.
      if (tab.favIconUrl && (!metadataAttempts.has(url.host) || now - metadataAttempts.get(url.host)! >= 30*60*1000)) {
        metadataAttempts.set(url.host, now);
        if (metadataAttempts.size > 512) metadataAttempts.delete(metadataAttempts.keys().next().value!);
        const favicon = new URL(tab.favIconUrl);
        if (favicon.origin !== url.origin) return;
        const image = await fetch(favicon.href, { credentials: "omit", redirect: "error", signal: AbortSignal.timeout(3000) });
        if (!image.ok || Number(image.headers.get("content-length")) > 2*1024*1024) return;
        const reader = image.body?.getReader(); if (!reader) return;
        const chunks: Uint8Array[] = []; let total = 0;
        for (;;) { const part = await reader.read(); if (part.done) break; total += part.value.length; if (total > 2*1024*1024) { await reader.cancel(); return; } chunks.push(part.value); }
        const bytes = new Uint8Array(total); let offset = 0;
        for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
        const png = bytes[0] === 137 && bytes[1] === 80;
        const jpeg = bytes[0] === 255 && bytes[1] === 216;
        const ico = bytes[0] === 0 && bytes[1] === 0 && bytes[2] === 1 && bytes[3] === 0;
        if (!png && !jpeg && !ico) return;
        let binary = ""; for (const byte of bytes) binary += String.fromCharCode(byte);
        const site = await (await request(`/api/screen-time/websites/by-host/${encodeURIComponent(url.host)}`)).json();
        if (site.allowMetadataAutoRefresh !== true || typeof site.id !== "string" || !/^[0-9a-f-]{36}$/i.test(site.id)) return;
        await request(`/api/screen-time/websites/${site.id}/refresh-metadata`, { icon: { extension: png ? "png" : jpeg ? "jpg" : "ico", data: btoa(binary) } });
      }
    } catch (error) {
      if (error instanceof TypeError || (error instanceof DOMException && error.name === "TimeoutError")) await status("disconnected");
    } finally { busy = false; }
  });
});
