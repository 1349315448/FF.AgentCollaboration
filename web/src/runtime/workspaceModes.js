function ratio(value, fallback, min, max) {
  return Number.isFinite(value) ? Math.max(min, Math.min(max, value)) : fallback;
}

export function workspacePreferences(saved) {
  return {
    workspaceMode: saved?.workspaceMode === "code" ? "code" : "agent",
    agentInspectorOpen: saved?.agentInspectorOpen === true,
    agentInspectorRatio: ratio(saved?.agentInspectorRatio, .30, .22, .45),
    codeAgentRatio: ratio(saved?.codeAgentRatio, .35, .24, .50),
    codeAgentHidden: saved?.codeAgentHidden === true,
    codeActiveWindowId: typeof saved?.codeActiveWindowId === "string" ? saved.codeActiveWindowId : null
  };
}

export function codeViewport(ids, layout) {
  const active = [layout.codeActiveWindowId, layout.primaryWindowId, layout.activeWindowId].find(id => ids.includes(id)) ?? ids[0];
  return { active, folded: ids.filter(id => id !== active) };
}

export const terminalTileLimit = 9;

export function terminalTiles(opened, width, height, active) {
  const gap = 8;
  const minimumHeight = 180;
  const rows = Math.min(3, Math.max(1, Math.floor((height + gap) / (minimumHeight + gap))));
  // 双栏给终端保留 240px 可用宽度及边框空间，三栏留出更宽的阅读区。
  const twoColumnsWidth = 2 * 244 + gap;
  const threeColumnsWidth = 3 * 300 + 2 * gap;
  const availableColumns = width < twoColumnsWidth ? 1 : width < threeColumnsWidth ? 2 : 3;
  const stackedPair = opened.length === 2 && rows >= 2;
  const preferredColumns = stackedPair ? 1 : opened.length > 4 ? 3 : 2;
  const columns = Math.min(availableColumns, preferredColumns, opened.length || 1);
  const visible = opened.slice(0, Math.min(terminalTileLimit, rows * columns));
  if (opened.includes(active) && !visible.includes(active)) visible[visible.length - 1] = active;
  return { columns, visible };
}
