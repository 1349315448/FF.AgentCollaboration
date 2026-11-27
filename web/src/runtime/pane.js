import { request } from "../services/api";
import { chooseApproval, labelApproval } from "./approval.js";
import { framedInputBounds } from "./framedPrompt.js";
import { byId, feedback } from "./dom.js";
import { openFiles } from "./files.js";
import { fitPaneToolbar, focusAgent, isLayoutAnimating, renderLayout, saveLayout, togglePrimary } from "./layout.js";
import { finishPicker, labelMentionButton, mentionChord, mentionPicker, openPeers, picking } from "./mentions.js";
import { activityLabels } from "../services/navigation";
import { showPreview } from "./preview.js";
import { loadQuota } from "./quota.js";
import { presentPane, mountTerminal, panes } from "../services/terminalRegistry";
import { openPaneSessions } from "./session-history.js";
import { loadShortcuts } from "./shortcuts.js";
import { layout, mentionShortcut, selectedTopicId } from "../store/workbenchStore";
import { fitPane, replayReports, sendBytes, syncTerminal } from "./terminal.js";
import { refresh, refreshing } from "./workbench.js";

import { engineNames } from "../services/engines";
const bytes = new TextEncoder();
document.addEventListener("pointerdown", event => {
  const input = document.activeElement;
  if (input?.matches(".pane:is([data-engine=qwen],[data-engine=codebuddy],[data-engine=qoder]) .xterm-helper-textarea") &&
      !input.closest(".terminal-viewport").contains(event.target)) input.blur();
}, {capture:true});
function makePane(agent) {
  const { root, element, term, fit, viewport, slot } = mountTerminal(byId("panes"));
  element.dataset.window = agent.id;
  element.dataset.engine = agent.engine;
  const surface = element.querySelector(".terminal-surface");
  surface.addEventListener("focusin", () => {
    if (layout?.workspaceMode === "agent" && selectedTopicId === agent.topicId && layout.activeWindowId !== agent.id) {
      layout.activeWindowId = agent.id;
      saveLayout();
    }
  });
  surface.id = "terminal-" + agent.id;
  const collapse = element.querySelector(".toggle-collapse");
  collapse.setAttribute("aria-controls", surface.id);
  collapse.onclick = () => {
    if (layout.workspaceMode === "code" || layout.collapsedIds.includes(agent.id)) return focusAgent(agent.id);
    layout.collapsedIds.push(agent.id);
    saveLayout();
    renderLayout(pane);
    collapse.focus({preventScroll:true});
  };
  const actions = element.querySelector(".terminal-actions"), more = actions.querySelector(".pane-more");
  const moreMenu = actions.querySelector(".pane-more-menu");
  more.popoverTargetElement = moreMenu;
  const toolbarItems = Array.from(actions.children).filter(item => item !== more && item.matches("button, label, .receive-controls"));
  const pane = { root, slot, agent, element, term, fit, surface, actions, more, moreMenu, toolbarItems,
    primaryButton:actions.querySelector(".set-primary"), generation:undefined, revision:0 };
  const reading = surface.querySelector(".reading-toggle");
  const scrollableEngine = ["qwen","codebuddy","qoder"].includes(agent.engine);
  function setReading(active) {
    reading.setAttribute("aria-pressed", String(active));
    reading.textContent = active ? "返回输入" : "阅读";
  }
  function readHistory() { setReading(true); surface.focus({preventScroll:true}); }
  if (scrollableEngine) {
    viewport.addEventListener("click", event => {
      if (!pane.view?.agent.online || term.hasSelection() || term.modes.mouseTrackingMode !== "none") return;
      const screen = event.target.closest(".xterm-screen");
      if (!screen) return;
      const rect = screen.getBoundingClientRect(), buffer = term.buffer.active;
      const y = Math.floor((event.clientY - rect.top) * term.rows / rect.height) + buffer.viewportY - buffer.baseY;
      const lines = Array.from({length:term.rows}, (_,i)=>buffer.getLine(buffer.baseY+i)?.translateToString(true) || "");
      const {row,bottom,framed} = framedInputBounds(agent.engine, lines);
      // 原生选择菜单和文本拖选仍归 CLI。只把明确的历史输出与输入框分开。
      if (y < 0 || (framed && bottom > row && y < row - 1)) readHistory();
    });
  }
  function pageHistory(page) {
    if (term.buffer.active.type === "normal") term.scrollPages(page);
    else if (pane.agent.engine === "qwen") sendBytes(pane, bytes.encode(page < 0 ? "\x1b[5~" : "\x1b[6~"));
  }
  surface.querySelector(".terminal-paging").onclick = event => {
    const button = event.target.closest("button");
    if (!button || !pane.view?.agent.online) return;
    if (button === reading && reading.getAttribute("aria-pressed") === "true") { setReading(false); term.focus(); return; }
    readHistory();
    if (button.dataset.page) pageHistory(Number(button.dataset.page));
  };
  surface.addEventListener("focusout", event => {
    if (!event.relatedTarget?.closest?.(".terminal-paging")) setReading(false);
  });
  surface.addEventListener("keydown", event => {
    if (event.target !== surface || !scrollableEngine || event.isComposing) return;
    if (event.key === "F6" || event.key === "Enter") { setReading(false); term.focus(); }
    else if (event.key === "PageUp" || event.key === "PageDown") pageHistory(event.key === "PageUp" ? -1 : 1);
    else if (event.key === "ArrowUp" || event.key === "ArrowDown") {
      const up = event.key === "ArrowUp";
      if (term.buffer.active.type === "normal") term.scrollLines(up ? -1 : 1);
      else if (agent.engine === "qwen") sendBytes(pane, bytes.encode(up ? "\x1b[1;2A" : "\x1b[1;2B"));
    } else return;
    event.preventDefault();
    event.stopPropagation();
  });
  moreMenu.addEventListener("click", event => {
    if (event.target.closest("button:not(.shortcuts-toggle), input") && moreMenu.matches(":popover-open")) moreMenu.hidePopover();
  });
  pane.observer = new ResizeObserver(() => {
    if (isLayoutAnimating()) {
      pane.pendingFit = true;
      return;
    }
    if (pane.fitRaf) return;
    pane.fitRaf = requestAnimationFrame(() => {
      pane.fitRaf = 0;
      if (!panes.has(agent.id)) return;
      if (isLayoutAnimating()) {
        pane.pendingFit = true;
        return;
      }
      fitPaneToolbar(pane);
      fitPane(pane);
    });
  });
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
    if (scrollableEngine && event.key === "F6" && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
      readHistory();
      event.preventDefault();
      return false;
    }
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
  element.querySelector(".rename-agent").onclick = () => openPaneSessions(pane, "basic");
  element.querySelector(".pane-quota").addEventListener("click", () => loadQuota(agent.engine, true));
  const approval = element.querySelector(".pane-approval");
  approval.onclick = () => chooseApproval(pane).catch(error => feedback(element.querySelector(".pane-error"), error.message));
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
        await request(windowRoute(pane) + (start ? "/start" : "/stop"), {method:"POST"});
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
  element.querySelector(".receive-mode").addEventListener("change", event => paneAction(pane, () =>
    request(windowRoute(pane) + "/auto-receive", { method:"PUT", body:{mode:event.target.value} })));
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
  pane.element.querySelector(".mcp-restart").hidden = !view.mcpRestartRequired;
  labelApproval(pane);
  if (pane.receiptErrorId && !view.inbox.some(item => item.id === pane.receiptErrorId)) {
    feedback(pane.element.querySelector(".pane-error"));
    pane.receiptErrorId = null;
  } else if (!view.inbox.some(item => item.state === "delivery_uncertain")) clearSubmitError(pane);
  const {agent, inbox, activity} = view;
  pane.element.dataset.activity = activity;
  pane.element.setAttribute("aria-label", "@" + agent.name + " 终端");
  const autoReceive = pane.element.querySelector(".auto-receive input");
  autoReceive.checked = agent.autoReceive;
  const receiveMode = pane.element.querySelector(".receive-mode");
  receiveMode.value = agent.receiveMode ?? "queued";
  receiveMode.title = view.nativeInbox
    ? "优先向当前会话直投。首次连接未获确认时，兼容投递会等待输入框安全。"
    : "兼容投递：原生建议视为空框；已输入的草稿或审批会暂缓投递。";
  receiveMode.hidden = !agent.receiveMode;
  receiveMode.disabled = !agent.receiveMode || !agent.autoReceive;
  if (!agent.online) pane.element.querySelector(".shortcut-menu:popover-open")?.hidePopover();
  const message = inbox.find(item => item.id === pane.selectedMessage) || inbox.find(item => item.state === "pending") || inbox[0];
  pane.message = message;
  const prefix = message?.fromTopic ? "跨话题 · " + message.fromTopic + " · " : "";
  const label = message?.state === "delivery_uncertain" ? "送达待核对" : "消息";
  const receipt = message?.state !== "pending" || !agent.online ? "" : message.nativeOffer ? " · 等待会话领取" :
    !agent.autoReceive ? " · 自动接收已关闭" : !view.nativeInbox && !view.inputReady ? " · 等待终端可接收" : "";
  presentPane(pane, {
    id: agent.id, name: agent.name, project: agent.project, engine: engineNames[agent.engine], online: agent.online, isLeader: agent.isLeader,
    powerAction: pane.presentation?.powerAction,
    status: activity === "offline" ? "未启动" : activity === "waiting" ? "待处理" : activityLabels[activity],
    exitText: !agent.online ? view.wakeError || (view.terminal ? "终端已退出" + (view.exitCode ? "（" + view.exitCode + "）" : "") : "") : "",
    caption: message ? prefix + "来自 @" + message.from + " 的" + label + receipt + (inbox.length > 1 ? " · " + inbox.length + " 条" : "") : "",
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
    if (!pane.surface.hidden && !pane.element.hidden && panes.has(pane.agent.id) && currentFocus === previousFocus) {
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
