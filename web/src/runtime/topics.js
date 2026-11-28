import { request } from "../services/api";
import { openCreate } from "./create.js";
import { byId, feedback, setBusy } from "./dom.js";
import { openFolders } from "./folders.js";
import { panes } from "../services/terminalRegistry";
import { applySplitRatio, focusAgent, readLayout } from "./layout.js";
import { openPaneSessions } from "./session-history.js";
import { navigation, projectKey } from "../services/navigation";
import { flushSync } from "react-dom";
import { layout, publishFrame, selectedTopicId, setTopic, snapshot } from "../store/workbenchStore";
import { refresh, refreshing, render } from "./workbench.js";

let editingTopicId;
const mobileProjects = matchMedia("(width < 900px)");
let activeProjectKey;
let projectSelection = Promise.resolve();
function selectProject(path, knowledgeMode) {
  return changeProject(() => request("/project", {method:"PUT", body:{path,knowledgeMode}}));
}

function queueSelection(action) {
  const change = projectSelection.then(action);
  projectSelection = change.catch(() => {});
  return change;
}

function changeProject(action) {
  return queueSelection(async () => {
    await action();
    await refreshing;
    await refresh();
    if (mobileProjects.matches) collapseProjects(true);
  });
}

function projectTab(path) {
  const key = projectKey(path);
  return Array.from(byId("project-tabs").querySelectorAll(".project-button")).find(row => row.dataset.key === key);
}

function selectedTopic() { return snapshot?.topics.find(topic => topic.id === selectedTopicId); }

function topicPreference(key, value) {
  try {
    if (value === undefined) return localStorage.getItem("lx.agent.workbench." + key);
    localStorage.setItem("lx.agent.workbench." + key, value);
  } catch { /* 存储不可用时仅在本页保留布局。 */ }
}

function applyTopic(topic) {
  setTopic(topic?.id, readLayout(topic));
  if (topic) topicPreference("selectedTopic." + projectKey(topic.project), topic.id);
  applySplitRatio();
}

let selectionVersion = 0;
let loadingTimer;
let selectionMotion;

function endNavigation() {
  clearTimeout(loadingTimer);
  byId("navigation-loading").hidden = true;
  byId("workspace-stage").removeAttribute("aria-busy");
}
window.addEventListener("ff:page-retired", () => { selectionVersion++; endNavigation(); }, {once:true});

function switchTopic(id, agentId) {
  const topic = snapshot.topics.find(t => t.id === id);
  if (!topic) return Promise.resolve();
  const version = ++selectionVersion;
  const current = () => version === selectionVersion;
  const alreadySwitching = byId("workspace-stage").hasAttribute("aria-busy");
  selectionMotion?.cancel();
  endNavigation();
  if (!alreadySwitching && !agentId && selectedTopicId === id && projectKey(topic.project) === activeProjectKey) return Promise.resolve();
  const stage = byId("workspace-stage");
  const agent = agentId && panes.get(agentId)?.agent;
  stage.setAttribute("aria-busy","true");
  byId("navigation-target").textContent = agent ? "正在切换到 @" + agent.name : "正在打开「" + topic.name + "」";
  loadingTimer = setTimeout(() => { if (current()) byId("navigation-loading").hidden = false; }, 150);
  let content;
  return queueSelection(async () => {
    if (!current()) return;
    const same = selectedTopicId === id;
    if (projectKey(topic.project) !== activeProjectKey) {
      await request("/project", {method:"PUT", body:{path:topic.project}});
      await refreshing;
      await refresh();
      if (!current()) return;
    }
    if (selectedTopicId !== id) {
      applyTopic(topic);
      render();
    }
    if (snapshot.timelineTopic !== id) content = refresh();
    if (mobileProjects.matches) collapseProjects(true);
    if (agentId) (same ? toggleAgent : focusAgent)(agentId);
    const surface = agentId && panes.get(agentId)?.element;
    const reveal = surface?.getClientRects().length ? surface.querySelector(".pane-header") : stage;
    if (!matchMedia("(prefers-reduced-motion: reduce)").matches)
      selectionMotion = reveal.animate([{opacity:.7},{opacity:1}], {duration:190,easing:"ease-out"});
  }).then(async () => {
    // An in-flight snapshot for the old topic can finish before the selected topic's snapshot.
    // Wait outside the project write queue so a newer selection can take over immediately.
    await content;
    if (content && current() && snapshot.timelineTopic !== id && refreshing) await refreshing;
  }).finally(() => { if (current()) endNavigation(); });
}

function renderTopics() {
  const key = snapshot.project ? projectKey(snapshot.project) : null;
  const topics = snapshot.topics.filter(t => projectKey(t.project) === key);
  let preferred = selectedTopicId;
  if (key !== activeProjectKey) {
    preferred = topicPreference("selectedTopic." + key) ?? topicPreference("selectedTopic");
    activeProjectKey = key;
    if (key) topicPreference("selectedProject", snapshot.project);
  }
  const topic = topics.find(t => t.id === preferred) ?? topics.find(t => t.status === "active") ?? topics[0];
  if (topic?.id !== selectedTopicId || !layout) applyTopic(topic);
  flushSync(publishFrame);
}

