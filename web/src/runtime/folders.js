import { request } from "../services/api";
import { loadCatalogs, setSelectedProject } from "./create.js";
import { byId, feedback, node, setBusy } from "./dom.js";
import { selectedProject, snapshot } from "../store/workbenchStore";
import { selectProject } from "./topics.js";

let folderRequest;
let folderListing;
let pickFolder;
function openFolders(path, onPick) {
  pickFolder = onPick;
  byId("folder-dialog").showModal();
  loadFolders(path);
}

async function loadFolders(path) {
  folderRequest?.abort();
  const controller = new AbortController();
  folderRequest = controller;
  folderListing = undefined;
  const list = byId("folder-list");
  byId("folder-path").value = path || "";
  byId("choose-folder").disabled = true;
  byId("folder-up").disabled = true;
  list.replaceChildren(node("p", "selection-empty", "正在读取…"));
  feedback("folder-error");
  try {
    const query = path ? "?" + new URLSearchParams({ path }) : "";
    const listing = await request("/folders" + query, { signal: controller.signal });
    if (controller.signal.aborted) return;
    folderListing = listing;
    byId("folder-path").value = listing.path || "";
    byId("folder-up").disabled = !listing.path;
    byId("choose-folder").disabled = !listing.path;
    list.replaceChildren(...listing.folders.map(folder => {
      const button = node("button", "selection-item");
      button.type = "button";
      button.append(node("span", "folder-icon"), node("span", "", folder.name), node("small", "", "›"));
      button.addEventListener("click", () => loadFolders(folder.path));
      return button;
    }));
    if (!listing.folders.length) list.append(node("p", "selection-empty", "这个文件夹没有子文件夹"));
  } catch (error) {
    if (controller.signal.aborted) return;
    list.replaceChildren();
    feedback("folder-error", error.message);
  }
}

byId("add-project").addEventListener("click", () => openFolders(snapshot.project, selectProject));
byId("agent-project").addEventListener("click", () => {
  if (byId("new-dialog").dataset.busy !== "true")
    openFolders(selectedProject, path => { setSelectedProject(path); loadCatalogs(); });
});
byId("folder-path-form").addEventListener("submit", event => {
  event.preventDefault();
  loadFolders(byId("folder-path").value.trim() || null);
});
byId("folder-path").addEventListener("input", () => {
  byId("choose-folder").disabled = byId("folder-path").value !== folderListing?.path;
});
byId("folder-disks").addEventListener("click", () => loadFolders(null));
byId("folder-up").addEventListener("click", () => loadFolders(folderListing?.parent || null));
byId("folder-dialog").addEventListener("close", () => folderRequest?.abort());
byId("choose-folder").addEventListener("click", async () => {
  const dialog = byId("folder-dialog");
  const button = byId("choose-folder");
  button.disabled = true;
  setBusy(dialog, true);
  try { await pickFolder(folderListing.path); dialog.close(); }
  catch (error) { feedback("folder-error", error.message); }
  finally { setBusy(dialog, false); button.disabled = false; }
});

export { folderListing, openFolders };
