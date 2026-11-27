import { projectKey } from "./navigation";

export const knowledgeModes = {
  disabled: "暂不启用",
  shared: "共享提交",
  local_only: "仅本机私有"
} as const;

export type KnowledgeMode = keyof typeof knowledgeModes;

export const knowledgeUiLocal = location.hostname === "127.0.0.1";

export function knowledgeModeFor(modes: Record<string, KnowledgeMode> | undefined, project?: string) {
  if (!project || !modes) return "disabled";
  const key = projectKey(project);
  return modes[project] ?? Object.entries(modes).find(([path]) => projectKey(path) === key)?.[1] ?? "disabled";
}
