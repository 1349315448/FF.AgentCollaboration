"use strict";

let selectedTopicId;
let editingTopicId;
let topicSignature;
const topicChoices = new Map();
const mobileProjects = matchMedia("(width < 900px)");
let activeProjectKey;
let projectSelection = Promise.resolve();

function projectKey(path) {
  return path.replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();
}

function selectProject(path) {
  // 将快速连续的点击按顺序提交，避免较早的 PUT 覆盖最后一次选择。
  const change = projectSelection.then(async () => {
    await request("/project", {method: "PUT", body: {path}});
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

const projectMeasure = document.createElement("canvas").getContext("2d");
function fitProjectPath(name) {
  const path = name.dataset.path, width = name.clientWidth;
  projectMeasure.font = getComputedStyle(name).font;
  if (!width || projectMeasure.measureText(path).width <= width) { name.textContent = path; return; }
  const chars = Array.from(path);
  function clipped(keep) {
    const head = Math.max(1, Math.floor(keep / 3));
    return chars.slice(0, head).join("") + "...." + chars.slice(chars.length - keep + head).join("");
  }
  let low = 2, high = chars.length - 1;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    if (projectMeasure.measureText(clipped(middle)).width <= width) low = middle;
    else high = middle - 1;
  }
  name.textContent = clipped(low);
}
const projectTitleResize = new ResizeObserver(entries => entries.forEach(entry => fitProjectPath(entry.target)));

function renderProjects(counts) {
  const nav = byId("project-tabs");
  const before = nav.querySelector("#current-project")?.dataset.key;
  const totals = new Map(snapshot.projects.map(path => [projectKey(path), {activity: "idle", unread: 0, topics: 0, agents: 0, working: 0, idle: 0, offline: 0}]));
  for (const topic of snapshot.topics) {
    const total = totals.get(projectKey(topic.project));
    if (!total) continue;
    const count = counts.get(topic.id);
    total.topics++;
    total.agents += count.agents;
    total.unread += count.unread;
    for (const [, , state] of count.members) {
      if (state === "offline") total.offline++;
      else if (state === "idle") total.idle++;
      else total.working++;
    }
    if (topic.activity === "working") total.activity = "working";
    else if (total.activity !== "working" && (topic.activity === "waiting" || total.unread)) total.activity = "waiting";
  }
  const previous = new Map(Array.from(nav.children, tab => [tab.dataset.key, tab]));
  snapshot.projects.forEach((path, index) => {
    const key = projectKey(path), active = key === activeProjectKey, total = totals.get(key);
    let tab = previous.get(key);
    if (!tab) {
      tab = node("button", "project-button");
      tab.type = "button";
      tab.dataset.key = key;
      const heading = node("span", "project-card-title");
      heading.append(statusDot(), node("strong"), node("span", "tag-inbox"));
      const states = node("span", "project-states");
      for (const label of ["工作", "空闲", "离线"]) {
        const cell = node("span", "", label + " ");
        cell.append(node("b"));
        states.append(cell);
      }
      tab.append(heading, node("span", "project-summary"), states);
      tab.onclick = () => selectProject(tab.title).catch(error => feedback("connection-error", error.message));
    }
    previous.delete(key);
    if (nav.children[index] !== tab) nav.insertBefore(tab, nav.children[index] || null);
    tab.title = path;
    tab.dataset.activity = total.activity;
    tab.setAttribute("aria-pressed", String(active));
    const summary = "会话 " + total.topics + " · Agent " + total.agents;
    tab.querySelector(".project-summary").textContent = summary;
    const values = [total.working, total.idle, total.offline];
    tab.querySelectorAll(".project-states b").forEach((cell, i) => { cell.textContent = values[i]; });
    tab.setAttribute("aria-label", path + " · " + summary + " · 工作 " + total.working + " · 空闲 " + total.idle +
      " · 离线 " + total.offline + " · " + total.unread + " 条待处理");
    const name = tab.querySelector("strong"), badge = tab.querySelector(".tag-inbox");
    tab.id = active ? "current-project" : "";
    name.id = active ? "project-name" : "";
    if (name.dataset.path !== path) {
      name.dataset.path = name.title = path;
      name.textContent = path;
      projectTitleResize.observe(name);
      fitProjectPath(name);
    }
    badge.hidden = total.unread === 0;
    badge.textContent = total.unread;
  });
  for (const tab of previous.values()) { projectTitleResize.unobserve(tab.querySelector("strong")); tab.remove(); }
  byId("add-project").disabled = false;
  if (before !== activeProjectKey) byId("current-project")?.scrollIntoView({block:"nearest", inline:"nearest"});
}

function selectedTopic() { return snapshot?.topics.find(topic => topic.id === selectedTopicId); }

function topicPreference(key, value) {
  try {
    if (value === undefined) return localStorage.getItem("lx.agent.workbench." + key);
    localStorage.setItem("lx.agent.workbench." + key, value);
  } catch { /* 存储不可用时仅在本页保留布局。 */ }
}

function applyTopic(topic) {
  selectedTopicId = topic?.id;
  if (topic) topicPreference("selectedTopic." + projectKey(topic.project), topic.id);
  layout = readLayout(topic);
  applySplitRatio();
}

function switchTopic(id) {
  const topic = snapshot.topics.find(t => t.id === id);
  if (!topic || id === selectedTopicId) return;
  applyTopic(topic);
  render();
}

function createTopicChoice(id) {
  const button = node("button", "topic-choice");
  button.type = "button";
  button.dataset.topic = id;
  button.onclick = () => switchTopic(id);
  const label = node("span", "topic-label"), count = node("small", "topic-count");
  const members = node("span", "topic-agents"), badge = node("span", "tag-inbox");
  button.append(label, node("span", "topic-running", "进行中"), members, badge, count);
  return {button, label, count, members, badge, option:new Option("", id)};
}

function updateTopicMembers(choice, topicId, members) {
  const roster = JSON.stringify(members.map(([id, name]) => [id, name]));
  if (choice.roster !== roster) {
    choice.roster = roster;
    choice.members.replaceChildren();
    choice.members.hidden = !members.length;
    for (const [id, name] of members) {
      const item = node("span", "topic-agent-tag");
      item.dataset.agent = id;
      item.title = "@" + name;
      item.append(statusDot(), node("span", "member-name", item.title));
      item.onclick = event => {
        event.stopPropagation();
        switchTopic(topicId);
        focusAgent(id);
      };
      choice.members.append(item);
    }
  }
  members.forEach((member, index) => {
    const item = choice.members.children[index];
    if (item.dataset.activity !== member[2]) {
      item.dataset.activity = member[2];
      item.title = "@" + member[1] + " · " + activityLabels[member[2]];
    }
  });
}

// [T08] 保留标签、焦点和状态灯，只在项目或话题顺序实际变化时移动节点。
function renderTopics() {
  const key = projectKey(snapshot.project);
  const topics = snapshot.topics.filter(t => projectKey(t.project) === key);
  byId("topic-new").disabled = false;
  let preferred = selectedTopicId;
  if (key !== activeProjectKey) {
    preferred = topicPreference("selectedTopic." + key) ?? topicPreference("selectedTopic");
    activeProjectKey = key;
    topicPreference("selectedProject", snapshot.project);
  }
  const topic = topics.find(t => t.id === preferred) ?? topics.find(t => t.status === "active") ?? topics[0];
  if (topic?.id !== selectedTopicId || !layout) applyTopic(topic);
  const counts = new Map(snapshot.topics.map(t => [t.id, { agents: 0, unread: 0, members: [] }]));
  for (const view of snapshot.windows) {
    const count = counts.get(view.agent.topicId);
    if (!count) continue;
    count.agents++;
    count.unread += view.inbox.filter(m => m.state === "pending" || m.state === "delivery_uncertain").length;
    count.members.push([view.agent.id, view.agent.name, view.activity]);
  }
  renderProjects(counts);
  byId("topic-heading").hidden = !topic;
  byId("active-topic-goal").textContent = topic?.status === "finished" ? topic.finishSummary || topic.goal || "" : topic?.goal || "";
  const topicWorking = topic?.activity === "working";
  byId("topic-activity").textContent = topicWorking ? "进行中" : "";
  byId("topic-activity").hidden = !topicWorking;
  byId("topic-activity").dataset.activity = topic?.activity || "";
  renderTimeline(snapshot.timelineTopic === topic?.id ? snapshot.timeline : []);
  byId("topic-state").textContent = topic?.status === "finished" ? "重新打开" : "归档";
  byId("topic-state").title = topic?.status === "finished" ? "重新打开话题" : "结束并归档话题";
  const sig = JSON.stringify([key, selectedTopicId, topics.map(t => [t.id,t.name,t.status,t.activity,counts.get(t.id)])]);
  if (sig === topicSignature) return;
  topicSignature = sig;
  const list = byId("topic-list"), select = byId("mobile-topic");
  const changedTopic = list.querySelector('[aria-pressed="true"]')?.dataset.topic !== selectedTopicId;
  const currentIds = new Set(topics.map(t => t.id));
  for (const [id, choice] of topicChoices) {
    if (currentIds.has(id)) continue;
    choice.button.remove();
    choice.option.remove();
    topicChoices.delete(id);
  }
  let tabIndex = 0;
  for (const finished of [false, true]) {
    const group = topics.filter(t => (t.status === "finished") === finished);
    const options = select.children[Number(finished)];
    options.hidden = !group.length;
    group.forEach((t, optionIndex) => {
      const {agents, unread, members} = counts.get(t.id);
      const selected = t.id === selectedTopicId;
      const choice = topicChoices.get(t.id) || createTopicChoice(t.id);
      topicChoices.set(t.id, choice);
      const {button, label, count, badge, option} = choice;
      const detail = t.name + " · " + activityLabels[t.activity] + " · " + agents + " 个 Agent";
      const title = detail + " · " + unread + " 条待处理";
      if (button.title !== title) {
        button.title = title;
        button.setAttribute("aria-label", title);
      }
      button.dataset.finished = String(finished);
      button.dataset.activity = t.activity;
      button.setAttribute("aria-pressed", String(selected));
      if (label.textContent !== t.name) label.textContent = t.name;
      const memberCount = agents + " 位";
      if (count.textContent !== memberCount) count.textContent = memberCount;
      const unreadLabel = unread > 99 ? "99+" : unread ? String(unread) : "";
      if (badge.textContent !== unreadLabel) badge.textContent = unreadLabel;
      updateTopicMembers(choice, t.id, members);
      if (!finished || selected) {
        const next = list.children[tabIndex++];
        if (next !== button) list.insertBefore(button, next || null);
      } else button.remove();
      const description = unread ? title : detail;
      if (option.text !== description) option.text = description;
      const nextOption = options.children[optionIndex];
      if (nextOption !== option) options.insertBefore(option, nextOption || null);
    });
  }
  select.value = selectedTopicId || "";
  select.disabled = !topic;
  if (changedTopic) requestAnimationFrame(() => {
    const current = list.querySelector('[aria-pressed="true"]');
    if (!current || !list.clientWidth) return;
    const tab = current.getBoundingClientRect(), strip = list.getBoundingClientRect();
    if (tab.right > strip.right) list.scrollLeft += Math.ceil(tab.right - strip.right);
    else if (tab.left < strip.left) list.scrollLeft -= Math.ceil(strip.left - tab.left);
  });
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

function attachTopicControls() {
  const initial = () => collapseProjects(mobileProjects.matches || topicPreference("projectRailCollapsed") === "true");
  initial();
  mobileProjects.addEventListener("change", initial);
  byId("project-collapse").onclick = () => {
    const collapsed = byId("topic-shell").dataset.collapsed !== "true";
    collapseProjects(collapsed);
    if (!mobileProjects.matches) topicPreference("projectRailCollapsed", String(collapsed));
  };
  byId("topic-new").onclick = () => openTopicDialog();
  byId("topic-edit").onclick = () => openTopicDialog(true);
  byId("mobile-topic").onchange = event => switchTopic(event.target.value);
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
