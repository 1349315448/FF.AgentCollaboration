import { request } from "../services/api";
import { byId, feedback } from "./dom.js";
import { openFiles } from "./files.js";
import { fitPaneToolbar, focusAgent, renderLayout, saveLayout, togglePrimary } from "./layout.js";
import { finishPicker, labelMentionButton, mentionChord, mentionPicker, openPeers, picking } from "./mentions.js";
import { activityLabels } from "../services/navigation";
import { showPreview } from "./preview.js";
import { loadQuota } from "./quota.js";
import { presentPane, mountTerminal, panes } from "../services/terminalRegistry";
import { openPaneSessions } from "./session-history.js";
import { openEngineLogin } from "./engine-login.js";
import { loadShortcuts } from "./shortcuts.js";
import { layout, mentionShortcut } from "../store/workbenchStore";
import { fitPane, replayReports, sendBytes, syncTerminal } from "./terminal.js";
import { refresh, refreshing } from "./workbench.js";

import { engineNames } from "../services/engines";
const bytes = new TextEncoder();
function makePane(agent) {
  const { root, element, term, fit, viewport, slot } = mountTerminal(byId("panes"));
  element.dataset.window = agent.id;
  element.dataset.engine = agent.engine;
  const surface = element.querySelector(".terminal-surface");
  surface.id = "terminal-" + agent.id;
  const collapse = element.querySelector(".toggle-collapse");
  collapse.setAttribute("aria-controls", surface.id);
  collapse.onclick = () => {
    if (layout.collapsedIds.includes(agent.id)) return focusAgent(agent.id);
    layout.collapsedIds.push(agent.id);
    saveLayout();
    renderLayout(pane);
    collapse.focus({preventScroll:true});
  };
  const actions = element.querySelector(".terminal-actions"), more = actions.querySelector(".pane-more");
  const moreMenu = actions.querySelector(".pane-more-menu");
  more.popoverTargetElement = moreMenu;
  const toolbarItems = Array.from(actions.children).filter(item => item !== more && item.matches("button, label"));
  const pane = { root, slot, agent, element, term, fit, surface, actions, more, moreMenu, toolbarItems,
    primaryButton:actions.querySelector(".set-primary"), generation:undefined, revision:0 };
  moreMenu.addEventListener("click", event => {
    if (event.target.closest("button:not(.shortcuts-toggle), input") && moreMenu.matches(":popover-open")) moreMenu.hidePopover();
  });
  pane.observer = new ResizeObserver(() => { fitPaneToolbar(pane); fitPane(pane); });
  pane.observer.observe(viewport);
  pane.observer.observe(actions);
  term.onData(text => {
    text = text.replace(replayReports, "");
    if (text) sendBytes(pane, bytes.encode(text));
  });
  term.onBinary(text => {
    const data = text.replace(replayReports, "");
    if (data) sendBytes(pane, Uint8Array.from(data, character => character.charCodeAt(0)));
  });
  term.attachCustomKeyEventHandler(event => {
    if (event.type !== "keydown" || event.isComposing || event.keyCode === 229 || !pane.view?.agent.online) return true;
    if (event.key === "Escape" && picking?.pane === pane && !mentionPicker.open) {
      if (term._core) term._core._keyDownSeen = false;
      finishPicker();
      event.preventDefault();
      return false;
    }
    const chord = mentionChord(event);
    if (chord === mentionShortcut) openPeers(pane);
    else if (chord === "Alt+Shift+F") openFiles(pane);
    else return true;
    if (term._core) term._core._keyDownSeen = false;
    event.preventDefault();
    return false;
  });

  labelMentionButton(pane);
  const mentionButton = element.querySelector(".insert-agent");
  mentionButton.addEventListener("mousedown", event => event.preventDefault());
  mentionButton.addEventListener("click", () => openPeers(pane));
  element.querySelector(".insert-file").addEventListener("click", () => openFiles(pane));
  const menu = element.querySelector(".shortcut-menu");
  element.querySelector(".shortcuts-toggle").popoverTargetElement = menu;
  menu.addEventListener("toggle", event => {
    if (event.newState === "open") loadShortcuts(pane, menu);
  });
  element.querySelector(".set-primary").addEventListener("click", () => togglePrimary(pane));
  element.querySelector(".pane-sessions").addEventListener("click", () => openPaneSessions(pane));
  element.querySelector(".rename-agent").onclick = () => {
    const dialog = byId("rename-dialog"), input = byId("rename-name");
    dialog.dataset.targetId = agent.id;
    input.value = pane.agent.name;
    feedback("rename-error");
    dialog.showModal();
    input.select();
  };
  element.querySelector(".pane-quota").addEventListener("click", () => loadQuota(agent.engine, true));
  const approval = element.querySelector(".pane-approval");
  approval.title = ["claude", "codebuddy"].includes(agent.engine) ? "切换到下一个原生审批模式（Shift+Tab）" : "打开当前 CLI 的审批模式选择器";
  approval.onclick = () => paneAction(pane, () =>
    request(windowRoute(pane) + "/approval", {method:"POST", body:{terminal:pane.generation}}));
  element.querySelector(".preview-inbox").addEventListener("click", () => showPreview(pane.message));
  const ignore = element.querySelector(".ack-inbox");
  ignore.onclick = async () => {
    const items = pane.view.inbox.filter(canIgnore);
    if (items.length < 2 && !canIgnore(pane.message)) return;
    ignore.disabled = true;
    try {
      const action = items.length > 1 ? "acknowledge-all" : pane.message.id + "/acknowledge";
      await request(windowRoute(pane) + "/inbox/" + action, {method:"POST"});
      await refreshing;
      await refresh();
    } catch (error) {
      pane.receiptErrorId = null;
      feedback(element.querySelector(".pane-error"), error.message);
    } finally { ignore.disabled = false; }
  };
  element.querySelector(".next-inbox").addEventListener("click", () => {
    const messages = pane.view.inbox;
    const current = messages.findIndex(item => item.id === pane.message?.id);
    pane.selectedMessage = messages[(current + 1) % messages.length].id;
    updatePane(pane, pane.view);
  });
  element.querySelector(".write-inbox").onclick = () => {
    const messageId = pane.message.id;
    return paneAction(pane, () => request(windowRoute(pane) + "/input", { method:"POST", body:{messageId} }), messageId);
  };
  const power = element.querySelector(".pane-power"), launcher = element.querySelector(".start-pane");
  async function changePower(start) {
    if (!pane.presentation || pane.presentation.powerAction || start === pane.view.agent.online) return;
    presentPane(pane, {...pane.presentation, powerAction:start ? "start" : "stop"});
    try {
      await paneAction(pane, async () => {
        try { await request(windowRoute(pane) + (start ? "/start" : "/stop"), {method:"POST"}); }
        catch (error) {
          if (!start || error.status !== 401) throw error;
          if (await openEngineLogin(pane.agent.engine, pane.agent.project)) openPaneSessions(pane);
          return;
        }
        await refreshing;
      });
    } finally {
      if (!panes.has(agent.id)) return;
      const origin = start && pane.view.agent.online ? launcher.getBoundingClientRect() : null;
      presentPane(pane, {...pane.presentation, powerAction:undefined});
      if (origin?.width && power.getClientRects().length) animatePowerOn(power, origin);
      if (pane.view && !pane.view.agent.online && !element.hidden && !surface.hidden && document.activeElement === document.body) {
        launcher.focus({preventScroll:true});
      }
    }
  }
  power.onclick = () => changePower(!pane.view.agent.online);
  launcher.onclick = () => changePower(true);
  element.querySelector(".close-pane").addEventListener("click", () => paneAction(pane, () =>
    request(windowRoute(pane), { method: "DELETE" })));
  element.querySelector(".auto-receive input").addEventListener("change", event => paneAction(pane, () =>
    request(windowRoute(pane) + "/auto-receive", { method: "PUT", body: { enabled: event.target.checked } })));
  return pane;
}

