import { request } from "../services/api";
import { byId, feedback, node, setBusy } from "./dom.js";
import { engineNames, insert, paneAction } from "./pane.js";

async function loadShortcuts(pane, menu) {
  const loading = node("p", "selection-empty", "正在读取…");
  menu.replaceChildren(loading);
  const engine = pane.agent.engine;
  try {
    const items = await request("/shortcuts/" + engine);
    if (!menu.contains(loading) || !menu.matches(":popover-open")) return;
    const buttons = items.map(({ command, note }) => {
      const button = node("button", "selection-item");
      button.type = "button";
      button.append(node("code", "", command), node("small", "", note));
      button.addEventListener("click", () => {
        menu.hidePopover();
        paneAction(pane, () => insert(pane, command));
      });
      return button;
    });
    const manage = node("button", "text-button", "管理快捷指令");
    manage.type = "button";
    manage.addEventListener("click", () => {
      menu.hidePopover();
      const dialog = byId("shortcuts-dialog");
      dialog.dataset.engine = engine;
      byId("shortcuts-title").textContent = engineNames[engine] + " 快捷指令";
      showShortcutChoices(items);
      feedback("shortcut-error");
      dialog.showModal();
    });
    menu.replaceChildren(...buttons, manage);
  } catch (error) { loading.textContent = error.message; }
}

function showShortcutChoices(items, selected = "") {
  const choice = byId("shortcut-choice");
  choice.replaceChildren(new Option("新增指令", ""), ...items.map(({ command, note }) => {
    const option = new Option(command, command);
    option.dataset.note = note;
    return option;
  }));
  choice.value = selected;
  editShortcut();
}

function editShortcut() {
  const choice = byId("shortcut-choice");
  const command = byId("shortcut-command");
  command.value = choice.value;
  command.readOnly = !!choice.value;
  byId("shortcut-note").value = choice.selectedOptions[0]?.dataset.note || "";
  byId("shortcut-delete").disabled = !choice.value;
}

async function changeShortcut(action) {
  const dialog = byId("shortcuts-dialog");
  if (dialog.dataset.busy === "true") return;
  const command = byId("shortcut-command").value;
  const body = action === "reset" ? { action } : { action, command, note: byId("shortcut-note").value };
  setBusy(dialog, true);
  feedback("shortcut-error");
  try {
    const items = await request("/shortcuts/" + dialog.dataset.engine, { method: "POST", body });
    showShortcutChoices(items, action === "save" ? command : "");
  } catch (error) { feedback("shortcut-error", error.message); }
  finally { setBusy(dialog, false); }
}

byId("shortcut-choice").addEventListener("change", editShortcut);
byId("shortcut-form").addEventListener("submit", event => { event.preventDefault(); changeShortcut("save"); });
byId("shortcut-delete").addEventListener("click", () => changeShortcut("delete"));
byId("shortcuts-reset").addEventListener("click", () => changeShortcut("reset"));

export { loadShortcuts };
