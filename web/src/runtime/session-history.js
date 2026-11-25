import { request } from "../services/api";
import { engineCapabilities } from "./create.js";
import { byId, feedback, node, setBusy } from "./dom.js";
import { focusAgent } from "./layout.js";
import { engineNames, windowRoute } from "./pane.js";
import { panes } from "../services/terminalRegistry";
import { refresh } from "./workbench.js";

let paneSessionRequest;
let sessionPane;

let showArchivedSessions = false;
let deleteCandidate;

function cancelSessionDelete() {
  deleteCandidate = null;
  byId("session-delete-confirm").hidden = true;
}

function openPaneSessions(pane) {
  sessionPane = pane;
  showArchivedSessions = false;
  const dialog = byId("pane-session-dialog");
  byId("pane-session-title").textContent = "@" + pane.agent.name + " 的会话记录";
  const context = byId("pane-session-context");
  context.title = context.textContent = engineNames[pane.agent.engine] + " · " + pane.agent.project;
  dialog.showModal();
  byId("pane-session-new").hidden = false;
  loadDrawerHistory();
}

async function loadDrawerHistory() {
  paneSessionRequest?.abort();
  const controller = new AbortController();
  paneSessionRequest = controller;
  const {engine, project} = sessionPane.agent;
  const archived = showArchivedSessions;
  const list = byId("pane-history"), status = byId("pane-session-status");
  cancelSessionDelete();
  feedback("pane-session-error");
  list.replaceChildren();
  status.textContent = "正在读取…";
  for (const tab of byId("pane-session-dialog").querySelectorAll("[data-history]"))
    tab.setAttribute("aria-pressed", String((tab.dataset.history === "archived") === archived));
  try {
    const params = new URLSearchParams({project, archived, includeBound:true});
    const [sessions, capabilities] = await Promise.all([
      request("/sessions/" + engine + "?" + params, {signal:controller.signal}), engineCapabilities()
    ]);
    const actionsAllowed = capabilities[engine]?.sessionActions ?? ["new", "resume", "archive", "restore", "delete"];
    if (controller.signal.aborted) return;
    byId("pane-session-new").hidden = !actionsAllowed.includes("new");
    status.textContent = sessions.length ? "" : archived ? "暂无已归档会话" : "当前项目暂无历史会话";
    for (const session of sessions) {
      const row = node("li", "session-entry");
      const name = node("strong", "session-name", session.name);
      name.title = session.id;
      const date = new Date(session.updatedAt ?? "");
      const meta = [Number.isNaN(date.getTime()) ? null : date.toLocaleString(), session.mayBeInUse ? "已绑定或可能正在使用" : null];
      const actions = node("div", "session-actions");
      function add(label, action, style = "text-button") {
        if (!actionsAllowed.includes(action)) return;
        const button = node("button", style, label);
        button.type = "button";
        button.disabled = session.mayBeInUse;
        button.onclick = () => {
          if (action === "delete") {
            deleteCandidate = session;
            byId("session-delete-name").textContent = session.name === session.id ? session.id : session.name + "\n" + session.id;
            byId("session-delete-confirm").hidden = false;
            byId("cancel-session-delete").focus();
          } else runSessionAction(action, session);
        };
        actions.append(button);
      }
      if (!archived) add("切换", "resume", "button compact");
      add(archived ? "还原" : "归档", archived ? "restore" : "archive");
      add("删除", "delete", "text-button delete-session");
      row.append(name, node("small", "session-meta", meta.filter(Boolean).join(" · ")), actions);
      list.append(row);
    }
  } catch (error) {
    if (!controller.signal.aborted) {
      status.textContent = "";
      feedback("pane-session-error", error.message);
    }
  }
}

async function runSessionAction(action, session) {
  const dialog = byId("pane-session-dialog");
  if (!dialog.open || dialog.dataset.busy === "true") return;
  const pane = sessionPane;
  paneSessionRequest?.abort();
  cancelSessionDelete();
  feedback("pane-session-error");
  setBusy(dialog, true);
  const controls = dialog.querySelectorAll(".session-tools, .session-scroll, .dialog-footer");
  controls.forEach(element => element.inert = true);
  try {
    if (action === "new" || action === "resume") {
      await request(windowRoute(pane) + "/session", {method:"POST", body:{newSession:action === "new", session:session?.id}});
      dialog.close();
      await refresh();
      if (panes.has(pane.agent.id)) focusAgent(pane.agent.id);
    } else {
      await request(windowRoute(pane) + "/history/" + action, {method:"POST", body:{session:session.id, archived:showArchivedSessions}});
      await loadDrawerHistory();
    }
  } catch (error) {
    await loadDrawerHistory();
    feedback("pane-session-error", error.message);
  } finally {
    setBusy(dialog, false);
    controls.forEach(element => element.inert = false);
  }
}

for (const tab of byId("pane-session-dialog").querySelectorAll("[data-history]")) {
  tab.onclick = () => { showArchivedSessions = tab.dataset.history === "archived"; loadDrawerHistory(); };
}
byId("pane-session-dialog").addEventListener("close", () => { paneSessionRequest?.abort(); cancelSessionDelete(); });
byId("pane-session-reload").onclick = loadDrawerHistory;
byId("pane-session-new").onclick = () => runSessionAction("new");
byId("cancel-session-delete").onclick = cancelSessionDelete;
byId("confirm-session-delete").onclick = () => { if (deleteCandidate) runSessionAction("delete", deleteCandidate); };

export { openPaneSessions };
