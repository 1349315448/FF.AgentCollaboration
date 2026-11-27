import { request } from "../services/api";
import { readNativePrompt, sendControl } from "./terminal.js";
import { panes } from "../services/terminalRegistry";

const keyPrefix = "ff.manual-draft.";
window.addEventListener("ff:recover-drafts", ({detail:doc}) => {
  try {
    for (const key of Object.keys(sessionStorage).filter(key => key.startsWith(keyPrefix))) {
      const text = sessionStorage.getItem(key);
      if (!text) continue;
      const id = key.slice(keyPrefix.length);
      const name = doc.createElement("h2"), body = doc.createElement("pre");
      name.textContent = "暂存草稿 · " + (panes.get(id)?.agent.name || id);
      body.textContent = text;
      doc.body.append(name,body);
    }
  } catch { /* This origin may have browser storage disabled. */ }
});

// xterm coordinates count cells. Reading to cursorX preserves full-width characters.
function currentLine(pane) {
  if (pane.replaying || !pane.term.modes.bracketedPasteMode) return null;
  const prompt = readNativePrompt(pane);
  if (prompt.screen !== "draft" || !prompt.editableText) return null;
  const buffer = pane.term.buffer.active;
  const row = buffer.baseY + buffer.cursorY, line = buffer.getLine(row);
  if (!line || line.isWrapped || buffer.getLine(row + 1)?.isWrapped) return null;
  const before = line.translateToString(false, 0, buffer.cursorX);
  const text = before.match(/^\s*[›>❯│┃] (.+)$/u)?.[1];
  if (text !== prompt.editableText || line.translateToString(true, buffer.cursorX).trim()) return null;
  return text;
}

export function manualInboxDelivery(pane, route) {
  const key = keyPrefix + pane.agent.id;
  const backup = pane.element.querySelector(".saved-editor-draft");
  const field = backup.querySelector("textarea");
  const discard = backup.querySelector("button");
  let deferred = null, composing = false, sending = false;
  function show(text) {
    field.value = text || "";
    backup.hidden = !text;
  }
  function forget() {
    deferred = null;
    sessionStorage.removeItem(key);
    show(null);
  }
  try { show(sessionStorage.getItem(key)); } catch { show(null); }
  discard.onclick = forget;
  for (const [event,active] of [["compositionstart",true],["compositionend",false]]) {
    pane.surface.addEventListener(event, () => {
      composing = active;
      sendControl(pane, {type:"composition",active});
    });
  }
  async function confirmRestore(draft) {
    const until = performance.now() + 1800;
    do {
      if (pane.generation === draft.generation && currentLine(pane) === draft.text) { forget(); return; }
      await new Promise(resolve => setTimeout(resolve, 60));
    } while (performance.now() < until);
  }
  pane.resumeSavedDraft = async () => {
    if (!deferred || composing || sending || !pane.view.inputReady || pane.replaying) return;
    const {messageId,draft} = deferred;
    deferred = null;
    if (draft.generation !== pane.generation) return;
    sending = discard.disabled = true;
    try {
      const result = await request("/windows/"+pane.agent.id+"/inbox/"+messageId+"/restore-draft", {method:"POST",body:draft});
      if (result?.restored) await confirmRestore(draft);
    } catch { /* The tab still holds the original for recovery. */ }
    finally { sending = discard.disabled = false; }
  };
  return async messageId => {
    if (sending) return;
    if (pane.view.nativeInbox) return request(route, {method:"POST",body:{messageId}});
    if (composing) throw new Error("请先完成输入法选词，再立即投递。");
    const text = currentLine(pane);
    if (!text) return request(route, {method:"POST",body:{messageId}});
    if (!backup.hidden && field.value !== text) throw new Error("此窗口还有一份暂存草稿，请先取回或清除副本。");
    const draft = {text, generation:pane.generation, revision:pane.revision};
    // Save before the first destructive operation, including if the HTTP connection is lost.
    try { sessionStorage.setItem(key, text); } catch { throw new Error("无法暂存草稿，已保留终端输入。"); }
    show(text);
    deferred = null;
    sending = discard.disabled = true;
    try {
      const result = await request(route, {method:"POST",body:{messageId,draft}});
      if (result?.restored) await confirmRestore(draft);
      else if (result?.restored === false) deferred = {messageId,draft};
    } finally { sending = discard.disabled = false; }
  };
}
