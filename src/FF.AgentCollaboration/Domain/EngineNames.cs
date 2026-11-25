namespace FF.AgentCollaboration.Domain;

internal static class EngineNames
{
    internal static string Require(string? name) => Supports(name)
        ? name! : throw new WorkbenchError("请选择受支持的引擎");

    internal static readonly string[] All = ["codex", "copilot", "claude", "codebuddy", "traecli", "zcode", "qwen"];
    internal static bool Supports(string? name) => Array.IndexOf(All, name) >= 0;
    internal static bool HasPromptObserver(string name) => name is "codex" or "copilot" or "claude" or "traecli";
}
