import { request } from "../services/api";
import { engineCapabilities } from "./create.js";
import { checkAccount, accountReady, openEngineLogin } from "./engine-login.js";
import { byId, feedback, node, setBusy } from "./dom.js";
import { focusAgent } from "./layout.js";
import { engineNames, windowRoute } from "./pane.js";
import { panes } from "../services/terminalRegistry";
import { refresh } from "./workbench.js";

let paneSessionRequest, configRequest, catalogRequest;
let configModels = [];
let sessionPane;

let showArchivedSessions = false;
let deleteCandidate;

function cancelSessionDelete() {
  deleteCandidate = null;
  byId("session-delete-confirm").hidden = true;
}

function openPaneSessions(pane, tab = "history") {
  sessionPane = pane;
  showArchivedSessions = false;
  const dialog = byId("pane-session-dialog");
  configRequest?.abort();
  const instance = configRequest = new AbortController();
  byId("pane-session-title").textContent = "@" + pane.agent.name + " · Agent 设置";
  byId("rename-name").value = pane.agent.name;
  byId("config-role").value = pane.agent.role ?? "";
  byId("config-auto-start").checked = pane.agent.autoStart === true;
  byId("config-platform-update").checked = pane.agent.allowPlatformUpdate === true;
  byId("config-update-key").value = "";
  byId("config-update-key-field").hidden = true;
  byId("config-platform-update").onchange = () => {
    byId("config-update-key-field").hidden = byId("config-platform-update").checked === (pane.agent.allowPlatformUpdate === true);
  };
  const engines = byId("config-engine");
  for (const option of engines.options) {option.textContent = engineNames[option.value]; option.disabled = false;}
  engines.value = pane.agent.engine;
  request("/engines", {signal:instance.signal}).then(installed=>{
    if (instance.signal.aborted) return;
    for (const option of engines.options) {
      option.textContent = engineNames[option.value] + (installed[option.value] ? " · 已安装" : " · 未安装");
      option.disabled = !installed[option.value] && option.value !== pane.agent.engine;
    }
  }).catch(()=>{});
  feedback("rename-error");
  updateConfigEngine();
  const context = byId("pane-session-context");
  context.title = context.textContent = engineNames[pane.agent.engine] + " · " + pane.agent.project;
  window.dispatchEvent(new CustomEvent("ff:agent-config", { detail: pane.agent.id }));
  dialog.showModal();
  byId("pane-session-new").hidden = true;
  showConfigTab(tab);
}

function configHeaders() {
  const key=byId("config-update-key").value.trim();
  return key?{"X-FF-Update-Key":key}:undefined;
}

function showConfigTab(tab) {
  const history = tab === "history";
  byId("agent-config-basic").hidden = history;
  byId("agent-config-history").hidden = !history;
  for (const button of byId("pane-session-dialog").querySelectorAll("[data-config-tab]"))
    button.setAttribute("aria-selected", String(button.dataset.configTab === tab));
  if (history) loadDrawerHistory();
  else {paneSessionRequest?.abort(); byId("rename-name").focus();}
}

function configPayload() {
  const engine = byId("config-engine").value;
  const fields = {name:byId("rename-name").value, role:byId("config-role").value, engine, autoStart:byId("config-auto-start").checked,
    allowPlatformUpdate:byId("config-platform-update").checked};
  if (!byId("config-model").disabled && !byId("config-model-fields").hidden) {
    const model = byId("config-model").value || null, effort = byId("config-effort").value || null;
    if (engine !== sessionPane.agent.engine || model !== (sessionPane.agent.model ?? null) || effort !== (sessionPane.agent.effort ?? null))
      Object.assign(fields, {model, effort, updateModel:true});
  }
  if (engine !== sessionPane.agent.engine)
    for (const field of ["mode", "approval"]) fields[field] = byId("config-"+field).value || null;
  return fields;
}

function configEfforts() {
  const choice=byId("config-model").value;
  const model=configModels.find(m=>choice ? m.id===choice : m.isDefault);
  byId("config-effort").replaceChildren(new Option("跟随模型默认",""),...(model?.efforts??[]).map(e=>new Option(e,e)));
}

