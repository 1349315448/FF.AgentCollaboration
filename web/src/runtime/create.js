import { request } from "../services/api";
import { byId, feedback, folderName, setBusy } from "./dom.js";
import { openProjectFolders } from "./folders.js";
import { focusAgent } from "./layout.js";
import { engineCapabilities, engineNames, installEngine, installGuides } from "../services/engines";
import { loadQuota, quotas } from "./quota.js";
import { isDialogOpen, selectedProject, selectedTopicId, setSelectedProjectPath, snapshot } from "../store/workbenchStore";
import { openTopicDialog, selectedTopic } from "./topics.js";
import { normalizeActivities, refresh, render } from "./workbench.js";
import { accountReady, checkAccount, openEngineLogin } from "./engine-login.js";

const installingEngines = new Set();
const selectedEngine = () => byId("new-form").elements.engine.value;
let catalogRequest;
let models = [];
function setSelectedProject(path) {
  setSelectedProjectPath(path);
  byId("agent-project-name").textContent = folderName(path);
  byId("agent-project-path").textContent = path;
  byId("agent-project").title = path;
}

function openCreate() {
  if (!snapshot.project) { openProjectFolders(null); return; }
  if (!selectedTopic()) { openTopicDialog(); return; }
  byId("new-dialog").dataset.topic = selectedTopicId;
  byId("new-form").reset();
  setSessionSource();
  setSelectedProject(selectedTopic().project);
  feedback("create-error");
  byId("new-dialog").showModal();
  loadCatalogs();
}

// 仅用于静态文件先更新、宿主仍是上一版本的过渡阶段。
function previousHostOptions(engine, installed) {
  const modes = {
    codex:[["on-request","按需请求审批"],["never","不发起审批请求"]],
    copilot:[["agent","Agent"],["autopilot","Autopilot"]],
    claude:[["default","默认审批"],["acceptEdits","自动接受编辑"],["plan","计划"],["bypassPermissions","跳过权限确认"]]
  };
  return {modeLabel:"初始模式", modes:(modes[engine] || []).map(([id,label]) => ({id,label})),
    sessionActions:["new","resume", ...(engine === "copilot" ? ["latest"] : []), ...(Object.hasOwn(installed,"claude") ? ["fork"] : [])]};
}

function setSessionSource() {
  const sources = byId("session-source");
  sources.dataset.chooseDefault = "true";
  sources.value = "new";
}

function setHistoryOptions(select, choices) {
  select.replaceChildren(new Option(choices.length ? "选择历史会话" : "当前项目没有可选历史会话", ""));
  for (const choice of choices) select.add(new Option(choice.name.slice(0, 100), choice.id));
}

function renderEfforts() {
  const model = models.find(item => item.id === byId("agent-model").value)
    || (byId("agent-model").value ? null : models.find(item => item.isDefault));
  byId("agent-effort").replaceChildren(new Option("跟随模型默认", ""),
    ...(model?.efforts || []).map(effort => new Option(effort, effort)));
  byId("agent-effort").disabled = !model?.efforts?.length;
}

let createHistory = [];
function renderCreateHistory() {
  const select = byId("history-session"), previous = select.value;
  const fork = byId("new-form").elements.historyAction.value === "fork";
  const choices = createHistory.filter(s => fork || !s.mayBeInUse);
  setHistoryOptions(select, choices);
  if (choices.some(s => s.id === previous)) select.value = previous;
  byId("create-agent").disabled = !select.value;
}
byId("history-action").addEventListener("change", renderCreateHistory);
byId("history-session").addEventListener("change", () => { byId("create-agent").disabled = !byId("history-session").value; });

