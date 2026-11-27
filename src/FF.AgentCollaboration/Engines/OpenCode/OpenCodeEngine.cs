using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal sealed class OpenCodeEngine(string cwd) : Engine
{
    private const string Name = "opencode";
    private const string InlineConfig = "OPENCODE_CONFIG_CONTENT";
    private static readonly Regex VersionNumber = new(@"^(?:opencode2?[\s/v]+)?v?(\d+\.\d+\.\d+)(?:[-+][A-Za-z0-9.+-]+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ModelId = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.:/-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SessionId = new(@"^ses_[A-Za-z0-9]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyCredentials = new(@"\b[1-9]\d*\s+credentials?\b", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyEnvironment = new(@"\b[1-9]\d*\s+environment variables?\b", RegexOptions.CultureInvariant);
    private Version? detectedVersion;

    internal static string? FindExecutable()
    {
        var executable = WindowsCommand.Dependency("opencode.exe", "opencode.cmd", "opencode2.exe", "opencode2.cmd");
        if (executable != null) return ResolveNpmShim(executable);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var prefixes = new[] { Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX"), Path.Combine(roaming, "npm"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(local, "opencode", "bin") };
        var installed = prefixes.Where(path => Path.IsPathFullyQualified(path ?? ""))
            .SelectMany(path => new[] { "opencode.exe", "opencode.cmd", "opencode2.exe", "opencode2.cmd" }
                .Select(name => Path.Combine(path!, name)))
            .FirstOrDefault(File.Exists);
        return installed == null ? null : ResolveNpmShim(installed);
    }

    private static string ResolveNpmShim(string shim)
    {
        if (!Path.GetExtension(shim).Equals(".cmd", StringComparison.OrdinalIgnoreCase)) return shim;
        var overridePath = Environment.GetEnvironmentVariable("OPENCODE_BIN_PATH");
        if (Path.IsPathFullyQualified(overridePath ?? "") && File.Exists(overridePath)) return overridePath!;
        try
        {
            // 如同 npm 包的入口，优先启动本机原生程序，省去多余的 Node/CMD 和 ConPTY 包装进程。
            if (new FileInfo(shim).Length > 16_384) return shim;
            string contents = File.ReadAllText(shim).Replace('/', '\\');
            string? package = contents.Contains(@"node_modules\opencode-ai\bin\", StringComparison.OrdinalIgnoreCase) ? "opencode-ai"
                : contents.Contains(@"node_modules\@opencode\cli\bin\", StringComparison.OrdinalIgnoreCase) ? "@opencode/cli" : null;
            if (package == null) return shim;
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string platform = package == "opencode-ai" ? $"opencode-windows-{arch}" : $"@opencode/cli-windows-{arch}";
            string[] variants = arch == "x64" ? Avx2.IsSupported ? [platform, platform + "-baseline"] : [platform + "-baseline"] : [platform];
            string modules = Path.Combine(Path.GetDirectoryName(shim)!, "node_modules");
            foreach (string root in new[] { Path.Combine(modules, package, "node_modules"), modules })
                foreach (string variant in variants)
                    foreach (string exe in new[] { "opencode.exe", "opencode2.exe" })
                    {
                        string candidate = Path.Combine(root, variant, "bin", exe);
                        if (File.Exists(candidate)) return candidate;
                    }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return shim;
    }

    internal override EngineCapabilities Capabilities => new("审批权限", [], ["new", "resume", "archive", "restore"],
        NativeArchive: false, Efforts: false);
    internal override string[] LoginArguments => ["auth", "login"];
    internal override string ApprovalKeys => "";
    internal override bool ValidSessionId(string id) => SessionId.IsMatch(id);

    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        var version = await SupportedVersion(ct);
        if (version.Major == 1)
        {
            var (authCode, auth) = await Command(["auth", "list"], ct);
            if (authCode == 0 && LegacyCredentials.IsMatch(auth)) return new("authenticated");
            if (authCode == 0 && LegacyEnvironment.IsMatch(auth)) return new("configured");
        }
        // OpenCode supports free models and environment-supplied API keys without a saved login.
        var (code, output) = await Query(["models"], ct);
        return new(code != 0 ? "unknown" : Models(output).Any() ? "configured" : "signed-out");
    }

    internal override async Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct)
    {
        var (code, output) = await Query(["models"], ct);
        if (code != 0) throw new WorkbenchError("暂时无法读取 OpenCode 模型列表", WorkbenchFailure.Unavailable);
        return Models(output).Select(id => new ModelChoice(id, id, false, [])).ToArray();
    }

    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) => Task.FromResult(new EngineQuota(false,
        "OpenCode 使用所选模型提供方的额度，请到提供方查看。"));

    internal override async Task<string> NewSession(string title, string? model, string? source, CancellationToken ct)
    {
        if (source != null) throw new WorkbenchError("此处可使用 OpenCode 原生会话创建或恢复");
        await SupportedVersion(ct);
        using var rpc = new RpcPipe(EngineExecutable.FindExecutable(Name), ["acp"], cwd, null);
        using var stop = ct.Register(rpc.Abort);
        try
        {
            await rpc.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, ct);
            var response = await rpc.Request("session/new", new { cwd, mcpServers = Array.Empty<object>() }, ct);
            if ((string?)response["sessionId"] is { } id && ValidSessionId(id)) return id;
            throw new WorkbenchError("OpenCode 未返回可恢复的会话编号", WorkbenchFailure.Unavailable);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is EngineRpcError or IOException or JsonException)
        {
            throw new WorkbenchError("OpenCode 无法创建原生会话，请检查模型提供方配置", WorkbenchFailure.Unavailable);
        }
    }

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        var (code, output) = await Query(["session", "list", "--max-count", "500", "--format", "json"], ct);
        if (code != 0) throw new WorkbenchError("暂时无法读取 OpenCode 会话列表", WorkbenchFailure.Unavailable);
        // Some V1 releases return an empty document when no sessions exist.
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var json = JsonDocument.Parse(output);
        var sessions = new List<HistorySession>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (GetString(item, "id") is not { } id || !ValidSessionId(id) ||
                GetString(item, "directory") is not { } directory || !Path.IsPathFullyQualified(directory) ||
                !ProjectFolders.Same(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), cwd)) continue;
            string name = GetString(item, "title") ?? id;
            sessions.Add(new(id, name, cwd, UpdatedAt(item)));
        }
        return sessions;
    }

    internal override Task ChangeHistory(string id, string action, CancellationToken ct) =>
        throw new WorkbenchError("工作台支持本地归档和恢复 OpenCode 会话");

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string>();
        if (window.Session is { } id && ValidSessionId(id)) args.AddRange(["--session", id]);
        if (window.Model != null) args.AddRange(["--model", window.Model]);
        return args;
    }

    internal override async Task<TerminalLaunch> PrepareTerminal(AgentWindow window, EngineMcp mcp, string? newTitle,
        string? forkSource, bool reopening, CancellationToken ct)
    {
        var version = await SupportedVersion(ct);
        var args = NativeArguments(window, mcp, newTitle, forkSource);
        // V2 defaults to a shared service. Only attach this window's bridge to a private server.
        if (version.Major == 2) args.Add("--standalone");
        else args.AddRange(["--hostname", "127.0.0.1", "--port", "0"]);
        var config = ReadInlineConfig();
        var group = GetObject(config, "mcp");
        var servers = version.Major == 1 ? group : GetObject(group, "servers");
        var command = new[] { mcp.Command }.Concat(mcp.Arguments);
        servers["lx_collab"] = version.Major == 1
            ? JsonSerializer.SerializeToNode(new { type = "local", command, enabled = true })
            : JsonSerializer.SerializeToNode(new { type = "local", command, disabled = false, codemode = false, pool = false });
        // ConPTY sets the grant on this child environment. The native stdio child inherits it.
        return new(args, new Dictionary<string, string> { [InlineConfig] = config.ToJsonString() });
    }

    private async Task<Version> SupportedVersion(CancellationToken ct)
    {
        if (detectedVersion != null) return detectedVersion;
        var (code, output) = await Command(["--version"], ct);
        var match = VersionNumber.Match(output.TrimStart('\uFEFF').Split('\n')[0].Trim());
        if (code == 0 && match.Success && Version.TryParse(match.Groups[1].Value, out var version) &&
            (version.Major == 1 && version >= new Version(1, 1, 34) || version.Major == 2 && version >= new Version(2, 4, 2)))
            return detectedVersion = version;
        throw new WorkbenchError("请安装 OpenCode 1.1.34+ 或 2.4.2+（1.x / 2.x），以隔离窗口协作授权", WorkbenchFailure.Unavailable);
    }

    private Task<(int Code, string Output)> Command(string[] args, CancellationToken ct) =>
        EngineAuthentication.Command(Name, cwd, args, ct);

    private async Task<(int Code, string Output)> Query(string[] args, CancellationToken ct)
    {
        var version = await SupportedVersion(ct);
        // Catalog probes also avoid starting the user's shared background service.
        return await Command(version.Major == 2 ? ["--standalone", .. args] : args, ct);
    }

    private static IEnumerable<string> Models(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(id => id.Length <= 160 && ModelId.IsMatch(id)).Distinct(StringComparer.Ordinal);

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? UpdatedAt(JsonElement item)
    {
        if (!item.TryGetProperty("updated", out var value)) return GetString(item, "updatedAt");
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long time) && time is >= 0 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(time).ToString("O", CultureInfo.InvariantCulture) : null;
    }

    private static JsonObject ReadInlineConfig()
    {
        string? configured = Environment.GetEnvironmentVariable(InlineConfig);
        if (string.IsNullOrWhiteSpace(configured)) return new();
        try
        {
            if (JsonNode.Parse(configured, documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is JsonObject config) return config;
        }
        catch (JsonException) { }
        throw new WorkbenchError("OpenCode 的启动环境配置不是有效的 JSON 对象");
    }

    private static JsonObject GetObject(JsonObject parent, string key)
    {
        if (parent[key] == null) parent[key] = new JsonObject();
        return parent[key] as JsonObject ?? throw new WorkbenchError("OpenCode 的启动环境 MCP 配置格式无效");
    }
}
