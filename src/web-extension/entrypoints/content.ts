export default defineContentScript({
  matches: ["http://*/*", "https://*/*"],
  allFrames: false,
  main() {
    let busy = false;
    window.setInterval(async () => {
      if (busy || !document.hasFocus() || document.visibilityState !== "visible") return;
      busy = true;
      try {
        const name = document.querySelector('meta[property="og:site_name"]')?.getAttribute("content") || document.querySelector('meta[name="application-name"]')?.getAttribute("content");
        await browser.runtime.sendMessage({ type: "activity", name: name?.slice(0,256) });
      } catch { /* Extension reload: next tick retries. */ }
      finally { busy = false; }
    }, 1500);
  },
});
