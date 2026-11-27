import { readFramedPrompt } from "./framedPrompt.js";
import { foldedCodexChars } from "./codexPaste.js";
import { isLayoutAnimating } from "./layout.js";
import { isCopilotPlaceholder } from "./copilotPrompt.js";
import { isClaudePlaceholder } from "./claudePrompt.js";
import { forgetTraeSuggestion, invalidateTraeDraft, readTraePrompt } from "./traePrompt.js";
import { request } from "../services/api";
import { pageSocketUrl } from "../services/workbenchPage";
import { showPeers } from "./mentions.js";
import { clearSubmitError, windowRoute } from "./pane.js";
import { panes } from "../services/terminalRegistry";
import { selectedTopicId } from "../store/workbenchStore";
import { refresh } from "./workbench.js";

const replayReports = /\x1b(?:\[(?:\??\d+;\d+R|[?>=][\d;]*c|\??\d+;\d+\$y|[IO]|0n)|\](?:10|11|12);rgb:[\da-f/]+(?:\x1b\\|\x07))/gi;
function sendBytes(pane, data) {
  if (pane.socket?.readyState !== WebSocket.OPEN) return;
  if (pane.agent.engine === "traecli" && !(data.length === 1 && data[0] === 13)) invalidateTraeDraft(pane.term);
  pane.socket.send(data);
}
function sendControl(pane, control) { if (pane.socket?.readyState === WebSocket.OPEN) pane.socket.send(JSON.stringify(control)); }

function fitPane(pane) {
  if (document.visibilityState !== "visible" || pane.replaying || pane.surface.hidden || pane.element.hidden || pane.agent.topicId !== selectedTopicId) return;
  if (isLayoutAnimating()) { pane.pendingFit = true; return; }
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
  clearTimeout(pane.promptTimer);
  pane.promptTimer = undefined;
  pane.lastPrompt = undefined;
  pane.socket?.close();
  pane.socket = undefined;
  pane.generation = pane.view.terminal;
  pane.runningUntil = 0;
  pane.sentSize = undefined;
  pane.replaying = true;
  pane.revision = 0;
  pane.draftRevision = undefined;
  pane.term.reset();
  if (pane.agent.engine === "traecli") forgetTraeSuggestion(pane.term);
  if (!pane.generation) return;
  const address = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/api" + windowRoute(pane) + "/terminal";
  const socket = new WebSocket(pageSocketUrl(address));
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
        reportPrompt(pane);
        return;
      }
      if (pane.replaying) return;
      schedulePromptReport(pane);
    });
  });
}

function schedulePromptReport(pane) {
  if (pane.promptTimer) return;
  const urgent = pane.view?.inbox?.some(item => item.state === "writing" || item.state === "pending");
  const hiddenIdle = (pane.element.hidden || pane.surface.hidden) && !urgent;
  const delay = urgent ? 16 : hiddenIdle ? 240 : 60;
  pane.promptTimer = setTimeout(() => {
    pane.promptTimer = undefined;
    if (pane.replaying || pane.socket?.readyState !== WebSocket.OPEN) return;
    reportPrompt(pane);
  }, delay);
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
  const reviewing = pane.view.inbox.filter(item => item.state === "delivery_uncertain");
  const writing = pane.view.inbox.find(item => item.state === "writing") ||
    (reviewing.length === 1 && reviewing[0]) ||
    (pane.view.agent.autoReceive && pane.view.inbox.find(item => item.state === "pending"));
  const shortId = writing?.id?.slice(0, 8);
  const draftIds = [...new Set([...(prompt.draftText?.replace(/\s/g, "").matchAll(/\bMSG_ID:([0-9a-f]{8})(?![0-9a-f])/gi) || [])].map(m => m[1].toLowerCase()))];
  // Codex collapses long pastes into `[Pasted Content N chars]`, hiding both MSG_ID and the raw 48-char tail.
  const foldedChars = shortId && !draftIds.includes(shortId)
    ? foldedCodexChars(pane.agent.engine, writing, prompt.draftText) : undefined;
  const draftId = draftIds.find(id => id === shortId) || draftIds.at(-1) || (foldedChars !== undefined ? shortId : undefined);
  const ownBusyDraft = prompt.busyEditable && pane.agent.receiveMode === "immediate" && shortId && draftId === shortId;
  const editable = prompt.editableText || (ownBusyDraft ? prompt.draftText : null);
  const sameDraft = editable && draftId && (shortId === draftId ? writing.id : draftId);
  // ConPTY may repaint its status line while the complete Codex editor stays unchanged.
  const draftTail = sameDraft && (pane.agent.receiveMode === "immediate" || ["codex", "traecli"].includes(pane.agent.engine))
    ? editable.replace(/\s/g, "").slice(-48) : undefined;
  const approvalDraft = prompt.screen === "draft" && /^\/(?:permissions|approval-mode)$/.test(prompt.draftText) ? prompt.draftText : undefined;
  sendControl(pane, { type: "prompt", revision, empty: prompt.empty, clearedDraft, screen: prompt.screen, draftId,
    approvalDraft, retryDraft: sameDraft || undefined, draftTail, foldedChars, busyInput:prompt.busyInput, queuedId:prompt.queuedId,
    bracketedPaste: pane.term.modes.bracketedPasteMode });
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
  let result = prompt;
  if (["codex", "traecli"].includes(pane.agent.engine)) {
    if (prompt.screen === "blocked" || prompt.draft) pane.runningUntil = 0;
    else if (prompt.screen === "running") pane.runningUntil = Date.now() + 3000;
    if (pane.runningUntil > Date.now())
      result = {...prompt, screen:"running", clear:false, empty:false};
  }
  pane.lastPrompt = { revision: pane.revision, runningUntil: pane.runningUntil || 0, prompt: result };
  return result;
}

