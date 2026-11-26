// Older Copilot versions paint this instruction in the composer. A normal draft can
// contain the identical text, so help text alone is insufficient even with the caret at Home.
const helpText = "Type @ to mention files, # for issues/PRs, / for commands, or ? for shortcuts".replace(/\s/g, "");

export function isCopilotPlaceholder(term, lines, row, bottom) {
  const buffer = term.buffer.active;
  const prompt = lines.slice(row, bottom).map(line => line.replace(/^[>❯│┃]\s?/, ""));
  if (prompt.join("").replace(/\s/g, "") !== helpText) return false;
  const start = lines[row].length - prompt[0].length;
  if (buffer.cursorY !== row || buffer.cursorX !== start) return false;
  for (let i = row; i < bottom; i++) {
    const raw = lines[i];
    const line = buffer.getLine(buffer.baseY + i);
    if (!line?.getCell) return false;
    for (let x = raw.length - prompt[i - row].length; x < raw.length; x++) {
      const cell = line.getCell(x);
      if (!cell || (cell.getChars().trim() && !(cell.isDim() || (cell.isFgPalette() && cell.getFgColor() === 8)))) return false;
    }
  }
  return true;
}