async function loadCatalogs() {
  catalogRequest?.abort();
  const controller = new AbortController();
  catalogRequest = controller;
  const engine = selectedEngine();
  byId("trae-create-notice").hidden = engine !== "traecli";
  byId("copilot-approval-field").hidden = true;
  const sources = byId("session-source");
  let history;
  let profile;
  const model = byId("agent-model");
  const sessions = byId("history-session");
  const submit = byId("create-agent");
  const summary = byId("catalog-status");
  const mode = byId("agent-mode");
  model.replaceChildren(new Option("跟随引擎默认", ""));
  model.disabled = true;
  models = [];
  renderEfforts();
  mode.replaceChildren(new Option("跟随引擎默认", ""));
  sessions.replaceChildren(new Option("正在读取…", ""));
  sessions.disabled = true;
  createHistory = [];
  submit.disabled = true;
  sources.disabled = mode.disabled = true;
  byId("history-field").hidden = byId("history-action").hidden = true;
  byId("open-engine-login").hidden = true;
  byId("refresh-quota").disabled = true;
  byId("engine-quota").replaceChildren();
  summary.textContent = "正在检查引擎登录状态…";
  byId("reload-catalog").hidden = true;
  try {
    const [installed, capabilities] = await Promise.all([request("/engines", {signal:controller.signal}), engineCapabilities()]);
    if (controller.signal.aborted) return;
    for (const button of byId("agent-engine").querySelectorAll("[data-install]")) {
      const id = button.dataset.install;
      button.closest(".engine-row").hidden = !Object.hasOwn(installed, id);
      button.hidden = installed[id];
      button.disabled = installingEngines.has(id);
      button.textContent = button.disabled ? "安装中…" : installGuides[id] ? "安装指南" : "安装";
    }
    profile = capabilities[engine] || previousHostOptions(engine, installed);
    const actions = profile.sessionActions;
    const latest = sources.querySelector('[value="latest"]');
    latest.hidden = latest.disabled = !actions.includes("latest");
    for (const [value, action] of [["new","new"],["history","resume"]]) {
      const option = sources.querySelector('[value="' + value + '"]');
      option.hidden = option.disabled = !actions.includes(action) && !(action === "resume" && actions.includes("fork"));
    }
    if (sources.dataset.chooseDefault) {
      sources.value = latest.disabled ? "new" : "latest";
      delete sources.dataset.chooseDefault;
    }
    history = sources.value === "history";
    sessions.disabled = !history;
    byId("history-field").hidden = byId("history-action").hidden = !history;
    byId("history-action").querySelector('[value="resume"]').disabled = !actions.includes("resume");
    const policy = profile?.startupApproval;
    const separate = engine === "copilot";
    byId("copilot-approval-field").hidden = !separate || !policy;
    byId("mode-label").textContent = separate ? "运行模式" : "审批权限";
    if (policy && !separate) {
      mode.add(new Option("需要审批", "approval:ask"));
      const automatic = new Option("自动审批", "approval:auto");
      automatic.title = policy.description;
      mode.add(automatic);
    }
    for (const choice of profile?.modes || []) {
      if (policy && !separate && [policy.ask,policy.auto].includes(choice.id)) continue;
      mode.add(new Option(choice.label, choice.id));
    }
    mode.closest("label").hidden = !profile?.modes?.length;
    model.closest("label").hidden = profile?.models === false;
    byId("agent-effort").closest("label").hidden = profile?.efforts === false;
    const fork = byId("history-action").querySelector('[value="fork"]');
    fork.disabled = !actions.includes("fork");
    if (!actions.includes("resume") && !fork.disabled) byId("new-form").elements.historyAction.value = "fork";
    if (fork.disabled) byId("new-form").elements.historyAction.value = "resume";
    fork.parentElement.title = fork.disabled ? "重新启动更新后的工作台即可创建分支" : "";
    if (!installed[engine]) {
      summary.textContent = installingEngines.has(engine) ? "正在安装 " + engineNames[engine] + "…" : engineNames[engine] + " 尚未安装";
      byId("engine-quota").replaceChildren();
      byId("refresh-quota").disabled = true;
      sessions.replaceChildren(new Option("请先安装引擎", ""));
      sessions.disabled = true;
      return;
    }
    const account = await checkAccount(engine, selectedProject, controller.signal);
    if (controller.signal.aborted) return;
    if (!accountReady(account)) {
      sources.value = "";
      sessions.disabled = true;
      byId("history-field").hidden = byId("history-action").hidden = true;
      byId("open-engine-login").hidden = byId("reload-catalog").hidden = false;
      byId("open-engine-login").hidden = account.status === "unavailable";
      summary.textContent = account.message || (account.status === "signed-out" ? "请先登录，再选择新建或恢复会话" : "无法确认账号状态，请重新检测或打开登录终端");
      return;
    }
    sources.disabled = mode.disabled = false;
    if (!sources.value) {
      summary.textContent = "请选择新建或恢复会话";
      sources.focus();
      return;
    }
  } catch (error) {
    if (controller.signal.aborted) return;
    summary.textContent = error.message;
    byId("reload-catalog").hidden = false;
    return;
  }
  // 使用引擎默认参数只依赖安装状态。读取模型/历史期间仍允许输入与新建。
  submit.disabled = history;
  loadQuota(engine);
  const params = "?" + new URLSearchParams({ project: selectedProject });
  const query = route => request(route + engine + params, { signal: controller.signal });
  const errors = [];
  const catalog = profile?.models !== false && query("/models/").then(choices => {
    if (controller.signal.aborted) return;
    models = choices;
    choices.forEach(choice => model.add(new Option(choice.name + (choice.isDefault ? "（默认）" : ""), choice.id)));
    model.disabled = false;
    renderEfforts();
  }).catch(error => { errors.push(error.message); });
  const saved = history && request("/sessions/" + engine + params + "&includeBound=true", {signal:controller.signal}).then(choices => {
    if (controller.signal.aborted) return;
    createHistory = choices;
    renderCreateHistory();
  }).catch(error => {
    if (controller.signal.aborted) return;
    sessions.replaceChildren(new Option("历史会话加载失败", ""));
    errors.push(error.message);
  });
  await Promise.all([catalog, saved]);
  if (controller.signal.aborted) return;
  summary.textContent = errors.join(" · ");
  byId("reload-catalog").hidden = !errors.length;
}


