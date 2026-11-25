import { request } from "../services/api";
import { openCreate } from "./create.js";
import { byId, feedback, setBusy } from "./dom.js";
import { openFolders } from "./folders.js";
import { applySplitRatio, focusAgent, readLayout } from "./layout.js";
import { navigation, projectKey } from "../services/navigation";
import { flushSync } from "react-dom";
import { layout, publishFrame, selectedTopicId, setTopic, snapshot } from "../store/workbenchStore";
import { refresh, refreshing, render } from "./workbench.js";

let editingTopicId;
const mobileProjects = matchMedia("(width < 900px)");
let activeProjectKey;
let projectSelection = Promise.resolve();
function selectProject(path) {
  return changeProject(() => request("/project", {method:"PUT", body:{path}}));
}

function changeProject(action) {
  // 将快速连续的点击按顺序提交，避免较早的 PUT 覆盖最后一次选择。
  const change = projectSelection.then(async () => {
    await action();
    await refreshing;
    await refresh();
    if (mobileProjects.matches) collapseProjects(true);
  });
  projectSelection = change.catch(() => {});
  return change;
}

function projectTab(path) {
  const key = projectKey(path);
  return Array.from(byId("project-tabs").children).find(tab => tab.dataset.key === key);
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

function switchTopic(id) {
  const topic = snapshot.topics.find(t => t.id === id);
  if (!topic || id === selectedTopicId) return;
  applyTopic(topic);
  render();
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

async function openTopicDialog(edit = false) {
  const topic = edit ? selectedTopic() : null;
  editingTopicId = topic?.id;
  byId("topic-name").value = topic?.name || "";
  byId("topic-goal").value = topic?.goal || "";
  byId("topic-project").textContent = topic?.project || snapshot.project;
  byId("topic-project").disabled = !!topic;
  byId("topic-title").textContent = topic ? "编辑话题" : "新建话题";
  byId("topic-submit").textContent = topic ? "保存" : "创建话题";
  feedback("topic-error");
  const choice = byId("topic-folder");
  choice.replaceChildren(new Option("自动创建话题目录", ""));
  choice.parentElement.hidden = !!topic;
  byId("topic-dialog").showModal();
  if (!edit) {
    try {
      const folders = await request("/topic-folders");
      choice.replaceChildren(new Option("自动创建话题目录", ""), ...folders.map(f => new Option(f.name, f.path)));
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
  if (!path || !window.confirm("从工作台移除这个项目？\n" + path + "\n文件、话题和 Agent 会话会保留。")) return;
  changeProject(() => request("/project?" + new URLSearchParams({path}), {method:"DELETE"}))
    .catch(error => feedback("connection-error", error.message));
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
    switchTopic, focusAgent, removeProject,
    selectProject: path => selectProject(path).catch(error => feedback("connection-error", error.message))
  });
  byId("topic-new").onclick = () => openTopicDialog();
  byId("topic-edit").onclick = () => openTopicDialog(true);
  byId("topic-project").onclick = () => openFolders(byId("topic-project").textContent, path => {
    byId("topic-project").textContent = path;
  });
  byId("topic-state").onclick = async () => {
    const topic = selectedTopic();
    try {
      await request("/topics/" + topic.id + (topic.status === "finished" ? "/reopen" : "/finish"), { method: "POST" });
      await refresh();
    } catch (e) { feedback("connection-error", e.message); }
  };
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
