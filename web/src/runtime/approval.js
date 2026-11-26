import { request } from "../services/api";
import { engineCapabilities } from "./create.js";
import { byId } from "./dom.js";
import { paneAction, windowRoute } from "./pane.js";

export function labelApproval(pane) {
  const agent = pane.agent, profile = pane.approvalProfile;
  let label = agent.approval === "auto" ? "自动审批" : agent.approval === "ask" ? "需要审批" : "引擎默认";
  if (!agent.approval && agent.mode && agent.engine !== "copilot")
    label = profile?.modes.find(choice => choice.id === agent.mode)?.label || agent.mode;
  const output = pane.element.querySelector(".approval-current"), text = "当前模式：" + label;
  if (output.textContent !== text) output.textContent = text;
}

export async function chooseApproval(pane) {
  const agent = pane.agent, generation = agent.online ? pane.generation : null;
  const profile = (await engineCapabilities())[agent.engine];
  const policy = profile?.startupApproval;
  if (!policy) throw new Error("请更新工作台以切换审批权限");
  const dialog = byId("approval-dialog"), choice = byId("approval-choice");
  if (dialog.open) return;
  pane.approvalProfile = profile;
  byId("approval-title").textContent = "@" + agent.name + " · 切换审批";
  choice.value = agent.approval || (agent.mode === policy.auto ? "auto" : "ask");
  choice.onchange = () => {
    byId("approval-detail").textContent = choice.value === "auto" ? policy.description : "需要授权时，由原生 CLI 按当前规则向你询问。";
  };
  choice.onchange();
  byId("approval-restart-note").textContent = agent.online ?
    "重启后继续原会话。当前执行会中断，未发送的输入会清空。" : "将按所选权限打开这个 Agent 的终端。";
  byId("apply-approval").textContent = agent.online ? "重启并应用" : "启动并应用";
  dialog.returnValue = "";
  dialog.addEventListener("close", () => {
    if (dialog.returnValue !== "apply") return;
    paneAction(pane, () => request(windowRoute(pane) + "/approval", {
      method:"PUT", body:{terminal:generation, approval:choice.value}
    }));
  }, {once:true});
  dialog.showModal();
  choice.focus();
}