async function openTopicDialog(topic, project = snapshot.project) {
  editingTopicId = topic?.id;
  byId("topic-name").value = topic?.name || "";
  byId("topic-goal").value = topic?.goal || "";
  byId("topic-project").textContent = topic?.project || project;
  byId("topic-project").disabled = !!topic;
  byId("topic-title").textContent = topic ? "编辑小组" : "创建小组";
  byId("topic-submit").textContent = topic ? "保存" : "创建小组";
  feedback("topic-error");
  const choice = byId("topic-folder");
  choice.replaceChildren(new Option("自动创建小组目录", ""));
  choice.parentElement.hidden = !!topic;
  byId("topic-dialog").showModal();
  if (topic) byId("topic-name").select();
  if (!topic) {
    try {
      const folders = await request("/topic-folders");
      choice.replaceChildren(new Option("自动创建小组目录", ""), ...folders.map(f => new Option(f.name, f.path)));
    } catch (error) { feedback("topic-error", error.message); }
  }
}

function collapseProjects(collapsed) {
  const button = byId("project-collapse");
  byId("topic-shell").dataset.collapsed = String(collapsed);
  button.setAttribute("aria-expanded", String(!collapsed));
  button.title = collapsed ? "展开项目栏" : "收起项目栏";
  button.setAttribute("aria-label", button.title);
}

function removeProject(path) {
  if (!path || !window.confirm("从工作台移除这个项目？\n" + path + "\n文件、小组和 Agent 会话会保留。")) return;
  changeProject(() => request("/project?" + new URLSearchParams({path}), {method:"DELETE"}))
    .catch(error => feedback("connection-error", error.message));
}

async function archiveTopic(id) {
    const topic = snapshot.topics.find(t=>t.id===id);
    try {
      await request("/topics/" + topic.id + (topic.status === "finished" ? "/reopen" : "/finish"), { method: "POST" });
      await refresh();
    } catch (e) { feedback("connection-error", e.message); }
}

function toggleAgent(id) {
  const pane=panes.get(id);
  if (!pane) return;
  if (pane.element.dataset.collapsed==="true" || !pane.element.getClientRects().length ||
      layout.workspaceMode==="code" && layout.codeAgentHidden) return focusAgent(id);
  if (layout.workspaceMode==="code") { byId("inspector-panel-action").click(); return; }
  if (layout.activeWindowId!==id) return focusAgent(id);
  pane.element.querySelector(".toggle-collapse").click();
}

function attachTopicControls() {
  const initial = () => collapseProjects(mobileProjects.matches || topicPreference("projectRailCollapsed") === "true");
  initial();
  mobileProjects.addEventListener("change", initial);
  byId("project-collapse").onclick = () => {
    const collapsed = byId("topic-shell").dataset.collapsed !== "true";
    collapseProjects(collapsed);
    if (!mobileProjects.matches) topicPreference("projectRailCollapsed", String(collapsed));
  };
  Object.assign(navigation, {
    switchTopic:id=>switchTopic(id).catch(error=>feedback("connection-error",error.message)),
    removeProject, editTopic:id=>openTopicDialog(snapshot.topics.find(t=>t.id===id)),
    addTopic:path=>openTopicDialog(null,path), addAgent:openCreate, archiveTopic,
    selectAgent:(topic,id)=>switchTopic(topic,id).catch(error=>feedback("connection-error",error.message)),
    renameAgent:id=>{
      const pane = panes.get(id);
      if (pane) openPaneSessions(pane, "basic");
    },
    powerAgent:id=>panes.get(id)?.element.querySelector(".pane-power")?.click(),
    openAgentSessions:id=>panes.get(id)?.element.querySelector(".pane-sessions")?.click(),
  });
  byId("topic-project").onclick = () => openFolders(byId("topic-project").textContent, path => {
    byId("topic-project").textContent = path;
  });
  byId("topic-form").onsubmit = async event => {
    event.preventDefault();
    const dialog = byId("topic-dialog");
    if (dialog.dataset.busy === "true") return;
    const edit = editingTopicId;
    setBusy(dialog, true);
    try {
      const body = {name: byId("topic-name").value, goal: byId("topic-goal").value, project: byId("topic-project").textContent,
        folder: edit ? undefined : byId("topic-folder").value || null};
      const topic = await request(edit ? "/topics/" + edit : "/topics", {method: edit ? "PUT" : "POST", body});
      dialog.close();
      await refreshing;
      await refresh();
      if (!edit) {
        if (projectKey(topic.project) !== activeProjectKey) await selectProject(topic.project);
        applyTopic(topic);
        render();
        openCreate();
      }
    } catch (e) { feedback("topic-error", e.message); }
    finally { setBusy(dialog, false); }
  };
}

export { activeProjectKey, attachTopicControls, openTopicDialog, projectTab, renderTopics, selectProject, selectedTopic };
