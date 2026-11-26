import { createRoot } from "react-dom/client";
import { flushSync } from "react-dom";
import { App } from "./App";
import { openWorkbenchPage } from "./services/workbenchPage";
import "./styles/workbench.css";

void openWorkbenchPage(() => {
  flushSync(() => createRoot(document.getElementById("root")!).render(<App />));
  void import("./runtime/start");
}).catch(() => {
  document.body.removeAttribute("data-starting");
  document.getElementById("root")!.textContent = "无法连接工作台，请刷新页面重试。";
});
