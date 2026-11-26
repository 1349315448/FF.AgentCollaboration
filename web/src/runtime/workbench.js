import { animateMessages, attachLogControls } from "./activity.js";
import { request } from "../services/api";
import { pageRetired } from "../services/workbenchPage";
import { byId, feedback, setBusy } from "./dom.js";
import { attachLayoutControls, renderLayout } from "./layout.js";
import { applyMentionShortcut, picking, renderPeers } from "./mentions.js";
import { makePane, updatePane } from "./pane.js";
import { loadQuota } from "./quota.js";
import { panes, removeMissingWindows } from "../services/terminalRegistry";
import { isDialogOpen, selectedTopicId, setSnapshot, snapshot } from "../store/workbenchStore";
import { cachedPrompt, syncTerminal } from "./terminal.js";
import { attachTopicControls, renderTopics } from "./topics.js";

let refreshing;
let retrySnapshot;
window.addEventListener("ff:page-retired", () => {
  clearTimeout(retrySnapshot);
  removeMissingWindows(new Set());
  if (byId("engine-login-dialog")?.open) byId("engine-login-dialog").close();
}, {once:true});
function refresh() {
  if (pageRetired) return Promise.resolve();
  if (!refreshing) {
    const requestedTopic = selectedTopicId;
    const query = selectedTopicId ? "?topic=" + encodeURIComponent(selectedTopicId) + "&recent=30" : "";
    refreshing = request("/state" + query).then(data => {
      if (pageRetired || selectedTopicId !== requestedTopic) return;
      const previous = snapshot;
      const formOpen = isDialogOpen("new-dialog");
      const samePanes = data.windows.length === panes.size && data.windows.every(w => {
        const pane = panes.get(w.agent.id);
        return pane && pane.generation === w.terminal && pane.view.agent.online === w.agent.online;
      });
      setSnapshot(data);
      applyMentionShortcut(data.mentionShortcut);
      if (formOpen && samePanes) {
        // 表单遮住工作区时，继续处理 WebSocket、收件和提示符；关闭后一次绘制最新快照。
        for (const view of data.windows) syncTerminal(panes.get(view.agent.id), view);
      } else {
        normalizeActivities(data);
        render();
        if (picking) renderPeers();
        if (!formOpen) animateMessages(previous, data);
      }
      byId("connection").textContent = "已连接";
      byId("connection").title = "已连接";
      byId("connection").setAttribute("aria-label", "已连接");
      byId("connection").dataset.state = "online";
      feedback("connection-error");
      clearTimeout(retrySnapshot);
    }).catch(error => {
      if (pageRetired || selectedTopicId !== requestedTopic) return;
      byId("connection").textContent = "连接中断";
      byId("connection").title = "连接中断";
      byId("connection").setAttribute("aria-label", "连接中断");
      byId("connection").dataset.state = "offline";
      feedback("connection-error", error.message);
      clearTimeout(retrySnapshot);
      retrySnapshot = setTimeout(refresh, 3000);
    }).finally(() => { refreshing = undefined; if(selectedTopicId!==requestedTopic) void refresh(); });
  }
  return refreshing;
}

function normalizeActivities(data) {
  for (const view of data.windows) {
    const pane = panes.get(view.agent.id);
    if (!pane || pane.replaying || !view.agent.online || pane.socket?.readyState !== WebSocket.OPEN) continue;
    const screen = cachedPrompt(pane).screen;
    if (screen === "blocked") view.activity = "waiting";
    else if (screen === "running" || screen === "active") view.activity = "working";
    else if (screen === "clear" || screen === "draft" || screen === "idle") view.activity = "idle";
  }
  const byTopic = new Map(data.topics.map(t => [t.id, []]));
  for (const view of data.windows) byTopic.get(view.agent.topicId)?.push(view);
  for (const topic of data.topics) {
    if (topic.status === "finished") { topic.activity = "finished"; continue; }
    const list = byTopic.get(topic.id) || [];
    if (!list.length || list.every(w => !w.agent.online)) topic.activity = "offline";
    else if (list.some(w => w.activity === "working")) topic.activity = "working";
    else if (list.some(w => w.activity === "waiting" || w.inbox.some(m => m.state !== "delivered" && !(m.state === "delivery_uncertain" && m.acknowledged)))) topic.activity = "waiting";
    else topic.activity = "idle";
  }
}

function render() {
  const { windows } = snapshot;
  renderTopics();
  // A new engine needs its own terminal parser, key handler and native connection.
  const ids = new Set(windows.filter(({agent}) => !panes.has(agent.id) || panes.get(agent.id).agent.engine === agent.engine).map(view => view.agent.id));
  removeMissingWindows(ids);
  for (const view of windows) {
    if (!panes.has(view.agent.id)) {
      panes.set(view.agent.id, makePane(view.agent));
      loadQuota(view.agent.engine);
    }
    updatePane(panes.get(view.agent.id), view);
  }
  renderLayout();
}


document.querySelectorAll("[data-close]").forEach(button => button.addEventListener("click", () => button.closest("dialog").close()));
document.querySelectorAll("dialog").forEach(dialog => dialog.addEventListener("cancel", event => {
  if (dialog.dataset.busy === "true") event.preventDefault();
}));
function listenForChanges() {
  let scheduled = false;
  window.addEventListener("ff:state-changed", () => {
    if (scheduled) return;
    scheduled = true;
    setTimeout(async () => {
      // 事件若落在快照请求途中，再取一次，避免使用提交前的旧结果。
      await refreshing;
      scheduled = false;
      refresh();
    }, 70);
  });
}

function startWorkbench() {
  if (!document.body.hasAttribute("data-starting")) return;
  document.body.removeAttribute("data-starting");
  attachTopicControls();
  attachLogControls();
  attachLayoutControls();
  refresh();
  listenForChanges();
}

export { normalizeActivities, refresh, refreshing, render, startWorkbench };