async function updateConfigEngine() {
  catalogRequest?.abort();
  const controller = catalogRequest = new AbortController();
  const engine = byId("config-engine").value;
  const {agent} = sessionPane;
  const changed = engine !== agent.engine;
  byId("config-launch-fields").hidden = false;
  const modelField = byId("config-model");
  modelField.disabled = true;
  byId("config-save").textContent = changed && agent.online ? "保存并重启终端" : "保存";
  byId("config-effect").textContent = changed && agent.online ? "切换引擎将重启当前终端" : "修改的模型和档位仅在下次启动时指定，之后跟随原生会话";
  byId("config-session-effect").textContent = agent.online ? "新建或切换会话将打开所选对话" : "下次打开时进入所选会话";
  const status=byId("config-catalog-status");
  configModels=[];
  for (const field of ["model","mode"]) byId("config-"+field).replaceChildren(new Option("跟随引擎默认",""));
  byId("config-approval").value="";
  configEfforts();
  status.textContent="正在读取引擎选项…";
  try {
    const caps=(await engineCapabilities())[engine];
    if (controller.signal.aborted) return;
    byId("config-approval").closest("label").hidden=!changed || !caps?.startupApproval;
    for(const mode of caps?.modes??[]) byId("config-mode").add(new Option(mode.label,mode.id));
    byId("config-mode").closest("label").hidden=!changed || !caps?.modes?.length;
    byId("config-model-fields").hidden = caps?.models === false;
    const models=caps?.models===false ? [] : await request("/models/"+engine+"?"+new URLSearchParams({project:agent.project}),{signal:controller.signal});
    if(controller.signal.aborted) return;
    configModels=models;
    for(const m of models) modelField.add(new Option(m.name,m.id));
    if (!changed && agent.model && !models.some(model=>model.id===agent.model))
      modelField.add(new Option(agent.model, agent.model));
    modelField.value = changed ? "" : agent.model ?? "";
    configEfforts();
    if (!changed && agent.effort) {
      const effort = byId("config-effort");
      if (![...effort.options].some(option=>option.value===agent.effort))
        effort.add(new Option(agent.effort, agent.effort));
      effort.value = agent.effort;
    }
    modelField.disabled = false;
    status.textContent="";
  } catch(error){if(!controller.signal.aborted) status.textContent=error.message;}
}

async function loadDrawerHistory() {
  paneSessionRequest?.abort();
  const controller = new AbortController();
  paneSessionRequest = controller;
  const {project} = sessionPane.agent;
  const engine = byId("config-engine").value;
  const archived = showArchivedSessions;
  const list = byId("pane-history"), status = byId("pane-session-status");
  cancelSessionDelete();
  feedback("pane-session-error");
  list.replaceChildren();
  status.textContent = "正在读取…";
  byId("pane-session-new").hidden = true;
  for (const tab of byId("pane-session-dialog").querySelectorAll("[data-history]"))
    tab.setAttribute("aria-pressed", String((tab.dataset.history === "archived") === archived));
  try {
    const account = await checkAccount(engine, project, controller.signal);
    if (controller.signal.aborted) return;
    if (!accountReady(account)) {
      status.textContent = account.status === "signed-out" ? "请先登录以选择会话 " : "暂时无法确认登录状态 ";
      const login = node("button", "button compact", "打开登录终端");
      login.type = "button";
      login.onclick = async () => { if (await openEngineLogin(engine, project) && !controller.signal.aborted) loadDrawerHistory(); };
      status.append(login);
      return;
    }
    const params = new URLSearchParams({project, archived, includeBound:true});
    const capabilities = await engineCapabilities();
    const actionsAllowed = capabilities[engine]?.sessionActions ?? ["new", "resume", "archive", "restore", "delete"];
    if (controller.signal.aborted) return;
    byId("pane-session-new").hidden = !actionsAllowed.includes("new");
    const sessions = await request("/sessions/" + engine + "?" + params, {signal:controller.signal});
    if (controller.signal.aborted) return;
    status.textContent = sessions.length ? "" : archived ? "暂无已归档会话" : "当前项目暂无历史会话";
    for (const session of sessions) {
      const row = node("li", "session-entry");
      const name = node("strong", "session-name", session.name);
      name.title = session.id;
      const date = new Date(session.updatedAt ?? "");
      const meta = [Number.isNaN(date.getTime()) ? null : date.toLocaleString(), session.mayBeInUse ? "已绑定或可能正在使用" : null];
      const actions = node("div", "session-actions");
      function add(label, action, style = "text-button") {
        if (!actionsAllowed.includes(action)) return;
        const button = node("button", style, label);
        button.type = "button";
        const ownOfflineSession = action === "resume" && !sessionPane.agent.online && engine === sessionPane.agent.engine && sessionPane.agent.session === session.id;
        button.disabled = session.mayBeInUse && (!ownOfflineSession || session.nativeInUse);
        button.onclick = () => {
          if (action === "delete") {
            deleteCandidate = session;
            byId("session-delete-name").textContent = session.name === session.id ? session.id : session.name + "\n" + session.id;
            byId("session-delete-confirm").hidden = false;
            byId("cancel-session-delete").focus();
          } else runSessionAction(action, session);
        };
        actions.append(button);
      }
      if (!archived) add("切换", "resume", "button compact");
      add(archived ? "还原" : "归档", archived ? "restore" : "archive");
      add("删除", "delete", "text-button delete-session");
      row.append(name, node("small", "session-meta", meta.filter(Boolean).join(" · ")), actions);
      list.append(row);
    }
  } catch (error) {
    if (!controller.signal.aborted) {
      status.textContent = "";
      feedback("pane-session-error", error.message);
    }
  }
}

