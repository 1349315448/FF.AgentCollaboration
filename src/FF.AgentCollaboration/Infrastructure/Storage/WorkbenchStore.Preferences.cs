using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private string? ProjectSetting() => Read("SELECT value FROM settings WHERE key='project'", row => row.GetString(0)).SingleOrDefault();

    internal string Project()
    {
        string? path = ProjectSetting();
        return Directory.Exists(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : Directory.GetCurrentDirectory();
    }

    // [T13] 已打开的选项卡按用户顺序排在前面；话题和未关闭窗口补齐旧库中的项目。
    internal string[] Projects(IEnumerable<string> candidates)
    {
        string? saved = Read("SELECT value FROM settings WHERE key='projects'", row => row.GetString(0)).SingleOrDefault();
        var paths = saved == null ? [] : JsonSerializer.Deserialize<string[]>(saved) ?? [];
        return paths.Concat(candidates).Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(Directory.Exists).ToArray();
    }

    internal void SelectProject(string project) => Atomic(() => SaveProject(project));

    private void SaveProject(string project)
    {
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
