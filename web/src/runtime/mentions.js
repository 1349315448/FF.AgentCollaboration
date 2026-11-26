import { request } from "../services/api";
import { byId, feedback, node } from "./dom.js";
import { fitPaneToolbar } from "./layout.js";
import { windowRoute } from "./pane.js";
import { panes } from "../services/terminalRegistry";
import { isDialogOpen, mentionShortcut, setMentionShortcut, snapshot } from "../store/workbenchStore";
import { sendControl } from "./terminal.js";
import { refresh } from "./workbench.js";
import { remoteOnline } from "../services/navigation";

const mentionPicker = byId("agents-dialog");
const peerSearch = byId("peer-search");
const peerOptions = byId("agent-options");
const peerStatus = byId("peer-status");
const defaultMentionKey = "Alt+Shift+A";
let picking;
let pickerSequence = 0;
let mentionNoticeTimer;

function mentionChord(event) {
  if (event.metaKey || event.getModifierState("AltGraph") || !(event.ctrlKey || event.altKey)) return null;
  const letter = /^(?:Key([A-Z])|Digit([0-9]))$/.exec(event.code);
  if (!letter) return null;
  return [event.ctrlKey && "Ctrl", event.altKey && "Alt", event.shiftKey && "Shift", letter[1] || letter[2]].filter(Boolean).join("+");
}

function labelMentionButton(pane) {
  const button = pane.element.querySelector(".insert-agent");
  button.querySelector(".mention-key").textContent = "(" + mentionShortcut + ")";
  button.title = "指定在线 Agent（" + mentionShortcut + "）";
  button.setAttribute("aria-keyshortcuts", mentionShortcut.replace("Ctrl", "Control"));
}

function applyMentionShortcut(value) {
  if ((value || defaultMentionKey) === mentionShortcut) return;
  setMentionShortcut(value || defaultMentionKey);
  for (const pane of panes.values()) {
    labelMentionButton(pane);
    pane.toolbarWidth = null;
    fitPaneToolbar(pane);
  }
}

function positionPeers() {
  if (!picking || !mentionPicker.open) return;
  const {pane, screen} = picking;
  const rect = screen.getBoundingClientRect(), term = pane.term, buffer = term.buffer.active;
  const row = Math.max(0, Math.min(term.rows - 1, buffer.baseY + buffer.cursorY - buffer.viewportY));
  const cellHeight = rect.height / term.rows, margin = 6;
  const x = rect.left + buffer.cursorX * rect.width / term.cols;
  const y = Math.max(margin, Math.min(innerHeight - margin, rect.top + row * cellHeight));
  const above = y - 2 * margin, below = innerHeight - y - cellHeight - 2 * margin;
  const desired = peerSearch.offsetHeight + Math.min(190, peerOptions.scrollHeight) + peerStatus.offsetHeight + 28;
  const putAbove = above >= desired || above >= below;
  mentionPicker.style.maxHeight = Math.max(0, putAbove ? above : below) + "px";
  mentionPicker.style.left = Math.max(margin, Math.min(x, innerWidth - margin - mentionPicker.offsetWidth)) + "px";
  mentionPicker.style.top = Math.max(margin, putAbove ? y - margin - mentionPicker.offsetHeight : y + cellHeight + margin) + "px";
}

function closePeerPicker() {
  if (!mentionPicker.open) return;
  if (document.activeElement instanceof HTMLElement && mentionPicker.contains(document.activeElement)) {
    document.activeElement.blur();
  }
  mentionPicker.close();
}

function focusTerminalInput(pane) {
  const core = pane.term._core;
  if (core) {
    core._keyDownSeen = false;
    core._keyPressHandled = false;
    core._unprocessedDeadKey = false;
  }
  // 强制刷新模态层移除后的样式树，确保 textarea 在非 inert 状态下重新建立输入法上下文。
  void document.body.offsetHeight;
  if (document.activeElement === pane.term.textarea) pane.term.blur();
  core?._syncTextArea?.();
  pane.term.focus();
  requestAnimationFrame(() => {
    if (picking || isDialogOpen() || !pane.view?.agent.online || pane.element.hidden || pane.surface.hidden) return;
    if (core) core._keyDownSeen = false;
    core?._syncTextArea?.();
    if (document.activeElement !== pane.term.textarea) pane.term.focus();
  });
}

function matchesPeer(text, query) {
  let i = 0;
  for (const character of text.toLocaleLowerCase()) {
    if (character !== query[i]) continue;
    if (++i === query.length) return true;
  }
  return !query.length;
}

