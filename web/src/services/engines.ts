import { request } from "./api";

export const engineNames: Record<string, string> = {
  codex: "Codex", copilot: "GitHub Copilot", claude: "Claude Code",
  codebuddy: "腾讯 CodeBuddy Code", traecli: "TRAE CLI", zcode: "智谱 ZCode", qwen: "千问 Qwen Code", qoder: "Qoder CLI",
  opencode: "OpenCode", cursor: "Cursor CLI"
};

export interface EngineCapabilities {
  modeLabel: string;
  modes: {id:string; label:string}[];
  sessionActions: string[];
  models: boolean;
  efforts: boolean;
  startupApproval?: {ask:string; auto:string; description:string};
}

export interface EngineModel {id:string; name:string; isDefault:boolean; efforts:string[]}
export interface EngineAccount {status:string; message?:string}

export function checkAccount(engine:string, project:string, signal?:AbortSignal) {
  return request<EngineAccount>("/engines/"+engine+"/account?"+new URLSearchParams({project}), {signal});
}
export const accountReady = (account:EngineAccount) => account.status==="authenticated" || account.status==="configured";

let capabilities: Promise<Record<string, EngineCapabilities>> | undefined;
export function engineCapabilities() {
  return capabilities ??= fetch("/api/engines/capabilities").then(response => {
    if (response.status === 404) return {};
    if (!response.ok) throw new Error("无法读取引擎能力");
    return response.json();
  }).catch(error => { capabilities = undefined; throw error; });
}

export const installGuides: Record<string, string> = {
  traecli: "https://docs.trae.cn/cli_get-started-with-trae-cli-2",
  zcode: "https://github.com/zai-org/ZCode/blob/main/apps/zcode-cli/docs/usage/01-quickstart.md",
  opencode: "https://opencode.ai/docs/",
  cursor: "https://cursor.com/docs/cli/installation"
};

export const traeAccountNotice = "当前引擎不支持个人账户登录。TRAE 中国版 CLI 2.0 仅向企业旗舰版账号开放。";

// npm 的收尾脚本有时晚于 CLI 就绪。只在本次安装期间探测选中的引擎。
export async function installEngine(engine: string) {
  const path = "/engines/" + engine, check = new AbortController();
  let timer: number | undefined;
  const ready = new Promise<void>(resolve => {
    async function probe() {
      try {
        if ((await request<{ready:boolean}>(path + "/installed", {signal:check.signal})).ready) { resolve(); return; }
      } catch { /* 安装命令仍负责返回最终结果。 */ }
      if (!check.signal.aborted) timer = window.setTimeout(probe, 2000);
    }
    timer = window.setTimeout(probe, 2000);
  });
  try { await Promise.race([request(path + "/install", {method:"POST"}), ready]); }
  finally { clearTimeout(timer); check.abort(); }
}

export const updateGuides: Record<string, string> = {
  codex: "https://developers.openai.com/codex/cli/",
  copilot: "https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli",
  claude: "https://code.claude.com/docs/en/setup",
  codebuddy: "https://www.codebuddy.ai/docs/cli/troubleshooting",
  qwen: "https://qwenlm.github.io/qwen-code-docs/en/users/support/troubleshooting/",
  qoder: "https://docs.qoder.com/cli/quick-start"
};
