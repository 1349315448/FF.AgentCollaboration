import { manualInboxEngines } from "../services/engines";
import { request } from "../services/api";
import { showPeers } from "./mentions.js";
import { clearSubmitError, windowRoute } from "./pane.js";
import { panes } from "../services/terminalRegistry";
import { selectedTopicId } from "../store/workbenchStore";
import { refresh } from "./workbench.js";

const replayReports = /\x1b(?:\[(?:\??\d+;\d+R|[?>=][\d;]*c|\??\d+;\d+\$y|[IO]|0n)|\](?:10|11|12);rgb:[\da-f/]+(?:\x1b\\|\x07))/gi;
function sendBytes(pane, data) { if (pane.socket?.readyState === WebSocket.OPEN) pane.socket.send(data); }
function sendControl(pane, control) { if (pane.socket?.readyState === WebSocket.OPEN) pane.socket.send(JSON.stringify(control)); }

function fitPane(pane) {
  if (document.visibilityState !== "visible" || pane.replaying || pane.surface.hidden || pane.element.hidden || pane.agent.topicId !== selectedTopicId) return;
  const width = Math.min(pane.element.clientWidth, document.documentElement.clientWidth);
  if (width < 240) return;
  pane.fit.fit();
  if (!pane.view?.agent.online || pane.socket?.readyState !== WebSocket.OPEN) return;
  const { cols, rows } = pane.term;
  const size = cols + "x" + rows;
  if (pane.sentSize === size) return;
  pane.sentSize = size;
  sendControl(pane, { type: "resize", cols, rows });
  if (!pane.replaying && pane.revision > 0) reportPrompt(pane);
}

function connect(pane) {
  clearTimeout(pane.runningTimer);
  clearTimeout(pane.reconnectTimer);
  pane.socket?.close();
  pane.socket = undefined;
  pane.generation = pane.view.terminal;
  pane.runningUntil = 0;
  pane.sentSize = undefined;
  pane.replaying = true;
  pane.revision = 0;
  pane.draftRevision = undefined;
  pane.term.reset();
  if (!pane.generation) return;
  const socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/api" + windowRoute(pane) + "/terminal");
  pane.socket = socket;
  socket.binaryType = "arraybuffer";
  socket.addEventListener("close", () => {
    if (panes.get(pane.agent.id) !== pane || pane.socket !== socket || !pane.view.agent.online) return;
    pane.reconnectTimer = setTimeout(() => {
      if (pane.socket === socket && pane.view.agent.online) connect(pane);
    }, 1500);
  });
  socket.addEventListener("message", event => {
    if (pane.socket !== socket) return;
    if (typeof event.data === "string") {
      const boundary = JSON.parse(event.data);
      if (boundary.type === "mention-ready") { showPeers(pane, boundary.request); return; }
      // 先用 ConPTY 的实际尺寸解码历史光标定位，仅在重放结束后允许可见窗格调整宿主。
      pane.term.resize(boundary.cols, boundary.rows);
      pane.replayThrough = boundary.replayThrough;
      pane.replaying = pane.replayThrough > 0;
      if (!pane.replaying) fitPane(pane);
      return;
    }
    const revision = Number(new DataView(event.data).getBigInt64(0, true));
    pane.term.write(new Uint8Array(event.data, 8), () => {
      if (pane.socket !== socket) return;
      pane.revision = revision;
      if (pane.replaying && revision >= pane.replayThrough) {
        pane.replaying = false;
        fitPane(pane);
      }
      if (pane.replaying) return;
      reportPrompt(pane);
    });
  });
}

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") panes.forEach(fitPane);
});

function reportPrompt(pane, prompt = observePrompt(pane)) {
  const revision = pane.revision;
  if (prompt.draft) pane.draftRevision = revision;
  const clearedDraft = prompt.clear ? pane.draftRevision : undefined;
  if (prompt.clear) pane.draftRevision = undefined;
  if (clearedDraft != null) clearSubmitError(pane);
  const writing = pane.view.inbox.find(item => item.state === "writing" || (item.state === "pending" && pane.view.agent.autoReceive));
  const draftIds = [...new Set([...(prompt.draftText?.replace(/\s/g, "").matchAll(/\bMSG_ID:([0-9a-f]{8})(?![0-9a-f])/gi) || [])].map(m => m[1].toLowerCase()))];
  const draftId = (writing && draftIds.find(id => id === writing.id.slice(0, 8))) || draftIds.at(-1);
  const sameDraft = prompt.editableText && draftId && (writing?.id.slice(0, 8) === draftId ? writing.id : draftId);
  sendControl(pane, { type: "prompt", revision, empty: prompt.empty, clearedDraft, screen: prompt.screen, draftId,
    retryDraft: sameDraft || undefined, bracketedPaste: pane.term.modes.bracketedPasteMode });
  clearTimeout(pane.runningTimer);
  const remaining = pane.runningUntil - Date.now();
  if (remaining > 0) {
    pane.runningTimer = setTimeout(() => {
      if (pane.replaying || pane.socket?.readyState !== WebSocket.OPEN) return;
      reportPrompt(pane);
      refresh();
    }, remaining + 50);
  }
}