function selectPeer(button, scroll = false) {
  peerOptions.querySelector('[aria-selected="true"]')?.setAttribute("aria-selected", "false");
  picking.selected = button || null;
  if (!button) { peerSearch.removeAttribute("aria-activedescendant"); return; }
  button.setAttribute("aria-selected", "true");
  peerSearch.setAttribute("aria-activedescendant", button.id);
  if (scroll) button.scrollIntoView({block:"nearest"});
}

function renderPeers(reset = false) {
  const state = picking;
  if (!state) return;
  const {pane} = state;
  if (!pane.view.agent.online || pane.socket !== state.socket || pane.socket.readyState !== WebSocket.OPEN ||
      pane.generation !== state.generation || panes.get(pane.agent.id) !== pane || pane.element.hidden || pane.surface.hidden) {
    cancelPeerSource();
    return;
  }
  if (!mentionPicker.open) return;
  const query = [...peerSearch.value.trim().toLocaleLowerCase().replace(/^@/, "")];
  const topics = new Map(snapshot.topics.map(t => [t.id, t.name]));
  const locals = snapshot.windows.filter(w => w.agent.online && w.agent.id !== pane.agent.id).map(({agent,terminal}) => {
    const topic = topics.get(agent.topicId) ?? "", same = agent.topicId === pane.agent.topicId;
    return {id:agent.id, terminal, name:agent.name, title:agent.sessionTitle || "", topic, same,
      address:"@" + (same ? "" : topic + "/") + agent.name};
  });
  const remotes = (snapshot.federation?.remotes ?? []).filter(r => r.topicId === pane.agent.topicId && remoteOnline(r))
    .map(r => ({id:r.id, name:r.agentName, title:r.nodeName, topic:topics.get(r.topicId) ?? "", same:true,
      address:"@" + r.agentName, label:"外部 · " + r.nodeName}));
  const rows = [...locals, ...remotes].filter(row => [row.name,row.title,row.topic,row.address].some(field => matchesPeer(field, query)))
    .sort((a,b) => Number(b.same) - Number(a.same));
  const signature = JSON.stringify(rows);
  if (!reset && state.signature === signature) return;
  state.signature = signature;
  const previous = state.selected;
  peerOptions.replaceChildren(...rows.map(row => {
    const button = node("button", "mention-choice");
    button.type = "button";
    button.id = "peer-" + row.id;
    button.tabIndex = -1;
    button.dataset.id = row.id;
    if (row.terminal) button.dataset.terminal = row.terminal;
    button.setAttribute("role", "option");
    button.setAttribute("aria-selected", "false");
    button.append(node("strong", "", "@" + row.name), node("small", "", row.label ?? row.topic));
    button.title = [row.address, row.title].filter(Boolean).join(" · ");
    button.onmousedown = event => event.preventDefault();
    button.onclick = () => confirmPeer(button);
    return button;
  }));
  const keep = previous && [...peerOptions.children].find(b => b.id === previous.id && b.dataset.terminal === previous.dataset.terminal);
  if (!reset && previous && !keep) state.reselect = true;
  if (reset) state.reselect = false;
  selectPeer(!reset && keep || (!state.reselect && peerOptions.firstElementChild));
  peerStatus.textContent = state.reselect ? "上次选择的 Agent 已离线或切换会话，请重新选择。" : rows.length ? "" : "没有匹配的在线 Agent";
  positionPeers();
}

function openPeers(pane) {
  if (picking || !pane.view?.agent.online || !pane.socket || pane.socket.readyState > WebSocket.OPEN) return;
  if (!snapshot.mentionShortcut) {
    feedback(pane.element.querySelector(".pane-error"), "请在下次启动新版工作台后使用在线 Agent 选择器。");
    return;
  }
  const screen = pane.element.querySelector(".xterm-screen");
  if (!screen) return;
  const state = {pane, socket:pane.socket, generation:pane.generation, screen, request:++pickerSequence};
  picking = state;
  state.disconnect = () => { if (picking === state) cancelPeerSource(); };
  state.socket.addEventListener("close", state.disconnect);
  pane.element.querySelector(".insert-agent").setAttribute("aria-busy","true");
  // A second browser can observe the shared ready prompt before its own socket opens.
  state.onOpen = () => {
    if (picking === state && pane.socket === state.socket) sendControl(pane, {type:"mention-picker", open:true, request:state.request});
  };
  if (state.socket.readyState === WebSocket.OPEN) state.onOpen();
  else state.socket.addEventListener("open", state.onOpen, {once:true});
}

