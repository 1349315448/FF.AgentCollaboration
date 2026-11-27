using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Application;

internal static class CollaborationSkill
{
    internal const string Id = "collab_skill";
    private const string Marker = "<!-- Managed by FF.AgentCollaboration -->";
    internal static string Read() => ReadFile("ff-agent-collab");
    internal static string ReadTeam(bool leader) => ReadFile(leader ? "ff-team-leader" : "ff-team-member");

    internal static string ReadFile(string folder)
    {
        string path = Path.Combine(AppContext.BaseDirectory, folder, "SKILL.md");
        try { return File.ReadAllText(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new WorkbenchError($"缺少协作平台技能文件，请从发布包恢复此文件：{path}");
        }
    }

    // 老版本在用户目录安装技能。只移走带平台标记的入口，保留原文和个人同名技能。
    internal static HealthItem Inspect(bool retireLegacy = false)
    {
        string home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"),
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), Path.Combine(home, ".agents") };
        var issues = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(root, "skills", "ff-agent-collab", "SKILL.md");
            try
            {
                if (!Path.IsPathFullyQualified(path)) throw new IOException("需要绝对目录");
                TopicArchive.CheckPath(path);
                if (!File.Exists(path) || !File.ReadLines(path).Contains(Marker, StringComparer.Ordinal)) continue;
                if (!retireLegacy) throw new IOException("发现旧版全局副本，可备份并移出技能列表");
                File.Move(path, path + "." + Guid.NewGuid().ToString("N") + ".bak");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkbenchError or ArgumentException)
            { issues.Add(path + " · " + error.Message); }
        }
        return new(Id, "窗口协作规范 (ff-agent-collab)", issues.Count == 0,
            issues.Count == 0 ? "通过当前工作台窗口的 lx_collab 注入" : string.Join("；", issues),
            issues.Count == 0 ? null : "skill");
    }
}
