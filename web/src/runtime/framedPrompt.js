// 对带边框的原生输入行，审批和生成状态只读框外控件。
const horizontal = /^\s*[─━]{3,}\s*$/;
const firstInput = /^\s*[│┃]?\s*[>❯›](?:\s|$)/;
const sendHintSuffix = /\s{2,}↵\s*(?:send|发送)\s*$/i;
const zcodeTitle = /^\s*╭─+\s*(?:build|plan|edit|guarded|yolo|input|输入|构建|计划|编辑|受限)\s*─+╮/i;
const waiting = /(?:\benter (?:to )?(?:select|confirm|approve)\b|(?:按\s*)?(?:Y\/)?Enter\s*(?:选择|确认)|waiting for (?:confirmation|permission|user)$|等待确认$|do you trust the files in this folder\?|^model setup required$|^需要配置模型$|no available models\. configure a provider|没有可用模型，请配置)/i;
const waitingIndicator = /^(?:[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*)?(?:waiting for (?:user )?(?:confirmation|permission)|等待(?:用户)?确认)(?:\.{3}|…)?$/i;
const running = /(?:\(\s*(?:\d+[hms]\s*)+[^()]*[·•][^()]*\besc (?:to )?(?:cancel|interrupt)\b|^working\. escape to cancel\.|^处理中。按 Escape 取消。|^[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*esc to interrupt\b)/i;
const buddySpinner = /^[✶✷✸✹✺] [^()\r\n]+(?:…|\.{3})\s*\((?:\d+[hms]\s*)+·\s*(preparing|waiting for model|streaming|processing|running [^()]+|waiting for (?:permission|user)|retrying|syncing file projection)\)$/i;
const buddySteerHeader = "Messages to be submitted after next tool call (press esc to interrupt and send immediately)";
const qwenSteer = /^\s*Enter (?:to steer|追加到当前任务)\s*·\s*Ctrl\+Q\b/i;
const qwenQueueHint = /^\s*Ctrl\+Q (?:to queue\s*·\s*↑ to edit queued messages|排到下一轮\s*·\s*↑ 编辑排队消息)\s*$/i;
const envelopeId = /^MSG_ID: ([0-9a-f]{8}) \|/i;

// CodeBuddy places its live spinner, optional tip and native queues directly above the editor.
// A transcript line or a tool result between these controls and the editor ends the match.
function codeBuddyRun(lines) {
  const start = lines.findLastIndex(line => buddySpinner.test(line));
  if (start < 0) return;
  const phase = buddySpinner.exec(lines[start])[1];
  let tip = false, section, steerId, followUpId;
  for (let i = start + 1; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) continue;
    if (!section && /^ {1,3}└ Tip:/.test(line)) { tip = true; continue; }
    if (tip && /^ {4,}\S/.test(line)) continue;
    tip = false;
    if (/^ Messages to be submitted\b/.test(line)) {
      let title = line.trim();
      while (title !== buddySteerHeader && buddySteerHeader.startsWith(title + " ") && i + 1 < lines.length)
        title += " " + lines[++i].trim();
      if (title !== buddySteerHeader) return;
      section = "steer";
    } else if (line.trimEnd() === " Queued follow-up inputs") {
      section = "followUp";
    } else if (section && line.startsWith(" ↳ ")) {
      const id = envelopeId.exec(line.slice(3))?.[1];
      if (id && section === "steer") steerId = id;
      if (id && section === "followUp") followUpId = id;
    } else if (!(section && /^ \+\d+ more\s*$/.test(line)) &&
        !(section === "followUp" && /^ (?:Alt|Option|⌥)\+↑ edit last queued message\s*$/i.test(line))) return;
  }
  return {blocked:/^waiting for (?:permission|user)$/i.test(phase), queuedId:steerId ?? followUpId};
}

function qwenQueuedId(lines, row, footer, buffer) {
  let i = row - 2;
  while (i >= 0 && !lines[i].trim()) i--;
  if (i < 0) return;
  if (qwenQueueHint.test(lines[i])) i--;
  else if (!/⏳\s*\d+\s*(?:queued\b|条已排队)/i.test(footer)) return;
  if (i >= 0 && /^  \.\.\. \(\+\d+ more\)/.test(lines[i])) i--;
  // Ink renders at most three queue previews with exactly two cells of padding and dim text.
  for (let n = 0; i >= 0 && n < 3; i--, n++) {
    if (!/^  \S/.test(lines[i]) || !buffer.getLine(buffer.baseY + i)?.getCell?.(2)?.isDim()) return;
    const id = envelopeId.exec(lines[i].slice(2))?.[1];
    if (id) return id;
  }
}

// Windows 千问在建议首字画下划线光标。草稿移到行首也有同样光标，须核对后面每个可见字。
function ghostSuggestion(engine, buffer, row, start, editor, sendHint) {
  const cursor = buffer.getLine(buffer.baseY + row)?.getCell?.(start);
  // 腾讯主题会将同一原生建议画成 243、亮黑、RGB 灰或弱化文字，硬件光标仍停在输入起点。
  const hardwarePrediction = engine === "codebuddy" && sendHint && buffer.cursorY === row && buffer.cursorX === start && !cursor?.isInverse();
  if (!hardwarePrediction && !(cursor?.isInverse() || (engine === "qwen" && cursor?.isUnderline()))) return false;
  if (!cursor.getChars() || !editor[0].startsWith(cursor.getChars())) return false;
  let mutedText = false;
  for (const [index, text] of editor.entries()) {
    if (!text) continue;
    if (index && engine !== "qwen" && !hardwarePrediction) return false;
    const line = buffer.getLine(buffer.baseY + row + index);
    let x = index ? line.translateToString(true).search(/\S/) : start;
    let offset = 0;
    if (!index && !hardwarePrediction) { x += cursor.getWidth(); offset = cursor.getChars().length; }
    while (offset < text.length) {
      const cell = line.getCell(x++);
      if (!cell) return false;
      if (cell.getWidth() === 0) continue;
      const chars = cell.getChars() || " ";
      if (!text.startsWith(chars, offset)) return false;
      offset += chars.length;
      if (!chars.trim()) continue;
      const fg = cell.getFgColor();
      const rgb = [fg >> 16, (fg >> 8) & 255, fg & 255];
      if (!(cell.isDim() || (cell.isFgPalette() && (fg === 8 || (hardwarePrediction && fg === 243))) ||
          (cell.isFgRGB() && Math.max(...rgb) <= 160 && Math.max(...rgb) - Math.min(...rgb) <= 32))) return false;
      mutedText = true;
    }
  }
  return mutedText;
}

export function framedInputBounds(engine, lines) {
  const zcode = engine === "zcode";
  const last = lines.findLastIndex(line => line.trim());
  const yolo = engine === "qwen" && lines.slice(Math.max(0, last - 3), last + 1).some(line => /\byolo\b/i.test(line) && /tab/i.test(line));
  const prefix = yolo ? /^\s*[│┃]?\s*[*❯›>](?:\s|$)/ : engine === "qwen" ?
    /^\s*[│┃]?\s*(?:[❯›>](?:\s|$)|\*\s+(?=\/approval-mode\b))/ : engine === "qoder" ? /^\s*>(?:\s|$)/ : firstInput;
  const row = zcode ? lines.findLastIndex(line => zcodeTitle.test(line)) + 1 :
    lines.findLastIndex((line, i) => i > 0 && prefix.test(line) && (horizontal.test(lines[i-1]) || /^\s*╭─/.test(lines[i-1])) &&
      lines.slice(i + 1).some(next => horizontal.test(next) || /^\s*╰─/.test(next)));
  const framed = row > 0;
  const bottom = framed ? lines.findIndex((line, i) => i > row && (horizontal.test(line) || /^\s*╰─/.test(line))) : -1;
  return {zcode,last,prefix,row,bottom,framed};
}

function idlePlaceholderStyle(engine, term, row, left, bottom, editor) {
  const buffer = term.buffer.active;
  let mutedCell;
  if (engine === "zcode") {
    // ZCode's model/provider separator uses the same palette.muted as its textarea placeholder.
    const metadata = buffer.getLine(buffer.baseY + bottom - 1);
    let modelCell;
    for (let x = 0; x < (metadata?.length || 0); x++) {
      const cell = metadata.getCell(x);
      if (cell.getChars() === "|") { mutedCell = cell; break; }
      if (!modelCell && /[^│┃\s]/.test(cell.getChars())) modelCell = cell;
    }
    if (!modelCell || !mutedCell || mutedCell.getFgColorMode() === 0 ||
        (modelCell.getFgColorMode() === mutedCell.getFgColorMode() && modelCell.getFgColor() === mutedCell.getFgColor())) return false;
  }
  for (let i = 0; i < editor.length; i++) {
    const line = buffer.getLine(buffer.baseY + row + i);
    let offset = 0;
    for (let x = left; offset < editor[i].length; x++) {
      const cell = line?.getCell?.(x);
      if (!cell) return false;
      if (cell.getWidth() === 0) continue;
      const chars = cell.getChars() || " ";
      if (!editor[i].startsWith(chars, offset)) return false;
      offset += chars.length;
      if (!chars.trim()) continue;
      if (mutedCell) {
        if (cell.getFgColorMode() !== mutedCell.getFgColorMode() || cell.getFgColor() !== mutedCell.getFgColor()) return false;
      } else if (!cell.isDim() && !(cell.isFgPalette() && cell.getFgColor() === 8)) return false;
    }
  }
  return true;
}

export function readFramedPrompt(engine, term, lines) {
  const {zcode,last,prefix,row,bottom,framed} = framedInputBounds(engine, lines);
  const before = framed ? lines.slice(Math.max(0, row - 5), row - 1).filter(line => line.trim()) : [];
  const after = bottom > row ? lines.slice(bottom + 1, last + 1) : [];
  const controls = framed ? [...before.slice(-1), ...after] : lines.slice(Math.max(0, last - 8), last + 1)
    .filter(line => !firstInput.test(line));
  const dialog = framed ? after : controls;
  const buddy = framed && engine === "codebuddy" ? codeBuddyRun(lines.slice(Math.max(0, row - 30), row - 1)) : undefined;
  const qwenWorking = framed && engine === "qwen" && after.some(line => qwenSteer.test(line));
  const nativeBusy = !!buddy || qwenWorking;
  const blocked = buddy?.blocked || (engine === "qoder" && dialog.some(line => /waiting for authentication|sign in to (?:continue|get started)|等待认证|登录以继续|↑\/↓ navigate/i.test(line))) ||
    dialog.some(line => waiting.test(line)) || controls.some(line => waitingIndicator.test(line.trim())) ||
    before.some(line => /^(?:model setup required|需要配置模型)$/i.test(line.trim()));
  // ZCode puts its spinner below the frame. For Qoder, prose in scrollback is not a live cancellation control.
  const runControls = framed && (zcode || engine === "qoder") ? after : controls;
  const qoderProgress = engine === "qoder" && /^\s*(?:Generating|Thinking)(?:…|\.{3})\s*\(esc to cancel\)\s*$/i.test(before.at(-1) || "");
  const busy = nativeBusy || qoderProgress || runControls.some(line => (engine === "qoder" && /(?:esc to cancel|^\s*(?:Generating|Thinking)(?:…|\.{3}))/i.test(line)) || running.test(line.trim()) || (engine === "codebuddy" &&
    /^(?:∴ Thinking(?:\.{3}|…)|Auto review: .* · esc to interrupt|attempt \d+ · esc to stop)$/i.test(line.trim())));
  const result = {empty:false, clear:false, draft:false, screen:blocked ? "blocked" : busy ? "active" : "idle"};
  if (!term.modes.bracketedPasteMode || blocked || !framed || bottom < row + 1) return result;
  if (after.some(line => /↑\s*(?:to manage attachments|管理附件)|Delete to remove|Delete\s*(?:删除|移除)/i.test(line)))
    return {...result, draft:true, screen:busy ? "active" : "draft"};

  // ZCode 把模型/供应商与推理档位放在框内最后一行；另两个引擎的状态栏在框外。
  if (zcode && (bottom < row + 3 || !/\|/.test(lines[bottom - 1]))) return result;
  const editor = lines.slice(row, zcode ? bottom - 1 : bottom).map((line, i) => {
    let value = line.replace(/^\s*[│┃]/, "").replace(/[│┃]\s*$/, "");
    if (!i && !zcode) value = value.replace(prefix, "");
    return (engine === "codebuddy" ? value : value.replace(sendHintSuffix, "")).trim();
  });
  const buffer = term.buffer.active;
  const first = lines[row];
  const x = zcode ? (first.match(/^\s*│\s?/)?.[0].length ?? 2) : (first.match(prefix)?.[0].length ?? 2);
  let text = editor.filter(Boolean).join(" ");
  // Qwen renders a dim inline argument hint after the caret while its slash menu is open.
  if (engine === "qwen" && /^\/approval-mode\s+<mode>\u200b?$/.test(text) &&
      buffer.cursorY === row && buffer.cursorX === x + "/approval-mode".length &&
      after.some(line => /^\s*[>❯›]\s*\/?approval-mode\s+<mode>/i.test(line)))
    text = "/approval-mode";
  // 输入斜杠命令时，候选列表会取代常规页脚。它仍是草稿，不能自动收件。
  const footer = after.join(" ");
  if (!zcode && !qwenWorking && !/^(?:\/permissions|\/approval-mode)$/.test(text) &&
      !/(?:for (?:shortcuts|agents)|Context\b|\d%\s*used|上下文|已用|接受编辑|(?:shift\s*\+\s*)?tab to (?:switch|cycle)|按\s*(?:Shift\s*\+\s*)?Tab\s*切换|\? for help|快捷键)/i.test(footer)) return result;
  const cellLine = buffer.getLine(buffer.baseY + row);
  const cursorCell = cellLine?.getCell?.(x);
  const physicalStart = buffer.cursorY === row && (buffer.cursorX === x || (!text && buffer.cursorX === x + 1));
  if (engine === "codebuddy" && physicalStart && editor.length === 1 && /^\s{4,}↵\s*(?:send|发送)\s*$/i.test(first.slice(x)) &&
      first.length >= term.cols - 8) text = "";
  const atStart = physicalStart || !!cursorCell?.isInverse();
  const placeholder = zcode ? /^(?:Type a prompt|输入提示词)$/i :
    /^(?:Type your message or @path\/to\/file|Type your request, or \/usage|输入您的消息或 @ 文件路径|输入消息或 @path\/to\/file)$/i;
  const sendHint = engine === "codebuddy" && editor.some(line => sendHintSuffix.test(line));
  const prediction = sendHint ? editor.map(line => line.replace(sendHintSuffix, "").trimEnd()) : editor;
  const suggested = !zcode && ghostSuggestion(engine, buffer, row, x, prediction, sendHint);
  const inEditor = buffer.cursorY >= row && buffer.cursorY < bottom && (buffer.cursorY !== row || buffer.cursorX >= x);
  // Qwen's static placeholder starts after the empty caret cell. Home on ordinary typed text occupies that cell.
  const nativePlaceholder = engine !== "codebuddy" && placeholder.test(text) &&
    (!(zcode || engine === "qoder") || idlePlaceholderStyle(engine, term, row, x, bottom, editor)) &&
    (engine !== "qwen" || /^\s/.test(first.slice(x)));
  const queuedId = qwenWorking ? qwenQueuedId(lines, row, footer, buffer) : buddy?.queuedId;
  if (suggested || (atStart && (!text || nativePlaceholder)))
    return {empty:!busy, clear:true, draft:false, screen:busy ? "running" : "clear",
      ...(nativeBusy && physicalStart && {busyInput:true}), ...(queuedId && {queuedId})};
  if (text) return {empty:false, clear:false, draft:true, draftText:text, editableText:busy ? null : text, screen:busy ? "active" : "draft",
    ...(nativeBusy && inEditor && {busyEditable:true}), ...(queuedId && {queuedId})};
  return result;
}
