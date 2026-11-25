import { request } from "../services/api";
import { engineNames } from "../services/engines";
import { byId } from "./dom.js";

export function checkAccount(engine, project, signal) {
  return request("/engines/" + engine + "/account?" + new URLSearchParams({project}), {signal});
}
export const accountReady = account => account.status === "authenticated" || account.status === "configured";

// 连接仅属于本次登录。退出弹窗时释放 ConPTY，调用方自行展示会话选择。
export function openEngineLogin(engine, project) {
  const dialog = byId("engine-login-dialog");
  if (dialog.open) return Promise.resolve(false);
  const viewport = byId("engine-login-terminal"), status = byId("engine-login-status"), verify = byId("verify-engine-login");
  byId("engine-login-title").textContent = engineNames[engine] + " 登录";
  status.textContent = "请在终端完成登录";
  verify.disabled = false;
  dialog.showModal();
  const term = new Terminal({fontSize:13, cursorBlink:true, scrollback:500, theme:{background:"#0a0a0d"}});
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(viewport);
  const url = new URL("/api/engines/" + engine + "/login", location.href);
  url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
  url.search = new URLSearchParams({project}).toString();
  let socket;
  const send = data => { if (socket?.readyState === WebSocket.OPEN) socket.send(data); };
  const resize = () => { fit.fit(); send(JSON.stringify({type:"resize",cols:term.cols,rows:term.rows})); };
  const observer = new ResizeObserver(resize);
  observer.observe(viewport);
  term.onData(data => send(new TextEncoder().encode(data)));
  let confirmed = false;
  const controller = new AbortController();
  async function verifyAccount() {
    if (!dialog.open || verify.disabled) return;
    verify.disabled = true;
    status.textContent = "正在检查登录状态…";
    try {
      const account = await checkAccount(engine, project, controller.signal);
      if (controller.signal.aborted) return;
      confirmed = accountReady(account);
      if (confirmed) dialog.close();
      else status.textContent = account.status === "signed-out" ? "尚未登录，可完成登录后再次检查" : "暂时无法确认登录状态，可重试";
    } catch (error) { if (!controller.signal.aborted) status.textContent = error.message; }
    finally { if (!controller.signal.aborted) verify.disabled = false; }
  }
  verify.onclick = verifyAccount;
  function connect(provider) {
    if (provider) url.searchParams.set("provider", provider);
    socket = new WebSocket(url);
    socket.binaryType = "arraybuffer";
    socket.onopen = () => { resize(); term.focus(); };
    socket.onmessage = event => { if (event.data instanceof ArrayBuffer) term.write(new Uint8Array(event.data, 8)); };
    socket.onclose = verifyAccount;
  }
  const providers = byId("zcode-login-providers");
  providers.hidden = engine !== "zcode";
  if (providers.hidden) connect();
  else {
    status.textContent = "选择账号所属平台";
    providers.onclick = event => {
      const choice = event.target.closest("button");
      if (!choice || socket) return;
      providers.hidden = true;
      status.textContent = "请在终端完成登录";
      connect(choice.value);
    };
  }
  return new Promise(resolve => dialog.addEventListener("close", () => {
    controller.abort();
    if (socket) { socket.onclose = null; socket.close(); }
    observer.disconnect();
    term.dispose();
    viewport.replaceChildren();
    resolve(confirmed);
  }, {once:true}));
}
