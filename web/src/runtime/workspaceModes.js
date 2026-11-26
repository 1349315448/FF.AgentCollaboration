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
