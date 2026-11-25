import { request } from "../services/api";
import { byId } from "./dom.js";
import { canIgnore } from "./pane.js";
import { refresh, refreshing } from "./workbench.js";

function showPreview(item) {
  const source = (item.fromTopic ? item.fromTopic + "/" : "") + item.from;
  const sections = ["[来自 @" + source + "] " + item.text];
  if (item.document) sections.push("交接文档：" + item.document);
  if (item.files.length) sections.push("附带文件：\n" + item.files.join("\n"));
  if (item.returnHint) sections.push("回送提示：" + item.returnHint);
  byId("preview-content").textContent = sections.join("\n\n");
  byId("copy-preview").textContent = "复制消息";
  const dialog = byId("preview-dialog"), dismiss = byId("dismiss-preview");
  const status = byId("preview-status");
  status.textContent = "";
  dismiss.hidden = item.acknowledged !== false || !canIgnore(item);
  dismiss.disabled = false;
  dismiss.onclick = async () => {
    dismiss.disabled = true;
    try {
      await request("/windows/" + item.target + "/inbox/" + item.id + "/acknowledge", {method:"POST"});
      dialog.close();
      await refreshing;
      await refresh();
    } catch (error) {
      status.textContent = error.message;
      dismiss.disabled = false;
    }
  };
  dialog.showModal();
}
byId("copy-preview").addEventListener("click", async () => {
  await navigator.clipboard.writeText(byId("preview-content").textContent);
  byId("copy-preview").textContent = "已复制";
});

export { showPreview };
