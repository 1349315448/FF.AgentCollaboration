using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal static class ClaudeHistory
{
    private static string Home => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static IEnumerable<string> Files(string project)
    {
        string parent = Path.Combine(Home, "projects");
        if (!Directory.Exists(parent)) yield break;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            string key = Regex.Replace(path, "[^a-zA-Z0-9]", "-");
            if (key.Length <= 200) candidates.Add(Path.Combine(parent, key));
            else candidates.UnionWith(Directory.EnumerateDirectories(parent, key[..200] + "*"));
        }
        Add(project);
        // Windows 的 TEMP 等变量可能只有路径前缀使用 8.3 短名。Claude 会用启动时的原始拼写生成目录名。
        var buffer = new StringBuilder(32768);
        if (GetShortPathName(project, buffer, buffer.Capacity) > 0)
        {
            var parts = project.Split(Path.DirectorySeparatorChar);
            var shortParts = buffer.ToString().Split(Path.DirectorySeparatorChar);
            if (shortParts.Length == parts.Length)
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = shortParts[i];
                    Add(string.Join(Path.DirectorySeparatorChar, parts));
                }
        }
        foreach (string dir in candidates.Where(Directory.Exists))
        {
            TopicArchive.CheckPath(dir);
            foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl")) yield return file;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string path, StringBuilder result, int size);

    private static HistorySession? Read(string file, string project, CancellationToken ct)
    {
        string id = Path.GetFileNameWithoutExtension(file);
        if (!Guid.TryParseExact(id, "D", out _)) return null;
        TopicArchive.CheckPath(file);
        bool matches = false;
        string? title = null, preview = null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var record = JsonDocument.Parse(line);
                var item = record.RootElement;
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? Field(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (Field("sessionId") is string session && session != id) continue;
                if (Field("cwd") is string cwd && Path.IsPathFullyQualified(cwd) && ProjectFolders.Same(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)), project)) matches = true;
                title = Field("customTitle") ?? title ?? Field("aiTitle") ?? Field("summary");
                if (preview == null && Field("type") == "user" && item.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    preview = content.GetString();
            }
            catch (JsonException) { /* 活动会话的末行可能尚未写完。 */ }
        }
        return matches ? new(id, title ?? (preview is { Length: > 0 } ? preview[..Math.Min(100, preview.Length)] : id), project,
            File.GetLastWriteTimeUtc(file).ToString("O")) : null;
    }

    internal static List<HistorySession> List(string project, CancellationToken ct)
    {
        var sessions = new List<HistorySession>();
        foreach (string file in Files(project))
        {
            try { if (Read(file, project, ct) is { } session) sessions.Add(session); }
            catch (FileNotFoundException) { }
        }
        return sessions;
    }

    internal static HashSet<string> ActiveSessions()
    {
        string directory = Path.Combine(Home, "sessions");
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return active;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int pid)) continue;
            try
            {
                using var info = JsonDocument.Parse(File.ReadAllText(file));
                if (!info.RootElement.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || session.GetString() is not { } id) continue;
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) active.Add(id);
            }
            catch (ArgumentException) { }
            catch (FileNotFoundException) { }
            catch (JsonException) { }
        }
        return active;
    }

    internal static void Delete(string project, string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new WorkbenchError("无效的 Claude 会话编号");
        foreach (string file in Files(project).Where(file => Path.GetFileNameWithoutExtension(file) == id))
        {
            if (Read(file, project, ct) == null || ActiveSessions().Contains(id)) break;
            File.Delete(file);
            return;
        }
        throw new WorkbenchError("会话已变化或正在使用，请刷新列表", WorkbenchFailure.Conflict);
    }
}