function windowRoute(pane) { return "/windows/" + pane.agent.id; }

function animatePowerOn(button, origin) {
  const end = button.getBoundingClientRect();
  const dx = origin.left + origin.width / 2 - end.left - end.width / 2;
  const dy = origin.top + origin.height / 2 - end.top - end.height / 2;
  button.classList.add("power-arriving");
  const motion = button.animate([
    {transform:`translate(${dx}px, ${dy}px) scale(${origin.width / end.width})`, color:"var(--muted)", backgroundColor:"var(--surface)", borderRadius:"50%", easing:"cubic-bezier(.4,0,.2,1)"},
    {offset:.65, transform:"scale(.80)", color:"var(--agent-green)", easing:"ease-out"},
    {offset:.80, transform:"scale(1.22)", easing:"ease-in-out"},
    {transform:"scale(1)"}
  ], {duration:620});
  motion.onfinish = motion.oncancel = () => button.classList.remove("power-arriving");
}
// [T09c] 实时输出和连接重放都可能触发多个终端查询回复。
function canIgnore(message) {
  return !message?.acknowledged && (message?.state === "pending" || message?.state === "delivery_uncertain");
}

function updatePane(pane, view) {
  syncTerminal(pane, view);
  if (pane.receiptErrorId && !view.inbox.some(item => item.id === pane.receiptErrorId)) {
    feedback(pane.element.querySelector(".pane-error"));
    pane.receiptErrorId = null;
  } else if (!view.inbox.some(item => item.state === "delivery_uncertain")) clearSubmitError(pane);
  const {agent, inbox, activity} = view;
  pane.element.dataset.activity = activity;
  pane.element.setAttribute("aria-label", "@" + agent.name + " 终端");
  const autoReceive = pane.element.querySelector(".auto-receive input");
  autoReceive.checked = agent.autoReceive;
  if (!agent.online) pane.element.querySelector(".shortcut-menu:popover-open")?.hidePopover();
  const message = inbox.find(item => item.id === pane.selectedMessage) || inbox.find(item => item.state === "pending") || inbox[0];
  pane.message = message;
  const prefix = message?.fromTopic ? "跨话题 · " + message.fromTopic + " · " : "";
  const label = message?.state === "delivery_uncertain" ? "送达待核对" : "消息";
  presentPane(pane, {
    name: agent.name, project: agent.project, engine: engineNames[agent.engine], online: agent.online, powerAction:pane.presentation?.powerAction,
    status: activity === "offline" ? "未启动" : activity === "waiting" ? "待处理" : activityLabels[activity],
    exitText: !agent.online && view.terminal ? "终端已退出" + (view.exitCode ? "（" + view.exitCode + "）" : "") : "",
    caption: message ? prefix + "来自 @" + message.from + " 的" + label + (inbox.length > 1 ? " · " + inbox.length + " 条" : "") : "",
    inboxState: message?.state, inboxCount: inbox.length, ignoreCount: inbox.filter(canIgnore).length, canIgnore: !!message && canIgnore(message)
  });
}

function clearSubmitError(pane) {
  const error = pane.element.querySelector(".pane-error");
  if (error.textContent.endsWith("请核对终端")) {
    feedback(error);
    pane.receiptErrorId = null;
  }
}

async function paneAction(pane, action, messageId = null) {
  pane.receiptErrorId = null;
  feedback(pane.element.querySelector(".pane-error"));
  const previousFocus = document.activeElement;
  try {
    await action();
    await refresh();
    const currentFocus = document.activeElement;
    if (!pane.surface.hidden && !pane.element.hidden && panes.has(pane.agent.id) && (currentFocus === previousFocus || currentFocus === document.body)) {
      if (pane.view.agent.online) pane.term.focus();
      else pane.element.querySelector(".start-pane").focus();
    }
  } catch (error) {
    pane.receiptErrorId = messageId;
    feedback(pane.element.querySelector(".pane-error"), error.message);
    await refresh();
  }
}

function insert(pane, text) {
  return request(windowRoute(pane) + "/input", { method: "POST", body: { text } });
}


export { bytes, canIgnore, clearSubmitError, engineNames, insert, makePane, paneAction, updatePane, windowRoute };
