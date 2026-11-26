// Claude 2.1 keeps the inverse caret on the first glyph of both an example and
// a real draft at Home. Its empty-composer shortcuts footer disappears on typing.
export function isClaudePlaceholder(term, lines, row, bottom) {
  const buffer = term.buffer.active;
  const prefix = /^❯\s/.exec(lines[row]);
  if (!prefix || buffer.cursorY !== row || buffer.cursorX !== prefix[0].length) return false;
  const caret = buffer.getLine(buffer.baseY + row)?.getCell?.(buffer.cursorX);
  if (!caret?.isInverse() || !lines.slice(bottom + 1).some(line => /^\s*\? for shortcuts\b/i.test(line))) return false;
  const example = [lines[row].slice(prefix[0].length), ...lines.slice(row + 1, bottom)].join(" ").trim();
  return /^Try "[^"]+"$/.test(example);
}
