using System.Text.Json;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal sealed class AntigravityEngine(string cwd) : Engine
{
    private const string Name = "antigravity";
    private static readonly Regex ModelLine = new(@"^(?<id>[a-z0-9]+(?:[._-][a-z0-9]+)+)(?:[ \t]+(?<label>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string? FindExecutable()
    {
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string installed = Path.Combine(local, "agy", "bin", "agy.exe");
        return Path.IsPathFullyQualified(installed) && File.Exists(installed) ? installed : WindowsCommand.Dependency("agy.exe");
    }

    internal override EngineCapabilities Capabilities { get; } = new("审批权限",
        [new("accept-edits", "自动接受编辑"), new("plan", "计划模式")], ["new"], NativeArchive: false, Efforts: false);
    internal override bool CreatesSessionBeforeLaunch => false;
    internal override bool SupportsDedicatedReader => false;
    internal override string[] LoginArguments => [];

    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        var (code, output) = await EngineAuthentication.Command(Name, cwd, ["models"], ct);
        if (output.Contains("Please sign in to view available models", StringComparison.OrdinalIgnoreCase))
            return new("signed-out");
        return new(code == 0 && Models(output).Any() ? "authenticated" : "unknown");
    }

    internal override async Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct)
    {
        var (code, output) = await EngineAuthentication.Command(Name, cwd, ["models"], ct);
        if (code != 0) throw new WorkbenchError("无法读取 Antigravity 模型，请在原生终端用 /model 查看");
        return Models(output).DistinctBy(model => model.Id, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<ModelChoice> Models(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ModelLine.Match(line);
            if (!match.Success || match.Groups["id"].Length > 160) continue;
            string id = match.Groups["id"].Value;
            yield return new(id, match.Groups["label"].Success ? match.Groups["label"].Value : id, false, []);
        }
    }

    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) =>
        Task.FromResult(new EngineQuota(false, "请在 Antigravity 终端使用 /usage 查看模型额度，使用 /credits 查看积分。"));

    // agy 的终端自行创建会话。恢复交给原生 /resume，避免多个窗口误用同一项目的最近会话。
    internal override Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived) =>
        Task.FromResult<IReadOnlyList<HistorySession>>([]);
    internal override Task ChangeHistory(string id, string action, CancellationToken ct) =>
        throw new WorkbenchError("请在 Antigravity 终端使用 /resume 管理会话");

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string>();
        if (window.Model is { } model) args.AddRange(["--model", model]);
        if (window.Mode is { } mode) args.AddRange(["--mode", mode]);
        return args;
    }

    internal override Task<TerminalLaunch> PrepareTerminal(AgentWindow window, EngineMcp mcp, string? newTitle,
        string? forkSource, bool reopening, CancellationToken ct)
    {
        if (NativeMcpInventory.Reserved(Name, cwd).Contains("lx_collab"))
            throw new WorkbenchError("Antigravity 原生配置已有 lx_collab，请移除同名配置后打开终端");

        string overlay = Path.Combine(mcp.Root, "engine-profiles", window.Id, "antigravity");
        string config = Path.Combine(overlay, ".agents", "mcp_config.json");
        TopicArchive.CheckPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        // The bridge inherits this ConPTY process's grant. The workspace overlay holds only its command.
        var servers = new { lx_collab = new { command = mcp.Command, args = mcp.Arguments } };
        TopicArchive.Publish(config, JsonSerializer.Serialize(new { mcpServers = servers }), overwrite: true);
        var args = NativeArguments(window, mcp, newTitle, forkSource);
        args.AddRange(["--add-dir", overlay]);
        return Task.FromResult(new TerminalLaunch(args, null));
    }
}
