using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class CodeBuddyEngine(string cwd) : ConfiguredTerminalEngine
{
    protected override string ModeOption => "--permission-mode";
    internal override EngineCapabilities Capabilities => new("权限模式",
        [new("default", "默认审批"), new("plan", "计划"), new("acceptEdits", "接受编辑")],
        ["new", "resume", "fork", "archive", "restore"], NativeArchive: false, Efforts: false);

    internal override Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelChoice>>(new[] { "default-model", "fast-model", "balanced-model", "primary-model", "deep-model" }
            .Select(id => new ModelChoice(id, id, id == "default-model", [])).ToArray());

    internal override Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        var home = Environment.GetEnvironmentVariable("CODEBUDDY_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codebuddy");
        var root = Path.Combine(home, "projects");
        var sessions = new List<HistorySession>();
        if (Directory.Exists(root))
        {
            var active = ClaudeHistory.ActiveSessions(home);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
                {
                    try
                    {
                        if (ClaudeHistory.Read(file, cwd, ct) is { } item)
                            sessions.Add(item with { MayBeInUse = active.Contains(item.Id) });
                    }
                    catch (FileNotFoundException) { }
                }
            }
        }
        return Task.FromResult<IReadOnlyList<HistorySession>>(sessions);
    }
}
