// Qwen、CodeBuddy 和 ZCode 都有独立的输入框。审批、生成状态只读框外的控件。
const horizontal = /^\s*[─━]{3,}\s*$/;
const firstInput = /^\s*[│┃]?\s*[>❯›](?:\s|$)/;
const zcodeTitle = /^\s*╭─+\s*(?:build|plan|edit|guarded|yolo|input|输入|构建|计划|编辑|受限)\s*─+╮/i;
const waiting = /(?:\benter (?:to )?(?:select|confirm|approve)\b|(?:按\s*)?(?:Y\/)?Enter\s*(?:选择|确认)|waiting for (?:confirmation|permission|user)$|等待确认$|do you trust the files in this folder\?|^model setup required$|^需要配置模型$|no available models\. configure a provider|没有可用模型，请配置)/i;
const waitingIndicator = /^(?:[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*)?(?:waiting for (?:user )?(?:confirmation|permission)|等待(?:用户)?确认)(?:\.{3}|…)?$/i;
const running = /(?:\(\s*(?:\d+[hms]\s*)+[^()]*[·•][^()]*\besc (?:to )?(?:cancel|interrupt)\b|^working\. escape to cancel\.|^处理中。按 Escape 取消。|^[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*esc to interrupt\b)/i;

// CLI 会给建议续写的第一字画反色光标，余下文字使用弱化色。普通草稿即使按 Home 也不能当作占位符。
function ghostSuggestion(line, start, text) {
  const cursor = line?.getCell?.(start);
  if (!cursor?.isInverse() || !cursor.getChars() || !text.startsWith(cursor.getChars())) return false;
  let mutedText = false;
  let offset = cursor.getChars().length;
  for (let x = start + cursor.getWidth(); offset < text.length; x++) {
    const cell = line.getCell(x);
    if (!cell) return false;
    if (cell.getWidth() === 0) continue;
    const chars = cell.getChars() || " ";
    if (!text.startsWith(chars, offset)) return false;
    offset += chars.length;
    if (!chars.trim()) continue;
    const fg = cell.getFgColor();
    const rgb = [fg >> 16, (fg >> 8) & 255, fg & 255];
    if (!(cell.isDim() || (cell.isFgPalette() && fg === 8) ||
        (cell.isFgRGB() && Math.max(...rgb) <= 160 && Math.max(...rgb) - Math.min(...rgb) <= 32))) return false;
    mutedText = true;
  }
  return mutedText;
}

export function readFramedPrompt(engine, term, lines) {
  const zcode = engine === "zcode";
  const last = lines.findLastIndex(line => line.trim());
  const yolo = engine === "qwen" && lines.slice(Math.max(0, last - 3), last + 1).some(line => /\byolo\b/i.test(line) && /tab/i.test(line));
  const prefix = yolo ? /^\s*[│┃]?\s*[*❯›>](?:\s|$)/ : engine === "qwen" ?
    /^\s*[│┃]?\s*(?:[❯›>](?:\s|$)|\*\s+(?=\/approval-mode\b))/ : firstInput;
  const row = zcode ? lines.findLastIndex(line => zcodeTitle.test(line)) + 1 :
    lines.findLastIndex((line, i) => i > 0 && prefix.test(line) && (horizontal.test(lines[i-1]) || /^\s*╭─/.test(lines[i-1])) &&
      lines.slice(i + 1).some(next => horizontal.test(next) || /^\s*╰─/.test(next)));
  const framed = row > 0;
  const bottom = framed ? lines.findIndex((line, i) => i > row && (horizontal.test(line) || /^\s*╰─/.test(line))) : -1;
  const before = framed ? lines.slice(Math.max(0, row - 5), row - 1).filter(line => line.trim()) : [];
  const after = bottom > row ? lines.slice(bottom + 1, last + 1) : [];
  const controls = framed ? [...before.slice(-1), ...after] : lines.slice(Math.max(0, last - 8), last + 1)
    .filter(line => !firstInput.test(line));
  const dialog = framed ? after : controls;
  const blocked = dialog.some(line => waiting.test(line)) || controls.some(line => waitingIndicator.test(line.trim())) ||
    before.some(line => /^(?:model setup required|需要配置模型)$/i.test(line.trim()));
  const busy = controls.some(line => running.test(line.trim()) || (engine === "codebuddy" &&
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
    return value.replace(/\s{2,}↵\s*(?:send|发送)\s*$/i, "").trim();
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
  if (!zcode && !/^(?:\/permissions|\/approval-mode)$/.test(text) &&
      !/(?:for (?:shortcuts|agents)|Context\b|\d%\s*used|上下文|已用|接受编辑|(?:shift\s*\+\s*)?tab to (?:switch|cycle)|按\s*(?:Shift\s*\+\s*)?Tab\s*切换|\? for help|快捷键)/i.test(footer)) return result;
  const cellLine = buffer.getLine(buffer.baseY + row);
  const softCursor = !!cellLine?.getCell?.(x)?.isInverse();
  const atStart = (buffer.cursorY === row && (buffer.cursorX === x || (!text && buffer.cursorX === x + 1))) || softCursor;
  const placeholder = zcode ? /^(?:Type a prompt|输入提示词)$/i :
    /^(?:Type your message or @path\/to\/file|Type your request, or \/usage|输入您的消息或 @ 文件路径)$/i;
  const suggested = !zcode && !editor.slice(1).some(Boolean) && ghostSuggestion(cellLine, x, text);
  if (atStart && (!text || placeholder.test(text) || suggested))
    return {empty:!busy, clear:true, draft:false, screen:busy ? "running" : "clear"};
  if (text) return {empty:false, clear:false, draft:true, draftText:text, editableText:busy ? null : text, screen:busy ? "active" : "draft"};
  return result;
}
