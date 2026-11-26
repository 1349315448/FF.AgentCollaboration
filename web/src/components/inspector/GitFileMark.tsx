import type { GitChange } from "./gitWorkspace";

const marks = {
  added:["+", "新增"], modified:["M","已修改"], deleted:["−","已删除"],
  conflict:["!","冲突"], renamed:["R","重命名"], copied:["C","已复制"]
} as const;
export function fileKind(change:GitChange) {
  if(change.group==="conflict") return "conflict";
  switch(change.code) {
    case "?": case "A": return "added";
    case "D": return "deleted";
    case "R": return "renamed";
    case "C": return "copied";
    default: return "modified";
  }
}
export function GitFileMark({change}:{change:GitChange}) {
  const [symbol,label]=marks[fileKind(change)];
  return <span className="git-file-mark" aria-label={label} title={label}>{symbol}</span>;
}