async function runSessionAction(action, session) {
  const dialog = byId("pane-session-dialog");
  if (!dialog.open || dialog.dataset.busy === "true") return;
  const pane = sessionPane;
  paneSessionRequest?.abort();
  cancelSessionDelete();
  feedback("pane-session-error");
  setBusy(dialog, true);
  const controls = dialog.querySelectorAll(".new-agent-tabs,.session-tools,.session-scroll,.dialog-footer");
  controls.forEach(element => element.inert = true);
  try {
    if (action === "new" || action === "resume") {
      if (!byId("rename-name").value.trim()) { showConfigTab("basic"); byId("rename-name").reportValidity(); return; }
      await request(windowRoute(pane) + "/config", {method:"PUT",headers:configHeaders(), body:{...configPayload(), sessionMode:action, sessionId:session?.id}});
      dialog.close();
      await refresh();
      if (panes.has(pane.agent.id)) focusAgent(pane.agent.id);
    } else {
      await request(windowRoute(pane) + "/history/" + action, {method:"POST", body:{session:session.id, archived:showArchivedSessions, engine:byId("config-engine").value}});
      await loadDrawerHistory();
    }
  } catch (error) {
    await loadDrawerHistory();
    feedback("pane-session-error", error.message);
  } finally {
    setBusy(dialog, false);
    controls.forEach(element => element.inert = false);
  }
}

for (const tab of byId("pane-session-dialog").querySelectorAll("[data-history]")) {
  tab.onclick = () => { showArchivedSessions = tab.dataset.history === "archived"; loadDrawerHistory(); };
}
for (const tab of byId("pane-session-dialog").querySelectorAll("[data-config-tab]"))
  tab.onclick = () => showConfigTab(tab.dataset.configTab);
byId("config-model").onchange = configEfforts;
byId("config-engine").onchange = updateConfigEngine;
byId("agent-config-basic").onsubmit = async e => {
  e.preventDefault();
  const dialog=byId("pane-session-dialog");
  if (dialog.dataset.busy === "true") return;
  setBusy(dialog,true); byId("agent-config-basic").inert=true;
  try {
    await request(windowRoute(sessionPane)+"/config",{method:"PUT",headers:configHeaders(),body:configPayload()});
    dialog.close(); await refresh();
  } catch(error){feedback("rename-error",error.message);}
  finally {byId("agent-config-basic").inert=false;setBusy(dialog,false);}
};
byId("pane-session-dialog").addEventListener("close", () => { configRequest?.abort();catalogRequest?.abort();paneSessionRequest?.abort(); cancelSessionDelete(); });
byId("pane-session-reload").onclick = loadDrawerHistory;
byId("pane-session-new").onclick = () => runSessionAction("new");
byId("cancel-session-delete").onclick = cancelSessionDelete;
byId("confirm-session-delete").onclick = () => { if (deleteCandidate) runSessionAction("delete", deleteCandidate); };

export { openPaneSessions };
