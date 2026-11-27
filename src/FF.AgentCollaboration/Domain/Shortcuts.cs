namespace FF.AgentCollaboration.Domain;

internal sealed record Shortcut(string Command, string Note);
internal sealed record ChangeShortcut(string Action, string? Command, string? Note);

internal static class ShortcutCommands
{
    internal static List<Shortcut> Defaults(string engine)
    {
        if (engine == "qoder") return [new("/model", "选择模型"), new("/mcp", "查看 MCP 服务"), new("/usage", "查看额度")];
        if (engine == "antigravity") return [new("/model", "选择模型"), new("/resume", "恢复历史会话"),
            new("/usage", "查看模型额度"), new("/credits", "查看积分"), new("/mcp", "查看 MCP 服务"),
            new("/permissions", "管理审批权限")];
        if (engine == "cursor") return [new("/model", "选择模型"), new("/resume", "恢复历史会话"), new("/mcp list", "查看 MCP 服务"),
            new("/plan", "切换计划模式"), new("/ask", "切换只读问答模式"), new("/run-everything status", "查看自动运行设置")];
        if (engine is "qwen" or "codebuddy" or "traecli") return [new("/model", "选择模型"), new("/mcp", "查看 MCP 服务")];
        if (engine == "zcode") return [new("/help", "查看命令帮助"), new("/model", "选择模型")];
        if (engine == "claude") return [new("/model", "选择模型"), new("/status", "查看状态"),
            new("/plan", "计划模式"), new("/resume", "恢复会话"), new("/mcp", "查看 MCP 服务"), new("/compact", "压缩上下文")];
        return engine == "codex"
            ? [new("/model", "选择模型和推理档位"), new("/status", "查看会话配置和用量"), new("/plan", "切换到计划模式"),
               new("/resume", "恢复历史会话"), new("/mcp", "查看 MCP 工具"), new("/diff", "查看 Git 差异")]
            : [new("/model", "切换模型"), new("/plan", "制定实施计划"), new("/resume", "切换历史会话"),
               new("/mcp", "管理 MCP 服务"), new("/help", "查看命令帮助"), new("/context", "查看上下文用量")];
    }

    internal static void Apply(List<Shortcut> items, ChangeShortcut change)
    {
        if (change.Action is not ("save" or "delete")) throw new WorkbenchError("未知的快捷指令操作");
        string? command = change.Command;
        if (string.IsNullOrWhiteSpace(command) || !ValidShortcutLine(command))
            throw new WorkbenchError("指令须为 1 至 200 字的单行文本，不能含控制字符");
        int index = items.FindIndex(item => item.Command == command);
        if (change.Action == "delete")
        {
            if (index >= 0) items.RemoveAt(index);
        }
        else
        {
            string note = change.Note ?? "";
            if (!ValidShortcutLine(note)) throw new WorkbenchError("备注须为 200 字以内的单行文本，不能含控制字符");
            var item = new Shortcut(command, note);
            if (index < 0) items.Add(item);
            else items[index] = item;
        }
    }

    private static bool ValidShortcutLine(string line) =>
        line.Length <= 200 && !line.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');
}