byId("empty-create").addEventListener("click", openCreate);
byId("agent-engine").addEventListener("change", () => {
  setSessionSource();
  loadCatalogs();
});
byId("agent-engine").addEventListener("click", async event => {
  const button = event.target.closest("[data-install]");
  if (!button || button.disabled) return;
  const engine = button.dataset.install;
  if (installGuides[engine]) { window.open(installGuides[engine], "_blank", "noopener,noreferrer"); return; }
  if (selectedEngine() !== engine) {
    byId("new-form").elements.engine.value = engine;
    setSessionSource();
  }
  installingEngines.add(engine);
  button.disabled = true;
  button.textContent = "安装中…";
  feedback("create-error");
  loadCatalogs();
  try {
    await installEngine(engine);
    if (!quotas.get(engine)?.loading) quotas.delete(engine);
  } catch (error) { feedback("create-error", error.message); }
  finally {
    installingEngines.delete(engine);
    button.disabled = false;
    button.textContent = "安装";
    if (isDialogOpen("new-dialog")) loadCatalogs();
  }
});
byId("refresh-quota").addEventListener("click", () => loadQuota(selectedEngine(), true));
byId("session-source").addEventListener("change", () => { delete byId("session-source").dataset.chooseDefault; loadCatalogs(); });
byId("agent-model").addEventListener("change", renderEfforts);
byId("reload-catalog").addEventListener("click", loadCatalogs);
byId("open-engine-login").onclick = async () => {
  if (await openEngineLogin(selectedEngine(), selectedProject)) loadCatalogs();
};
byId("new-dialog").addEventListener("close", () => {
  catalogRequest?.abort();
  if (snapshot) { normalizeActivities(snapshot); render(); }
});
byId("new-form").addEventListener("submit", async event => {
  event.preventDefault();
  const dialog = byId("new-dialog");
  const button = byId("create-agent");
  if (dialog.dataset.busy === "true" || button.disabled) return;
  const body = {
    topic: dialog.dataset.topic,
    name: byId("agent-name").value, role: byId("agent-role").value, project: selectedProject, engine: selectedEngine(),
    session: byId("session-source").value === "history" ? byId("history-session").value : null,
    newSession: byId("session-source").value === "new",
    forkSession: byId("session-source").value === "history" && byId("new-form").elements.historyAction.value === "fork",
    model: byId("agent-model").value || null, effort: byId("agent-effort").value || null,
    mode: byId("agent-mode").value.startsWith("approval:") ? null : byId("agent-mode").value || null,
    approval: selectedEngine() === "copilot" ? byId("copilot-approval").value || null :
      byId("agent-mode").value.split(":")[1] || null
  };
  catalogRequest?.abort();
  setBusy(dialog, true);
  button.disabled = true;
  button.textContent = "正在打开…";
  feedback("create-error");
  try {
    const agent = await request(body.forkSession ? "/windows/fork" : "/windows", { method: "POST", body });
    dialog.close();
    await refresh();
    focusAgent(agent.id);
  } catch (error) {
    feedback("create-error", error.message);
    if (error.status === 401) { setBusy(dialog, false); await loadCatalogs(); }
  }
  finally {
    setBusy(dialog, false);
    button.disabled = byId("session-source").disabled || !byId("session-source").value;
    button.textContent = "打开终端";
  }
});

export { engineCapabilities, loadCatalogs, openCreate, selectedEngine, setSelectedProject };
