using System.Text.Json;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal sealed partial class CursorEngine(string cwd) : Engine
{
    private bool pluginSupported;

    internal static string? FindExecutable()
    {
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Path.IsPathFullyQualified(local))
        {
            string installation = Path.Combine(local, "cursor-agent");
            foreach (string name in new[] { "agent.exe", "agent.cmd", "cursor-agent.exe", "cursor-agent.cmd" })
            {
                string path = Path.Combine(installation, name);
                if (File.Exists(path)) return path;
            }
        }
        // A generic agent.exe or agent.cmd elsewhere on PATH may belong to another product.
        return WindowsCommand.Dependency("cursor-agent.exe", "cursor-agent.cmd");
    }

    internal override EngineCapabilities Capabilities => new("初始模式",
        [new("plan", "计划"), new("ask", "只读问答")], ["new", "resume"], NativeArchive:false, Efforts:false);
    internal override string ApprovalKeys => "";

    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        var (code, status) = await EngineAuthentication.Command("cursor", cwd, ["status"], ct);
        status = Ansi().Replace(status, "");
        bool signedOut = SignedOut().IsMatch(status);
        if (code == 0 && !signedOut && Authenticated().IsMatch(status)) return new("authenticated");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CURSOR_API_KEY")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CURSOR_AUTH_TOKEN")))
        {
            // Some Cursor versions show browser-login status even when the CLI has a usable API key.
            var (modelsCode, models) = await EngineAuthentication.Command("cursor", cwd, ["--list-models"], ct);
            if (modelsCode == 0 && ModelLine().IsMatch(Ansi().Replace(models, ""))) return new("configured");
        }
        return new(signedOut ? "signed-out" : "unknown");
    }

    internal override async Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct)
    {
        var (code, output) = await EngineAuthentication.Command("cursor", cwd, ["--list-models"], ct);
        if (code != 0) throw new WorkbenchError("无法读取此账号的 Cursor 模型列表");
        return ModelLine().Matches(Ansi().Replace(output, "")).Select(match =>
        {
            string id = match.Groups["id"].Value;
            string name = match.Groups["name"].Value.Trim();
            return new ModelChoice(id, name, ModelDefault().IsMatch(name), []);
        }).DistinctBy(model => model.Id, StringComparer.Ordinal).ToArray();
    }

    internal override async Task<string> NewSession(string title, string? model, string? source, CancellationToken ct)
    {
        await RequirePluginSupport(ct);
        var (code, output) = await EngineAuthentication.Command("cursor", cwd, ["create-chat"], ct);
        if (code == 0 && Guid.TryParseExact(Ansi().Replace(output, "").Trim(), "D", out var id)) return id.ToString("D");
        throw new WorkbenchError("Cursor 未返回有效的新会话编号", WorkbenchFailure.Unavailable);
    }

    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) =>
        Task.FromResult(new EngineQuota(false, "Cursor CLI 未公开剩余额度查询。请在 Cursor 账户的用量页面查看。"));

    // Cursor's ls and /resume are interactive pickers. The workbench only binds an exact native chat ID.
    internal override Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived) =>
        Task.FromResult<IReadOnlyList<HistorySession>>([]);

    internal override Task ChangeHistory(string id, string action, CancellationToken ct) =>
        throw new WorkbenchError("请在 Cursor 中管理会话历史");

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string>();
        if (window.Session is { } session) args.Add("--resume=" + session);
        if (window.Model is { } model) args.AddRange(["--model", model]);
        if (window.Mode is "plan" or "ask") args.AddRange(["--mode", window.Mode]);
        return args;
    }

    internal override async Task<TerminalLaunch> PrepareTerminal(AgentWindow window, EngineMcp mcp, string? newTitle,
        string? forkSource, bool reopening, CancellationToken ct)
    {
        await RequirePluginSupport(ct);
        var args = NativeArguments(window, mcp, newTitle, forkSource);
        string root = Path.Combine(mcp.Root, "engine-profiles", window.Id, "cursor-plugin");
        string manifest = Path.Combine(root, ".cursor-plugin", "plugin.json");
        TopicArchive.CheckPath(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        TopicArchive.Publish(manifest, """{"name":"ff-agent-collaboration","description":"FF Agent Collaboration for the current terminal"}""", overwrite:true);

        var bridge = new { type = "stdio", command = mcp.Command, args = mcp.Arguments,
            env = new { LX_P0_WINDOW_GRANT = "${env:LX_P0_WINDOW_GRANT}" } };
        var servers = mcp.Servers("cursor", bridge);
        // Store placeholders only. Cursor fills them from this terminal's process environment.
        var environment = new Dictionary<string, string>();
        foreach (var entry in servers.Values.OfType<Dictionary<string, object?>>())
        {
            foreach (string field in new[] { "env", "headers" })
            {
                if (entry.GetValueOrDefault(field) is not Dictionary<string, string?> values) continue;
                foreach (string key in values.Keys.ToArray())
                {
                    if (string.IsNullOrEmpty(values[key])) continue;
                    string variable = $"FF_CURSOR_MCP_{environment.Count}";
                    environment.Add(variable, values[key]!);
                    values[key] = "${env:" + variable + "}";
                }
            }
        }
        string config = Path.Combine(root, "mcp.json");
        TopicArchive.CheckPath(config);
        TopicArchive.Publish(config, JsonSerializer.Serialize(new { mcpServers = servers }), overwrite:true);
        args.AddRange(["--plugin-dir", root]);
        return new(args, environment);
    }

    private async Task RequirePluginSupport(CancellationToken ct)
    {
        if (pluginSupported) return;
        var (code, help) = await EngineAuthentication.Command("cursor", cwd, ["--help"], ct);
        if (code != 0 || !help.Contains("--plugin-dir", StringComparison.Ordinal))
            throw new WorkbenchError("此版本的 Cursor CLI 不支持启动时加载协作插件。请按官方指南更新 Cursor CLI 后重试。", WorkbenchFailure.Unavailable);
        pluginSupported = true;
    }

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();
    [GeneratedRegex(@"\b(?:Logged in as|Login successful|Authenticated as)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Authenticated();
    [GeneratedRegex(@"\b(?:Not logged in|Not authenticated|Please log in)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignedOut();
    [GeneratedRegex(@"^[ \t]*(?<id>[A-Za-z0-9][A-Za-z0-9._:/-]{0,199})[ \t]+-[ \t]+(?<name>[^\x00-\x1f\x7f]{1,300})\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ModelLine();
    [GeneratedRegex(@"\([^()]*\bdefault\b[^()]*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelDefault();
}
