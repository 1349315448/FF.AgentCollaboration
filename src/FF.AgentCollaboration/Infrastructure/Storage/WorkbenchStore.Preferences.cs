using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal string MentionShortcut() =>
        Read("SELECT value FROM settings WHERE key='mention_shortcut'", row => row.GetString(0)).SingleOrDefault()
        ?? MentionKey.Default;

    internal string SaveMentionShortcut(string? chord)
    {
        var value = MentionKey.Validate(chord);
        if (value == MentionKey.Default) Exec("DELETE FROM settings WHERE key='mention_shortcut'");
        else Exec("INSERT INTO settings VALUES('mention_shortcut',$p0) ON CONFLICT(key) DO UPDATE SET value=excluded.value", value);
        return value;
    }

    private string? ProjectSetting() => Read("SELECT value FROM settings WHERE key='project'", row => row.GetString(0)).SingleOrDefault();

    internal (string? Current, string[] Paths) ProjectList(IEnumerable<string>? associated = null)
    {
        string? selected = ProjectSetting();
        associated ??= Read("SELECT project FROM topics UNION ALL SELECT project FROM windows WHERE closed=0", row => row.GetString(0));
        var paths = Projects(selected == null ? associated : associated.Prepend(selected));
        return (paths.FirstOrDefault(path => string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)) ?? paths.FirstOrDefault(), paths);
    }

    private string[] SavedProjects()
    {
        string? saved = Read("SELECT value FROM settings WHERE key='projects'", row => row.GetString(0)).SingleOrDefault();
        return saved == null ? [] : JsonSerializer.Deserialize<string[]>(saved) ?? [];
    }

    internal string RegisteredProject(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested) || requested.StartsWith(@"\\") || requested.StartsWith("//"))
            throw new WorkbenchError("请选择工作台中的本地项目", WorkbenchFailure.Forbidden);
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        return ProjectList().Paths.FirstOrDefault(path => ProjectFolders.Same(path, canonical))
        ?? throw new WorkbenchError("项目尚未添加或已移除", WorkbenchFailure.Forbidden);
    }

    // [T13] 已打开的选项卡按用户顺序排在前面；话题和未关闭窗口补齐旧库中的项目。
    private string[] Projects(IEnumerable<string> candidates)
    {
        var removed = Read("SELECT value FROM settings WHERE key GLOB 'removed_project:*'", row => row.GetString(0));
        return SavedProjects().Concat(candidates).Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Except(removed, StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists).ToArray();
    }

    internal void SelectProject(string project) => Atomic(() => SaveProject(project));

    internal void RemoveProject(string project) => Atomic(() =>
    {
        // 保留话题、窗口与磁盘原件，同时阻止旧话题的项目路径被重新补回侧栏。
        Exec("INSERT OR REPLACE INTO settings(key,value) VALUES($p0,$p1)", RemovedProjectKey(project), project);
        var remaining = SavedProjects().Where(path => !string.Equals(path, project, StringComparison.OrdinalIgnoreCase));
        Exec("UPDATE settings SET value=$p0 WHERE key='projects'", JsonSerializer.Serialize(remaining));
        if (string.Equals(ProjectSetting(), project, StringComparison.OrdinalIgnoreCase))
            Exec("DELETE FROM settings WHERE key='project'");
    });

    private static string RemovedProjectKey(string path) => "removed_project:" + path.ToUpperInvariant();

    private void SaveProject(string project)
    {
        Exec("DELETE FROM settings WHERE key=$p0", RemovedProjectKey(project));
        string[] tabs = Projects([ProjectSetting() ?? project, project]);
        Exec("""
            INSERT INTO settings(key,value) VALUES('project',$p0),('projects',$p1)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """, project, JsonSerializer.Serialize(tabs));
    }

    // [T02] 每个引擎单独保存自定义清单。移除配置即恢复本版本核实过的原生指令。
    internal List<Shortcut> Shortcuts(string engine)
    {
        string key = "shortcuts." + EngineNames.Require(engine);
        var saved = Read("SELECT value FROM settings WHERE key=$p0", row => row.GetString(0), key).SingleOrDefault();
        if (saved != null) return JsonSerializer.Deserialize<List<Shortcut>>(saved)!;
        return ShortcutCommands.Defaults(engine);
    }

    internal List<Shortcut> ChangeShortcuts(string engine, ChangeShortcut change)
    {
        List<Shortcut> items = [];
        Atomic(() =>
        {
            items = Shortcuts(engine);
            string key = "shortcuts." + engine;
            if (change.Action == "reset")
            {
                Exec("DELETE FROM settings WHERE key=$p0", key);
                items = Shortcuts(engine);
                return;
            }
            ShortcutCommands.Apply(items, change);
            Exec("INSERT INTO settings VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                key, JsonSerializer.Serialize(items));
        });
        return items;
    }
}
