using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal sealed class CodeBuddyEngine(string cwd) : ConfiguredTerminalEngine
{
    internal override ISubmissionReceiptSource? SubmissionReceipts(AgentWindow window)
    {
        if (!Guid.TryParseExact(window.Session, "D", out _)) return null;
        string session = window.Session!;
        string home = Environment.GetEnvironmentVariable("CODEBUDDY_CONFIG_DIR") ?? Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codebuddy");
        var root = Path.Combine(home, "projects");
        string? known = null;
        string Locate()
        {
            if (known != null) return known;
            string key = ProjectKey(window.Project);
            var expected = Path.Combine(root, key, session + ".jsonl");
            if (File.Exists(expected)) return known = expected;
            // Older CLIs can spell the project folder differently. Check only this exact session filename.
            if (Directory.Exists(root))
            {
                foreach (var folder in Directory.EnumerateDirectories(root).Take(512))
                {
                    if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)) continue;
                    var candidate = Path.Combine(folder, session + ".jsonl");
                    if (File.Exists(candidate)) return known = candidate;
                }
            }
            return expected;
        }
        return new JsonlSubmissionReceipts(Locate, (record, envelope) =>
            JsonlSubmissionReceipts.StringIs(record, "sessionId", session) && JsonlSubmissionReceipts.StringIs(record, "type", "message") &&
            JsonlSubmissionReceipts.IsUserEnvelope(record, envelope));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string path, StringBuilder result, int length);

    private static string ProjectKey(string project)
    {
        var path = new StringBuilder(32768);
        if (OperatingSystem.IsWindows() && GetLongPathName(project, path, path.Capacity) is > 0 and < 32768)
            project = path.ToString();
        var slug = Regex.Replace(project.Replace(":", "-").Replace('\\', '-').Replace('/', '-'), "-+", "-").Trim('-');
        var bytes = Encoding.UTF8.GetBytes(slug);
        if (bytes.Length <= 255) return slug;
        uint hash = 5381;
        foreach (byte value in bytes) hash = unchecked(hash * 33 ^ value);
        string suffix = "";
        do { suffix = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(hash % 36)] + suffix; hash /= 36; } while (hash != 0);
        int length = 180;
        while ((bytes[length] & 0xC0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length) + "-" + suffix;
    }
    protected override string ModeOption => "--permission-mode";
    internal override string[] LoginArguments => ["--no-session-persistence"];
    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        // 配置凭据也须能启动本机 CLI，否则建窗应显示依赖错误，而不是要求重新登录。
        WindowsCommand.Resolve(EngineExecutable.FindExecutable("codebuddy"), LoginArguments);
        var home = Environment.GetEnvironmentVariable("CODEBUDDY_CONFIG_DIR") ?? Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codebuddy");
        foreach (var path in new[] { Path.Combine(cwd, ".codebuddy", "settings.local.json"), Path.Combine(cwd, ".codebuddy", "settings.json"), Path.Combine(home, "settings.json") })
        {
            var settings = EngineAuthentication.Settings(path);
            if (EngineAuthentication.HasKey(settings, "CODEBUDDY_AUTH_TOKEN") ||
                (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEBUDDY_API_KEY_DISABLED")) && EngineAuthentication.HasKey(settings, "CODEBUDDY_API_KEY")) ||
                (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEBUDDY_API_KEY_HELPER_DISABLED")) && !string.IsNullOrWhiteSpace((string?)settings?["apiKeyHelper"])))
                return new("configured");
        }
        using var rpc = new RpcPipe(EngineExecutable.FindExecutable("codebuddy"), ["--acp"], cwd, null);
        using var stop = ct.Register(rpc.Abort);
        await rpc.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, ct);
        var user = await rpc.Request("_codebuddy.ai/getUserInfo", new { }, ct);
        return new(string.IsNullOrWhiteSpace((string?)user["userInfo"]?["userId"]) ? "signed-out" : "authenticated");
    }
    internal override string ApprovalKeys => "\u001b[Z";
    protected override string QuotaGuide => "请在 CodeBuddy 官网的「个人主页 → 套餐与用量」查看可用积分。终端内的 /cost 显示当前会话消耗。";
    internal override EngineCapabilities Capabilities => new("权限模式",
        [new("default", "默认审批"), new("plan", "计划"), new("acceptEdits", "接受编辑")],
        ["new", "resume", "fork", "archive", "restore"], NativeArchive: false, Efforts: false);

    internal override IReadOnlyList<ModelChoice> KnownModels { get; } =
        new[] { "default-model", "fast-model", "balanced-model", "primary-model", "deep-model" }
            .Select(id => new ModelChoice(id, id, id == "default-model", [])).ToArray();

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
