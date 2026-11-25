export const engineNames: Record<string, string> = {
  codex: "Codex", copilot: "GitHub Copilot", claude: "Claude Code",
  codebuddy: "腾讯 CodeBuddy Code", traecli: "TRAE CLI", zcode: "智谱 ZCode", qwen: "千问 Qwen Code"
};

export const installGuides: Record<string, string> = {
  traecli: "https://docs.trae.cn/cli_get-started-with-trae-cli-2",
  zcode: "https://github.com/zai-org/ZCode/blob/main/apps/zcode-cli/docs/usage/01-quickstart.md"
};

export const manualInboxEngines = new Set(["qwen", "codebuddy", "zcode"]);