function observePrompt(pane) {
  const prompt = readNativePrompt(pane);
  if (["codex", "traecli"].includes(pane.agent.engine)) {
    if (prompt.screen === "blocked" || prompt.draft) pane.runningUntil = 0;
    else if (prompt.screen === "running") pane.runningUntil = Date.now() + 3000;
    if (pane.runningUntil > Date.now())
      return {...prompt, screen:"running", clear:false, empty:false};
  }
  return prompt;
}

// 自动提交只接受两个 CLI 正常输入行的可见特征与原生粘贴模式。
function readNativePrompt(pane) {
  const term = pane.term;
  const unknown = { empty: false, clear: false, draft: false };
  if (manualInboxEngines.has(pane.agent.engine)) return { ...unknown, screen: "unknown" };
  const buffer = term.buffer.active;
  const lines = Array.from({ length: term.rows }, (_, offset) =>
    buffer.getLine(buffer.baseY + offset)?.translateToString(true).trimEnd() || "");
  const blocking = /esc (to )?(interrupt|cancel|back)|ctrl\s*\+\s*c (?:to )?interrupt|enter (to )?select|allow this (command|tool)|would you like to run|do you (want to run|trust)|approval request|press esc to stop|select model/i;
  const codex = ["codex", "traecli"].includes(pane.agent.engine);
  // 完成行以上是上一轮的输出，可能仍留有 Tip、计时器或审批提示。
  const completedTurn = /^\s*(?:[─━]+\s*)?Worked for\s+(?:\d+[hms]\s*)+(?:[•·]\s*\d{1,2}:\d{2})?\s*[─━]*$/i;
  const turnStart = codex ? lines.findLastIndex((line, y) => y < buffer.cursorY && completedTurn.test(line)) + 1 : 0;
  const screenBelow = lines.slice(Math.max(turnStart, buffer.cursorY - (codex ? 12 : 5)));
  // Codex 的工具输出可能把运行提示顶到上方；出现新审批时优先读取下方的新提示。
  const controls = codex ? (screenBelow.findLast(line => blocking.test(line)) || "") : screenBelow.join("\n");
  const executing = /(?:esc|ctrl\s*\+\s*c) (?:to )?interrupt|press esc to stop/i.test(controls);
  const blocked = blocking.test(controls) || (pane.agent.engine === "claude" &&
    /do you want to (?:proceed|allow|make this edit)|yes,? (?:allow|accept)|enter to confirm/i.test(controls));
  const screen = executing ? "running" : blocked ? "blocked" : "unknown";
  unknown.screen = screen;
  if (!term.modes.bracketedPasteMode) return unknown;
  const placeholder = /^(?:ask(?: codex| copilot)?\b|type\b|describe\b|start typing\b|enter (?:a|your)\b|send (?:a|your)\b|what (?:would|can|should)\b|message (?:codex|copilot)\b)/i;
  if (codex) {
    const row = buffer.cursorY;
    const prompt = lines[row]?.match(/^(\s*)›\s*(.*)$/);
    const clear = !!prompt && buffer.cursorX === prompt[1].length + 2 && (!prompt[2] || placeholder.test(prompt[2]));
    // 长粘贴或粘贴后回车换行时，首行 › 可能已滚出小视口，当前输入框底部的状态栏紧贴在 row 下方。
    let first = row;
    const boundary = /^\s*(?:[─━═]{3,}|[•●]\s|(?:GPT-[\w.-]+|model:)\s)|(?:for shortcuts|for agents|esc to interrupt|context left|f2 to view)/i;
    const top = Math.max(turnStart, row - 64);
    while (first > top && !/^\s*›/.test(lines[first])) {
      if (boundary.test(lines[first])) { first++; break; }
      first--;
    }
    const promptFooter = lines.slice(row + 1, row + 4).some(line => /(?:GPT-[\w.-]+|model:|for shortcuts|context left|f2\s*to\s*view)/i.test(line));
    const draftBlock = lines.slice(first, row + 1).join("").replace(/^\s*›\s*/, "").trim();
    const content = lines[first]?.match(/^\s*›\s*(.+)$/)?.[1]?.trim() || (promptFooter && !clear ? draftBlock : "");
    const draft = !!content && !placeholder.test(content);
    const draftText = draft ? draftBlock : null;
    // 窄窗会裁掉 interrupt；计时器、Tip 和闪烁符号来自输入区外的运行状态行。
    const above = lines.slice(Math.max(turnStart, first - 8), first);
    const below = lines.slice(/^\s*›/.test(lines[first]) ? row + 1 : first, row + 4);
    const codexSpinner = /[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]|^\s*◦\s|\((?:\d+h\s*)?(?:\d+m\s*)?\d+s\s*[•·]|^\s*└\s*Tip:/m.test([...above, ...below].join("\n"));
    const codexScreen = blocked ? screen : codexSpinner ? "running" : draft ? "draft" : clear ? "clear" : "unknown";
    return { clear: clear && !codexSpinner, draft, draftText, editableText: blocked || codexSpinner ? null : draftText,
      empty: clear && !blocked && !codexSpinner, screen: codexScreen };
  }
  const topBorder = /^(?:─+|╻▄+)$/;
  const bottomBorder = /^(?:─+|╹▀+)$/;
  const row = lines.findLastIndex((line, i) => /^[>❯│┃](?:\s|$)/.test(line) && i > 0 && topBorder.test(lines[i - 1]));
  if (row <= 0 || !topBorder.test(lines[row - 1])) return unknown;
  const separator = lines.findIndex((line, index) => index > row && bottomBorder.test(line));
  if (separator === -1) return unknown;
  const prompt = lines.slice(row, separator).map(line => line.replace(/^[>❯│┃]\s?/, "")).join(" ").replace(/\s+/g, " ").trim();
  const last = lines.findLastIndex(Boolean);
  const cursorAtStart = buffer.cursorY === row && buffer.cursorX <= 4;
  const renderCursor = buffer.cursorX >= term.cols - 1 && buffer.cursorY >= last;
  const cursorBelow = buffer.cursorY > separator && buffer.cursorY >= last;
  const draft = !!prompt && (pane.agent.engine === "claude" || !placeholder.test(prompt));
  const clear = !draft && (cursorAtStart || renderCursor || cursorBelow);
  const statusLines = [...lines.slice(Math.max(0, row - 3), row - 1), ...lines.slice(separator + 1)].join("\n");
  const hasStatus = /\bSession:\s*[^\n]*\bAIC\s+used\b|\b(?:interactive|autopilot|plan)\s*·|shift\s*\+\s*tab|shortcuts|\? for help|\/\s*commands\b/i.test(statusLines);
  const claudeFooter = pane.agent.engine === "claude" && /permissions|accept edits|plan mode|bypass permissions|for shortcuts/i.test(statusLines);
  const interactive = (hasStatus || claudeFooter || last === separator) && !blocked && !blocking.test(lines.slice(Math.max(0, row - 5)).join("\n"));
  const claude = pane.agent.engine === "claude";
  const claudeRun = claude && lines.slice(Math.max(0, row - 6), row - 1).some(line => /^\s*[✻✢✳✶✽⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]/.test(line));
  const activeRun = claudeRun || /\bworking\b|[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏◎◉○]/i.test(lines.slice(Math.max(0, row - 3), separator + 2).join("\n"));
  const copilotScreen = blocked ? screen : activeRun ? "running" : draft ? "draft" : clear ? "clear" : "unknown";
  return { clear: clear && !activeRun, draft, draftText: draft ? prompt : null, editableText: draft && interactive && !activeRun ? prompt : null,
    empty: clear && interactive && !activeRun, screen: copilotScreen };
}

function syncTerminal(pane, view) {
  pane.agent = view.agent;
  pane.view = view;
  if (pane.generation !== view.terminal) connect(pane);
  const writing = view.inbox.some(item => item.state === "writing");
  const pending = view.inbox.some(item => item.state === "pending");
  if (view.agent.online && !pane.replaying && pane.revision > 0 && (writing || !view.inputReady)) {
    const prompt = observePrompt(pane);
    if (writing || prompt.empty || (pending && view.agent.autoReceive)) reportPrompt(pane, prompt);
  }
}

export { fitPane, observePrompt, readNativePrompt, replayReports, reportPrompt, sendBytes, sendControl, syncTerminal };
