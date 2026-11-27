// xterm trims trailing cells on each wrapped row, sometimes removing a space in the marker.
export function foldedCodexChars(engine, writing, draft) {
  if (engine !== "codex" || !draft || (writing?.state !== "writing" && writing?.state !== "delivery_uncertain")) return undefined;
  if (!/\[Pasted\s*Content\s*\d+\s*chars\]/i.test(draft)) return undefined;
  const single = draft.trim().match(/^\[Pasted\s*Content\s*(\d+)\s*chars\]$/i);
  return single ? Number(single[1]) : 0;
}
