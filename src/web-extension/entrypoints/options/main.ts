export {};
const code = document.getElementById("code") as HTMLInputElement;
const server = document.getElementById("baseUrl") as HTMLInputElement;
const statusElement = document.getElementById("status")!;
document.getElementById("pair")!.addEventListener("click", async () => {
  if (server.value !== "http://127.0.0.1:5124") { statusElement.textContent = "Only the fixed local server is supported"; return; }
  try {
    const result = await browser.runtime.sendMessage({ type: "pair", code: code.value.trim(), baseUrl: server.value });
    code.value = "";
    statusElement.textContent = result?.ok ? "Paired / 已配对" : result?.error || "Pairing failed / 配对失败";
  } catch { statusElement.textContent = "Extension unavailable / 扩展不可用"; }
});