function cachedPrompt(pane) {
  const cached = pane.lastPrompt;
  if (cached && cached.revision === pane.revision && (!cached.runningUntil || cached.runningUntil > Date.now())) {
    return cached.prompt;
  }
  return observePrompt(pane);
}


function readNativePrompt(pane) {
  const term = pane.term;
  const unknown = { empty: false, clear: false, draft: false };
  const buffer = term.buffer.active;
  const lines = Array.from({ length: term.rows }, (_, offset) =>
    buffer.getLine(buffer.baseY + offset)?.translateToString(true).trimEnd() || "");
  if (pane.agent.engine === "traecli") {
    const fullScreen = readTraePrompt(term, lines);
    if (fullScreen) return fullScreen;
  }
  if (["qwen", "codebuddy", "zcode", "qoder"].includes(pane.agent.engine)) return readFramedPrompt(pane.agent.engine, term, lines);
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
  if (codex) {
    const row = buffer.cursorY;
    // 全屏重绘时光标会暂移到旧消息。下方仍有原生输入行时，该帧不能确认送达或补回车。
    if (lines.some((line, index) => index > row && /^›(?:\s|$)/.test(line)))
      return { ...unknown, screen:"unknown", editableText:null };
    const prompt = lines[row]?.match(/^(\s*)›\s*(.*)$/);
    const editorLine = buffer.getLine(buffer.baseY + row);
    const clear = !!prompt && buffer.cursorX === prompt[1].length + 2 && (!prompt[2] ||
      Array.from({length:prompt[2].length}, (_,i)=>editorLine?.getCell?.(buffer.cursorX + i)).every(cell=>cell?.isDim?.()));
    // 长粘贴或粘贴后回车换行时，首行 › 可能已滚出小视口，当前输入框底部的状态栏紧贴在 row 下方。
    let first = row;
    const boundary = /^\s*(?:[─━═]{3,}|[•●]\s|(?:GPT-[\w.-]+|model:)\s)|(?:for shortcuts|for agents|esc to interrupt|context left|f2 to view)/i;
    const top = Math.max(turnStart, row - 64);
    while (first > top && !/^\s*›/.test(lines[first])) {
      if (boundary.test(lines[first])) { first++; break; }
      first--;
    }
    // 重绘时光标可留在交接草稿首行。只追踪当前写入编号、连续满宽行与 xterm 软折行。
    // 后端仍核对整条消息的尾部、键盘版本和原生审批状态。
    let end=row;
    const ownId=lines[first]?.match(/^\s*›\s*MSG_ID:\s*([a-f0-9]{8})\b/i)?.[1];
    if(ownId && pane.view?.inbox.some(item=>
      (item.state==="writing" || item.state==="delivery_uncertain") && item.id.startsWith(ownId))) {
      while(end+1<lines.length) {
        const line=buffer.getLine(buffer.baseY+end);
        const edge=line?.getCell(term.cols-1);
        const wrapped=buffer.getLine(buffer.baseY+end+1)?.isWrapped;
        if(!wrapped && !(edge?.getChars() || edge?.getWidth()===0 || line?.getCell(term.cols-2)?.getChars())) break;
        if(!lines[end+1].trim() || boundary.test(lines[end+1]) || /^\s*›/.test(lines[end+1])) break;
        end++;
      }
    }
    const promptFooter = lines.slice(end + 1, end + 4).some(line => /(?:GPT-[\w.-]+|model:|for shortcuts|context left|f2\s*to\s*view)/i.test(line));
    const draftBlock = lines.slice(first, end + 1).join("").replace(/^\s*›\s*/, "").trim();
    const content = lines[first]?.match(/^\s*›\s*(.+)$/)?.[1]?.trim() || (promptFooter && !clear ? draftBlock : "");
    const draft = !!content && !clear;
    const draftText = draft ? draftBlock : null;
    // 窄窗会裁掉 interrupt；计时器、Tip 和闪烁符号来自输入区外的运行状态行。
    const above = lines.slice(Math.max(turnStart, first - 8), first);
    const below = lines.slice(/^\s*›/.test(lines[first]) ? row + 1 : first, row + 4);
    const codexSpinner = /[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]|^\s*◦\s|\((?:\d+h\s*)?(?:\d+m\s*)?\d+s\s*[•·]|^\s*└\s*Tip:/m.test([...above, ...below].join("\n"));
    const codexScreen = blocked ? screen : codexSpinner ? "running" : draft ? "draft" : clear ? "clear" : "unknown";
    // While a native turn is running, Enter steers the turn. Only the actual editor and
    // its working/queue controls qualify; a spinner or a matching line in output is insufficient.
    const turnControls = [];
    for (let y = Math.max(turnStart, first - 16); y < first; y++) {
      const wrapsNext = buffer.getLine(buffer.baseY + y + 1)?.isWrapped;
      const text = wrapsNext ? buffer.getLine(buffer.baseY + y).translateToString(false) : lines[y];
      if (turnControls.length && buffer.getLine(buffer.baseY + y)?.isWrapped) turnControls[turnControls.length - 1] += text;
      else if (text.trim()) turnControls.push(text);
    }
    // Codex 新版可省略旋转符，并在计时器后显示后台终端数量。计时器、编辑器和页脚须属于同一画面。
    const working = turnControls.findLastIndex(line =>
      /^(?:\s*[◦⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s+|(?=[\p{L}])).+?\((?:\d+[hms]\s*)+\s*[•·]\s*esc to interrupt\)(?:\s*·\s*\d+ background terminals? running\b.*)?\s*$/iu.test(line));
    const afterWorking = turnControls.slice(working + 1);
    const queueTitle = /^\s*•\s+(?:Queued follow-up inputs|Messages to be submitted after next tool call \(press esc to interrupt and send immediately\))\s*$/i;
    const queueEntry = /^\s*↳\s+MSG_ID:\s*([\da-f]{8})\b/i;
    const nativeTurn = pane.agent.engine === "codex" && working >= 0 && promptFooter && codexScreen === "running" &&
      afterWorking.every(line => queueTitle.test(line) || /^\s+(?:↳|└\s*Tip:|\S)/.test(line)) &&
      !afterWorking.some(line => /enter to (?:select|confirm)|approval request|would you like to run/i.test(line));
    const queueStart = afterWorking.findLastIndex(line => queueTitle.test(line));
    const queuedId = nativeTurn && queueStart >= 0
      ? afterWorking.slice(queueStart + 1).findLast(line => queueEntry.test(line))?.match(queueEntry)?.[1]?.toLowerCase() : undefined;
    return { clear: clear && !codexSpinner, draft, draftText, editableText: blocked || codexSpinner ? null : draftText,
      busyInput:nativeTurn && clear, busyEditable:nativeTurn && draft && lines.slice(row + 1, row + 5).some(line => /tab to queue message/i.test(line)), queuedId,
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
  const draft = !!prompt && !(pane.agent.engine === "claude" ? isClaudePlaceholder(term, lines, row, separator) :
    isCopilotPlaceholder(term, lines, row, separator));
  const clear = !draft && (cursorAtStart || renderCursor || cursorBelow);
  const statusLines = [...lines.slice(Math.max(0, row - 3), row - 1), ...lines.slice(separator + 1)].join("\n");
  const hasStatus = /\bSession:\s*[^\n]*\bAIC\s+used\b|\b(?:interactive|autopilot|plan)\s*·|shift\s*\+\s*tab|shortcuts|\? for help|\/\s*commands\b/i.test(statusLines);
  const claudeFooter = pane.agent.engine === "claude" && /permissions|accept edits|plan mode|bypass permissions|for shortcuts/i.test(statusLines);
  const interactive = (hasStatus || claudeFooter || last === separator) && !blocked && !blocking.test(lines.slice(Math.max(0, row - 5)).join("\n"));
  const claude = pane.agent.engine === "claude";
  const activeRun = claude
    ? /^\s*[✻✢✳✶✽⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏].*(?:…|\.{3})/.test(lines.slice(0, row - 1).findLast(line => line.trim()) || "")
    : /\bworking\b|[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏◎◉○]/i.test(lines.slice(Math.max(0, row - 3), separator + 2).join("\n"));
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
    const prompt = cachedPrompt(pane);
    if (writing || prompt.empty || (pending && view.agent.autoReceive)) reportPrompt(pane, prompt);
  }
}

export { cachedPrompt, fitPane, observePrompt, readNativePrompt, replayReports, reportPrompt, sendBytes, sendControl, syncTerminal };
