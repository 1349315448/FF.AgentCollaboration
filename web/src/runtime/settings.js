import { request } from "../services/api";
import { byId, node } from "./dom.js";
import { openFolders } from "./folders.js";
import { applyMentionShortcut, mentionChord } from "./mentions.js";
import { engineNames, installEngine, installGuides, updateGuides } from "../services/engines";
import { mentionShortcut, snapshot } from "../store/workbenchStore";
import { refresh, refreshing } from "./workbench.js";

const settingsDialog = byId("settings-dialog");
function settingsNote(text) { byId("settings-feedback").textContent = text; }

function settingsButton(label, action) {
  const button = node("button", "button", label);
  button.type = "button";
  button.onclick = async () => {
    button.disabled = true;
    settingsNote("");
    try { await action(button); }
    catch (error) { settingsNote(error.message); }
    finally { button.disabled = false; }
  };
  return button;
}

function renderPaths(paths) {
  const status = byId("settings-database");
  status.dataset.pending = String(!!paths.pending_restart);
  status.textContent = paths.active_data_dir ? "当前数据库文件 " + paths.active_data_dir + "\\workbench.sqlite" : "";
  if (paths.pending_restart) status.textContent += "\n已设置新数据库目录 " + paths.data_dir + "（当前数据已实时备份，重启工作台后正式切换）";
  const fields = [["archive_root","话题归档"],["handoffs","长消息交接"]];
  if (paths.data_dir) fields.unshift(["data_dir","数据库位置"]);
  byId("settings-paths").replaceChildren(...fields.map(([key, label]) => {
    const row = node("div", "path-setting"), input = node("input");
    input.id = "path-" + key;
    input.value = paths[key];
    const caption = node("label", "", label);
    caption.htmlFor = input.id;
    row.append(caption, input, settingsButton("选文件夹", () => openFolders(input.value, value => { input.value = value; })),
      settingsButton("恢复默认", () => { input.value = paths.defaults[key]; }));
    return row;
  }));
}

function statusRow(title, ok, status, detail, action) {
  const row = node("div", "health-row");
  const info = node("div", "health-info"), head = node("div", "health-title");
  head.append(node("strong", "", title), node("span", ok ? "health-ok" : "health-missing", status));
  info.append(head, node("small", "", detail));
  row.append(info);
  if (action) row.append(action);
  return row;
}

function engineRow(engine, installed) {
  const title = engine === "codex" ? "OpenAI Codex" : engineNames[engine];
  const actions = node("div", "engine-actions");
  const row = statusRow(title + " · " + engine, installed, installed ? "已安装" : "未安装", "", actions);
  const label = row.querySelector(".health-title span"), detail = row.querySelector("small");
  const release = node("small", "engine-release", "正在查询版本…");
  detail.after(release);
  const url = "/engines/" + engine;
  function command(kind) {
    const updating = kind === "update";
    return settingsButton(updating ? "更新" : "一键安装", async button => {
      for (const action of actions.children) action.disabled = true;
      button.textContent = updating ? "更新中…" : "安装中…";
      try {
        if (updating) await request(url + "/update", {method:"POST"});
        else await installEngine(engine);
        settingsNote(title + (updating ? " 已更新" : " 已安装"));
      } finally { await detect(false); }
    });
  }
  async function detect(refresh) {
    actions.replaceChildren();
    release.textContent = "正在查询版本…";
    release.dataset.status = "";
    try {
      const result = await request(url + "/release" + (refresh ? "?refresh=true" : ""));
      const item = result.installed;
      installed = item.installed;
      label.textContent = installed ? "已安装" : "未安装";
      label.className = installed ? "health-ok" : "health-missing";
      detail.textContent = installed ? [item.version || item.diagnostic || "版本暂不可读", item.path].filter(Boolean).join(" · ") : "";
      const suffix = installed ? {available:" · 有更新", current:" · 已是最新", ahead:" · 本地版本较新", unknown:" · 当前版本未识别"}[result.status] || "" : "";
      release.textContent = result.latestVersion ? "最新 " + result.latestVersion + suffix : "最新版本暂不可用";
      release.dataset.status = result.status;
      if (installed && result.status === "available") {
        if (result.canUpdate) actions.append(command("update"));
        else {
          const guide = installGuides[engine] || updateGuides[engine];
          if (guide) actions.append(settingsButton("更新指南 ↗", () => window.open(guide, "_blank", "noopener,noreferrer")));
        }
      }
    } catch { release.textContent = "版本检测暂不可用"; }
    if (!installed) {
      const guide = installGuides[engine];
      actions.append(guide ? settingsButton("安装指南", () => window.open(guide, "_blank", "noopener,noreferrer")) : command("install"));
    }
    actions.append(settingsButton("重新检测", () => detect(true)));
  }
  void detect(false);
  return row;
}

async function settingsEngines() {
  const installed = await request("/engines");
  byId("settings-engines").replaceChildren(...Object.entries(installed).map(([engine, present]) => engineRow(engine, present)));
}

async function settingsEnvironment() {
  const checks = await request("/environment");
  byId("settings-environment").replaceChildren(...checks.map(item => {
    let action;
    if (item.repair === "store") {
      action = node("a", "button", "安装 WinGet");
      action.href = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";
    } else if (item.repair) action = settingsButton("一键修复", async () => {
      await request("/environment/repair", {method:"POST", body:{item:item.id}});
      await settingsEnvironment();
    });
    return statusRow(item.name, item.ready, item.ready ? "✓ 正常" : "⚠ 待处理", item.description, action);
  }));
}

const cleanupButtons = [...byId("log-cleanup").querySelectorAll("button")];
let countTimer, countVersion = 0, cleaningLogs = false;

