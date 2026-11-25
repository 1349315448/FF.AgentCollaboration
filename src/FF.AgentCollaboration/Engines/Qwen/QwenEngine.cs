using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal sealed class QwenEngine(string cwd) : ConfiguredTerminalEngine
{
    protected override string ModeOption => "--approval-mode";
    internal override EngineCapabilities Capabilities => new("审批模式",
        [new("default", "默认审批"), new("plan", "计划"), new("auto-edit", "自动编辑")],
        ["new", "resume", "archive", "restore"], NativeArchive: false, Models: false, Efforts: false);
    internal override Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelChoice>>([]);

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        var active = (await Records(["sessions", "ps", "--json"], ct))
            .Select(row => (string?)row["sessionId"]).ToHashSet();
        var sessions = new List<HistorySession>();
        foreach (var row in await Records(["sessions", "list", "--json", "--limit", "1000"], ct))
        {
            string? id = (string?)row["sessionId"], project = (string?)row["cwd"];
            if (!Guid.TryParse(id, out _) || project == null || !ProjectFolders.Same(project, cwd)) continue;
            string title = (string?)row["customTitle"] ?? (string?)row["prompt"] ?? id!;
            sessions.Add(new(id!, title, cwd, row["startTime"]?.ToString()) { MayBeInUse = active.Contains(id) });
        }
        return sessions;
    }

    private async Task<List<JsonNode>> Records(string[] arguments, CancellationToken ct)
    {
        var start = WindowsCommand.Resolve(EngineExecutable.FindExecutable("qwen"), arguments);
        start.WorkingDirectory = cwd;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new WorkbenchError("无法启动千问 CLI");
        process.BeginErrorReadLine();
        try
        {
            var records = new List<JsonNode>();
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                if (!string.IsNullOrWhiteSpace(line) && JsonNode.Parse(line) is JsonObject row) records.Add(row);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new WorkbenchError("千问 CLI 读取会话失败，请检查安装版本");
            return records;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
