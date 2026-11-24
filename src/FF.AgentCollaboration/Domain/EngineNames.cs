namespace FF.AgentCollaboration.Domain;

internal static class EngineNames
{
    internal static string Require(string? name) => Supports(name)
        ? name! : throw new WorkbenchError("请选择受支持的引擎");

    internal static bool Supports(string? name) => name is "codex" or "copilot" or "claude";
}
