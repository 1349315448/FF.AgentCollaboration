const topRule = /^─{3,}(?:\s.+\s─+)?$/;
const bottomRule = /^─{3,}$/;
const dialogFooter = /^(?:enter (?:confirm|continue(?: and create sandbox)?|select)\s*\|\s*esc (?:quit|go back|cancel)|\[press any key to continue\])$/i;
const execution = /^\s*[^\w\s▍>❯›]\s.+\((?:\d+[hms]\s*)+[•·]\s*esc to interrupt\)\s*$/i;
const queueHeader = /^\s*Send after tool call\s*·\s*esc to send now\s*·\s*↑ to edit\s*$/i;
const suggestions = new WeakMap();

export function forgetTraeSuggestion(term) { suggestions.delete(term); }
export function invalidateTraeDraft(term) {
  const previous = suggestions.get(term);
  if (previous) {
    previous.echo = null;
    previous.confirmed = false;
  }
}

// TRAE fullscreen uses ❯ between rules. Its inline TUI still uses the Codex-style ›.
export function readTraePrompt(term, lines) {
  const unavailable = {empty:false, clear:false, draft:false, screen:"unknown"};
  const last = lines.findLastIndex(line => line.trim());
  if (dialogFooter.test(lines[last]?.trim())) return {...unavailable, screen:"blocked"};
  const row = lines.findLastIndex((line, i) => i > 0 && /^❯(?: |$)/.test(line) && topRule.test(lines[i - 1].trim()));
  if (row < 0) return null;
  const bottom = lines.findIndex((line, i) => i > row && bottomRule.test(line.trim()));
  if (bottom < 0 || !term.modes.bracketedPasteMode) return unavailable;

  const editor = [lines[row].slice(2), ...lines.slice(row + 1, bottom)];
  const text = editor.join("\n").trim();
  if (text === "Input disabled until setup completes.") return {...unavailable, screen:"blocked"};
  const footer = lines.slice(bottom + 1, last + 1);
  if (!footer.length || !footer.some(line => /(?:\? for shortcuts|\bContext\s+\d+%)/i.test(line))) return unavailable;
  const controls = lines.slice(Math.max(0, row - 12), row - 1);
  const queue = controls.findLastIndex(line => queueHeader.test(line));
  const entries = queue < 0 ? [] : controls.slice(queue + 1);
  const entry = /^\s+❯\s/;
  const nativeQueue = queue > 0 && execution.test(controls[queue - 1]) && entry.test(entries[0] || "") &&
    entries.every(line => entry.test(line) || /^\s{4,}\S/.test(line));
  const busy = controls.some(line => execution.test(line));
  const busyComposer = nativeQueue || execution.test(controls.at(-1) || "");
  const queuedId = nativeQueue ? entries.findLast(line => entry.test(line))?.match(/^\s+❯\s+MSG_ID:\s*([0-9a-f]{8})\b/i)?.[1]?.toLowerCase() : undefined;
  const buffer = term.buffer.active;
  const atStart = buffer.cursorY === row && buffer.cursorX === 2;
  // Suggested examples rotate. The native composer draws every visible glyph dim; user text at Home keeps normal intensity.
  let suggestion = atStart && !!text;
  for (let y = row; suggestion && y < bottom; y++) {
    const line = buffer.getLine(buffer.baseY + y);
    if (!line?.getCell) { suggestion = false; break; }
    for (let x = y === row ? 2 : 0; x < term.cols; x++) {
      const cell = line.getCell(x);
      if (cell?.getChars().trim() && !cell.isDim()) { suggestion = false; break; }
    }
  }
  // ConPTY can drop dim after submit. Reuse only the previous suggestion, after a NEW
  // native user transcript row echoes the draft. This also covers a human-started turn.
  let previous = suggestions.get(term);
  if (suggestion && previous?.text !== text) {
    previous = {text};
    suggestions.set(term, previous);
  }
  const echoes = prefix => lines.slice(0, row - 1).filter(line => line.trimStart().startsWith("▍ " + prefix)).length;
  if (previous && atStart && previous.text === text && previous.echo) {
    previous.confirmed ||= echoes(previous.echo) > previous.count ||
      (!!queuedId && /^MSG_ID:\s*([0-9a-f]{8})$/i.exec(previous.echo)?.[1]?.toLowerCase() === queuedId);
    suggestion ||= previous.confirmed;
  } else if (previous && text && !suggestion && buffer.cursorY >= row && buffer.cursorY < bottom) {
    const id = /^MSG_ID:\s*[0-9a-f]{8}\b/i.exec(text)?.[0];
    const echo = id || editor[0].trim().slice(0, 48);
    if (echo !== previous.echo) previous.count = echoes(echo);
    previous.echo = echo;
    previous.confirmed = false;
  }
  if (atStart && (!text || suggestion))
    return {empty:!busy, clear:true, draft:false, screen:busy ? "active" : "clear", busyInput:busyComposer, queuedId};
  if (text) return {empty:false, clear:false, draft:true, draftText:text, editableText:busy ? null : text,
    busyEditable:busyComposer && buffer.cursorY >= row && buffer.cursorY < bottom && !atStart,
    screen:busy ? "active" : "draft", queuedId};
  return busy ? {...unavailable, screen:"active"} : unavailable;
}