function cancelPeerSource() {
  const {pane} = picking;
  clearTimeout(mentionNoticeTimer);
  feedback("mention-notice", "已取消 @" + pane.agent.name + " 的选择，来源窗格已关闭、切换或离开当前视图。");
  mentionNoticeTimer = setTimeout(() => feedback("mention-notice"), 6000);
  if (mentionPicker.open) closePeerPicker();
  else finishPicker();
}

function showPeers(pane, request) {
  const state = picking;
  if (!state || state.request !== request || state.pane !== pane || state.socket !== pane.socket || mentionPicker.open) return;
  if (isDialogOpen() || pane.element.hidden || pane.surface.hidden) { finishPicker(); return; }
  const {screen} = state;
  pane.element.querySelector(".insert-agent").removeAttribute("aria-busy");
  peerSearch.value = "";
  if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  mentionPicker.show();
  peerSearch.focus({preventScroll:true});
  renderPeers(true);
  state.resize = new ResizeObserver(positionPeers);
  state.resize.observe(screen);
  state.cursor = pane.term.onCursorMove(positionPeers);
  state.scroll = pane.term.onScroll(positionPeers);
}

async function confirmPeer(button) {
  const state = picking;
  if (!state || !button || state.sending || !peerOptions.contains(button)) return;
  const {pane, generation} = state, {id,terminal} = button.dataset;
  state.sending = true;
  try {
    await request(windowRoute(pane) + "/mention", {method:"POST",
      body:{sourceTerminal:generation, targetId:id, targetTerminal:terminal}});
    if (picking === state) {
      closePeerPicker();
      // 原生 close 事件延迟至后续帧；确认选人后立即恢复终端，接住紧随其后的输入法事件。
      finishPicker();
    }
  } catch (error) {
    await refresh();
    if (picking === state) { peerStatus.textContent = error.message; positionPeers(); }
  } finally { state.sending = false; }
}

peerSearch.addEventListener("input", () => renderPeers(true));
peerSearch.addEventListener("compositionstart", () => { if (picking) picking.composing = true; });
peerSearch.addEventListener("compositionend", () => { if (picking) picking.composing = false; });
peerSearch.addEventListener("keydown", event => {
  if (!picking || picking.composing || event.isComposing || event.keyCode === 229) return;
  if (event.key === "Escape") {
    event.preventDefault();
    if (!picking.sending) closePeerPicker();
    return;
  }
  if (event.key === "Enter") { event.preventDefault(); confirmPeer(picking.selected); }
  if (event.key === "ArrowUp" || event.key === "ArrowDown") {
    event.preventDefault();
    const options = [...peerOptions.children], i = options.indexOf(picking.selected);
    if (!options.length) return;
    const next = i < 0 ? (event.key === "ArrowDown" ? 0 : options.length - 1) : (i + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length;
    picking.reselect = false;
    peerStatus.textContent = "";
    selectPeer(options[next], true);
  }
});
mentionPicker.addEventListener("mousedown", event => {
  if (event.target !== peerSearch) event.preventDefault();
});
document.addEventListener("pointerdown", event => {
  if (!mentionPicker.open || mentionPicker.contains(event.target) || picking?.sending) return;
  closePeerPicker();
}, true);
mentionPicker.addEventListener("cancel", event => { if (picking?.composing || picking?.sending) event.preventDefault(); });
function finishPicker() {
  const state = picking;
  if (!state || mentionPicker.open) return;
  picking = null;
  state.pane.element.querySelector(".insert-agent").removeAttribute("aria-busy");
  state.resize?.disconnect();
  state.cursor?.dispose();
  state.scroll?.dispose();
  state.socket.removeEventListener("open", state.onOpen);
  state.socket.removeEventListener("close", state.disconnect);
  if (state.socket.readyState === WebSocket.OPEN) state.socket.send(JSON.stringify({type:"mention-picker",open:false}));
  const {pane} = state;
  if (pane.generation === state.generation && pane.view.agent.online && !pane.element.hidden && !pane.surface.hidden &&
      !isDialogOpen()) focusTerminalInput(pane);
}
mentionPicker.addEventListener("close", finishPicker);
window.addEventListener("resize", positionPeers);
document.addEventListener("scroll", event => { if (!mentionPicker.contains(event.target)) positionPeers(); }, true);

export { applyMentionShortcut, finishPicker, labelMentionButton, mentionChord, mentionPicker, openPeers, picking, renderPeers, showPeers };
