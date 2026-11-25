import { request } from "../services/api";
import { byId, feedback, node } from "./dom.js";
import { insert, paneAction } from "./pane.js";

let fileRequest;
let fileTimer;
let filePane;
const selectedFiles = new Set();
function openFiles(pane) {
  filePane = pane;
  selectedFiles.clear();
  byId("file-query").value = "";
  byId("files-dialog").showModal();
  loadFiles();
}

async function loadFiles() {
  fileRequest?.abort();
  const controller = new AbortController();
  fileRequest = controller;
  const list = byId("file-options");
  list.replaceChildren(node("p", "selection-empty", "正在查找…"));
  feedback("file-error");
  refreshSelection();
  try {
    const params = new URLSearchParams({ project: filePane.agent.project, q: byId("file-query").value.trim() });
    const result = await request("/files?" + params, { signal: controller.signal });
    if (controller.signal.aborted) return;
    list.replaceChildren(...result.files.map(path => {
      const item = node("label", "selection-item file-item");
      const box = node("input");
      box.type = "checkbox";
      box.checked = selectedFiles.has(path);
      box.addEventListener("change", () => {
        if (box.checked) selectedFiles.add(path);
        else selectedFiles.delete(path);
        refreshSelection(result.truncated);
      });
      item.append(box, node("span", "", path));
      return item;
    }));
    if (!result.files.length) list.append(node("p", "selection-empty", "没有匹配的文件"));
    refreshSelection(result.truncated);
  } catch (error) {
    if (controller.signal.aborted) return;
    list.replaceChildren();
    feedback("file-error", error.message);
  }
}

function refreshSelection(truncated = false) {
  byId("file-status").textContent = "已选 " + selectedFiles.size + " 个" + (truncated ? " · 可继续缩小搜索范围" : "");
  byId("insert-files").disabled = !selectedFiles.size;
}

byId("file-query").addEventListener("input", () => {
  fileRequest?.abort();
  clearTimeout(fileTimer);
  fileTimer = setTimeout(loadFiles, 180);
});
byId("files-dialog").addEventListener("close", () => { fileRequest?.abort(); clearTimeout(fileTimer); });
byId("insert-files").addEventListener("click", () => {
  const text = Array.from(selectedFiles, path => /\s/.test(path) ? '"' + path + '"' : path).join(" ") + " ";
  const pane = filePane;
  byId("files-dialog").close();
  paneAction(pane, () => insert(pane, text));
});

export { openFiles };
