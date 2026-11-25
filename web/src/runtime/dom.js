const byId = id => document.getElementById(id);
function node(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text != null) element.textContent = text;
  return element;
}

function feedback(id, message) {
  const target = typeof id === "string" ? byId(id) : id;
  target.textContent = message || "";
  target.hidden = !message;
}

function folderName(path) {
  return path.replace(/[\\/]+$/, "").split(/[\\/]/).at(-1) || path;
}

function setBusy(dialog, busy) {
  dialog.dataset.busy = String(busy);
  dialog.querySelectorAll("[data-close]").forEach(button => { button.disabled = busy; });
}

export { byId, feedback, folderName, node, setBusy };
