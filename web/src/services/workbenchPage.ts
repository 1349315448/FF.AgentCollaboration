export let pageId = "";
export let pageRetired = false;

export function pageSocketUrl(address: string | URL) {
  const url = new URL(address);
  url.searchParams.set("page", pageId);
  return url;
}

function retirePage() {
  if (pageRetired) return;
  pageRetired = true;
  const recovery = document.implementation.createHTMLDocument("工作台已在新页面打开");
  window.dispatchEvent(new CustomEvent("ff:recover-drafts", { detail: recovery }));
  window.dispatchEvent(new Event("ff:page-retired"));
  if (recovery.body.childElementCount) {
    // 仅把尚未保存的文字留给用户复制，不将其写回项目或上传给宿主。
    const heading = recovery.createElement("h1");
    heading.textContent = "工作台已在新页面打开。请复制本页未保存的修改。";
    recovery.body.prepend(heading);
    recovery.documentElement.lang = "zh-CN";
    const html = new Blob(["<!doctype html>", recovery.documentElement.outerHTML], { type:"text/html;charset=utf-8" });
    location.replace(URL.createObjectURL(html));
    return;
  }
  window.close();
  if (!window.closed) location.replace("/page-inactive.html");
}

export async function openWorkbenchPage(start: () => void) {
  const response = await fetch("/api/page", { method:"POST", headers:{"X-FF-Page":"open"}, cache:"no-store" });
  if (!response.ok) throw new Error("无法连接工作台，请刷新页面重试。");
  pageId = await response.json();
  let started = false;
  let events: EventSource;
  function listen() {
    events = new EventSource("/api/events?page=" + encodeURIComponent(pageId));
    events.onmessage = event => {
      if (event.data === "replaced") {
        events.close();
        retirePage();
      } else if (event.data === "state") {
        document.documentElement.inert = false;
        if (started) window.dispatchEvent(new Event("ff:state-changed"));
        else {
          started = true;
          start();
        }
      }
    };
    events.onerror = () => {
      const dot = document.getElementById("connection");
      if (dot) {
        dot.dataset.state = "offline";
        dot.title = "正在重新连接工作台";
        dot.setAttribute("aria-label", dot.title);
      } else if (!started) {
        document.getElementById("root")!.textContent = "正在重新连接工作台…";
      }
      // EventSource 会以原页面编号重连，不能在断线时重新认领。
    };
  }
  window.addEventListener("pagehide", () => events.close());
  window.addEventListener("pageshow", event => {
    if (event.persisted && !pageRetired) {
      document.documentElement.inert = true;
      listen();
    }
  });
  listen();
}