function logCount(total) {
  byId("log-count").textContent = "当前共 " + total + " 条协作日志记录";
}

async function readLogCount() {
  if (cleaningLogs || !settingsDialog.open) return;
  const version = ++countVersion;
  try {
    const {total} = await request("/settings/logs");
    if (version !== countVersion) return;
    logCount(total);
    cleanupButtons.forEach(button => { button.disabled = false; });
  } catch (error) {
    if (version !== countVersion) return;
    byId("log-count").textContent = "未能读取日志数量 · " + error.message;
    cleanupButtons.forEach(button => { button.disabled = true; });
  }
}

for (const button of cleanupButtons) button.onclick = async () => {
  const days = Number(button.dataset.days);
  if (days === 0 && !window.confirm("清除所有协作日志和无引用的托管交接文件？正在投递的消息会保留。")) return;
  cleaningLogs = true;
  ++countVersion;
  cleanupButtons.forEach(b => { b.disabled = true; });
  const status = byId("cleanup-result");
  try {
    const data = await request("/settings/logs/cleanup", { method:"POST", body:{days} });
    logCount(data.total);
    status.textContent = "已清理 " + data.deleted + " 条协作日志，释放托管交接文档 " + data.files + " 个" +
      (data.retained ? "。另有 " + data.retained + " 个文件暂时无法删除，下次清理时重试" : "");
    await refreshing;
    await refresh();
  } catch (error) { status.textContent = error.message; }
  finally {
    cleaningLogs = false;
    cleanupButtons.forEach(b => { b.disabled = false; });
  }
};
settingsDialog.addEventListener("close", () => { clearInterval(countTimer); ++countVersion; });

byId("open-settings").onclick = async () => {
  settingsDialog.showModal();
  resetMentionRecorder();
  byId("log-count").textContent = "正在统计…";
  byId("cleanup-result").textContent = "";
  cleanupButtons.forEach(button => { button.disabled = true; });
  clearInterval(countTimer);
  countTimer = setInterval(readLogCount, 5000);
  byId("save-paths").disabled = true;
  for (const id of ["settings-paths","settings-engines","settings-environment"]) byId(id).textContent = "正在检测…";
  settingsNote("");
  byId("settings-database").textContent = "";
  const results = await Promise.allSettled([
    request("/settings").then(paths => { renderPaths(paths); byId("save-paths").disabled = false; }),
    settingsEngines(), settingsEnvironment(), readLogCount()
  ]);
  if (results.some(result => result.status === "rejected")) settingsNote("部分配置未能读取。若刚更新工作台，请在下次启动后重新打开配置。");
};
byId("save-paths").onclick = async event => {
  const save = event.currentTarget;
  save.disabled = true;
  try {
    const paths = await request("/settings", {method:"PUT", body:{
      data_dir:byId("path-data_dir")?.value, archive_root:byId("path-archive_root").value, handoffs:byId("path-handoffs").value
    }});
    renderPaths(paths);
    settingsNote("目录已保存");
    await settingsEnvironment();
  } catch (error) { settingsNote(error.message); }
  finally { save.disabled = false; }
};
byId("close-settings").onclick = () => settingsDialog.close();

const mentionRecord = byId("mention-record"), mentionSave = byId("mention-save"), mentionReset = byId("mention-reset");
const mentionFeedback = byId("mention-key-feedback");
let recordedChord, recordingKey = false;

function resetMentionRecorder() {
  recordingKey = false;
  recordedChord = mentionShortcut;
  mentionRecord.textContent = recordedChord;
  mentionRecord.setAttribute("aria-pressed", "false");
  mentionSave.disabled = true;
  mentionRecord.disabled = mentionReset.disabled = !snapshot?.mentionShortcut;
  mentionFeedback.textContent = mentionRecord.disabled ? "下次启动工作台后可配置快捷键" : "";
}

mentionRecord.onclick = () => {
  recordingKey = !recordingKey;
  mentionRecord.textContent = recordingKey ? "请按组合键…" : recordedChord;
  mentionRecord.setAttribute("aria-pressed", String(recordingKey));
  mentionFeedback.textContent = recordingKey ? "按 Ctrl 或 Alt 搭配字母或数字，可加 Shift。Esc 取消。" : "";
};
mentionRecord.onkeydown = event => {
  if (!recordingKey || event.isComposing) return;
  if (event.key === "Tab") { mentionRecord.click(); return; }
  event.preventDefault();
  if (event.key === "Escape") { event.stopPropagation(); mentionRecord.click(); return; }
  if (["Control", "Shift", "Alt"].includes(event.key)) return;
  const chord = mentionChord(event);
  if (!chord || chord === "Alt+Shift+F") {
    mentionFeedback.textContent = chord ? "Alt+Shift+F 已用于选择文件" : "请使用 Ctrl 或 Alt 搭配字母或数字";
    return;
  }
  recordedChord = chord;
  mentionRecord.click();
  mentionSave.disabled = chord === mentionShortcut;
};
async function storeMentionKey(chord) {
  mentionRecord.disabled = mentionSave.disabled = mentionReset.disabled = true;
  try {
    const result = await request("/settings/mention-shortcut", {method:"PUT", body:{chord}});
    if (refreshing) await refreshing;
    await refresh();
    applyMentionShortcut(result.chord);
    resetMentionRecorder();
    mentionFeedback.textContent = chord ? "快捷键已保存" : "已恢复默认快捷键";
  } catch (error) {
    mentionRecord.disabled = mentionSave.disabled = mentionReset.disabled = false;
    mentionFeedback.textContent = error.message;
  }
}
mentionSave.onclick = () => storeMentionKey(recordedChord);
mentionReset.onclick = () => storeMentionKey(null);
