import { request } from "../services/api";
import { byId, feedback, node } from "./dom.js";
import { openFolders } from "./folders.js";
import { startWorkbench } from "./workbench.js";

const setupDialog = byId("setup-dialog");
const setupFields = [["data_dir", "数据库位置"], ["archive_root", "话题归档"], ["handoffs", "长消息交接"]];

function enterWorkbench() {
  if (setupDialog.open) setupDialog.close();
  startWorkbench();
}

async function checkSetup() {
  const submit = byId("setup-submit"), retry = byId("setup-retry");
  try {
    const response = await fetch("/api/initialize", {cache:"no-store"});
    // 已在运行的旧宿主可直接使用更新后的静态资源。
    if (response.status === 404) { enterWorkbench(); return; }
    if (!response.ok) throw new Error("无法读取工作台初始化状态");
    const paths = await response.json();
    if (paths.initialized) { enterWorkbench(); return; }
    const fields = setupFields.map(([key, name]) => {
      const field = node("div", "setup-path"), group = node("div");
      const input = node("input"), label = node("label", "", name);
      label.htmlFor = input.id = "setup-" + key;
      input.name = key;
      input.value = paths[key];
      input.required = true;
      input.readOnly = key === "data_dir" && paths.fixed_data_dir;
      const choose = node("button", "button", "选文件夹");
      choose.type = "button";
      choose.disabled = input.readOnly;
      choose.onclick = () => openFolders(null, path => { input.value = path; });
      group.append(input, choose);
      field.append(label, group);
      return field;
    });
    byId("setup-paths").replaceChildren(...fields);
    feedback("setup-error");
    retry.hidden = true;
    submit.disabled = false;
  } catch (error) {
    feedback("setup-error", error.message);
    submit.disabled = true;
    retry.hidden = false;
  }
  if (!setupDialog.open) setupDialog.showModal();
}

byId("setup-retry").onclick = checkSetup;
setupDialog.addEventListener("cancel", event => event.preventDefault());
byId("setup-form").onsubmit = async event => {
  event.preventDefault();
  const submit = byId("setup-submit");
  if (submit.disabled) return;
  submit.disabled = true;
  feedback("setup-error");
  try {
    const paths = Object.fromEntries(new FormData(event.currentTarget));
    await request("/initialize", {method:"POST", body:paths});
    enterWorkbench();
  } catch (error) {
    // 另一浏览器页可能已经完成初始化。
    try { if ((await request("/initialize")).initialized) { enterWorkbench(); return; } } catch {}
    feedback("setup-error", error.message);
  } finally { submit.disabled = false; }
};

export { checkSetup };
