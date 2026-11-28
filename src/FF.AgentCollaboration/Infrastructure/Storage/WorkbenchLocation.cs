using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

// 数据目录必须在打开 SQLite 前解析，引导文件因此独立于工作台数据库。
internal sealed class WorkbenchLocation
{
    private sealed record Location(string data_dir, string? previous_data_dir, bool pending_migration = false);
    internal static string LocalData => Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolderOption.DoNotVerify);
    internal static string LegacyDirectory => Path.Combine(LocalData, "FF.AgentCollaboration", "workbench");
    private static string DataRoot => Path.Combine(Path.GetFullPath(
        Environment.GetEnvironmentVariable("FF_WORKBENCH_HOME") ?? AppContext.BaseDirectory), "workbench-data");
    internal static string DefaultDirectory => Path.Combine(DataRoot, "database");
    internal WorkbenchPaths Defaults => IsFixed
        ? new(Path.Combine(Root, "topics"), Path.Combine(Root, "handoffs"), Root)
        : new(Path.Combine(DataRoot, "topics"), Path.Combine(DataRoot, "handoffs"), DefaultDirectory);
    private readonly string? config;
    private readonly object gate = new();
    private Location? saved;
    internal string Root { get; private set; }
    internal string Target { get { lock (gate) return saved?.data_dir ?? Root; } }
    private bool Pending => !ProjectFolders.Same(Target, Root);

    internal WorkbenchLocation(string initial, bool fixedDirectory, string? knowledge)
    {
        config = fixedDirectory ? null : Path.GetFullPath(Environment.GetEnvironmentVariable("FF_LOCATION_CONFIG") ??
            Path.Combine(LocalData, "FF.AgentCollaboration", "workbench-location.json"));
        saved = Read();
        Root = saved?.data_dir ?? initial;
        if (!fixedDirectory && saved == null && ProjectFolders.Same(initial, DefaultDirectory) &&
            !File.Exists(Path.Combine(initial, "workbench.sqlite")) && File.Exists(Path.Combine(LegacyDirectory, "workbench.sqlite")))
            Root = LegacyDirectory;
        // P4 迁入知识库时保留过 FF 原库。双库并存取旧启动器实际使用的 cache，两份原库都保留。
        if (!fixedDirectory && saved == null && ProjectFolders.Same(Root, LegacyDirectory) && knowledge != null)
        {
            string cache = Path.Combine(knowledge, "agent-collaboration", "cache");
            if (File.Exists(Path.Combine(cache, "workbench.sqlite")))
            {
                if (File.Exists(Path.Combine(Root, "workbench.sqlite")))
                {
                    string candidate = Root + "-from-cache";
                    Root = candidate;
                    for (int suffix = 2; File.Exists(Root) ||
                         (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any()); suffix++)
                        Root = candidate + "-" + suffix;
                }
                using var guard = LockConfig();
                Publish(new(Root, cache, true));
            }
        }
    }

    private Location? Read()
    {
        if (config == null || !File.Exists(config)) return null;
        var value = JsonSerializer.Deserialize<Location>(File.ReadAllText(config));
        if (value == null || !Path.IsPathFullyQualified(value.data_dir) ||
            (value.previous_data_dir != null && !Path.IsPathFullyQualified(value.previous_data_dir)) ||
            (value.pending_migration && (value.previous_data_dir == null || ProjectFolders.Same(value.previous_data_dir, value.data_dir))))
            throw new WorkbenchError("数据库引导配置无效，请检查 " + config);
        return value;
    }

    // 多个独立数据目录可能同时打开配置，禁止旧实例覆盖另一个实例刚写的切换决定。
    private FileStream LockConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config!)!);
        var owner = new FileStream(config + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Read() != saved) throw new WorkbenchError("数据库引导位置已由其他实例修改，请重新启动后再配置", WorkbenchFailure.Conflict);
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    private void Publish(Location value)
    {
        string temp = config + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp)) { JsonSerializer.Serialize(file, value); file.Flush(true); }
            File.Move(temp, config!, true);
            saved = value;
        }
        finally { File.Delete(temp); }
    }

    internal bool IsFixed => config == null;
    internal string? MigrationSource(string initial, string? knowledge) =>
        WorkbenchMigration.FindSource(Root, knowledge, initial, saved?.previous_data_dir, saved?.pending_migration == true);

    internal void Import(string source)
    {
        using var guard = saved == null ? null : LockConfig();
        WorkbenchMigration.Import(source, Root);
        if (saved?.pending_migration == true) Publish(saved with { pending_migration = false });
    }

    internal void Activate(string target, string staged)
    {
        using var guard = config == null ? null : LockConfig();
        // 先发布选择，再原子发布完整数据库。中途退出时，下次仍从所选目录进入初始化。
        if (guard != null) Publish(new(target, null));
        File.Move(staged, Path.Combine(target, "workbench.sqlite"));
        Root = target;
    }

    internal object Settings(WorkbenchStore store)
    {
        lock (gate)
        {
            var (archive, handoffs) = store.Directories;
            return new
            {
                data_dir = Target,
                active_data_dir = Root,
                pending_restart = Pending,
                archive_root = archive,
                handoffs,
                defaults = Defaults
            };
        }
    }

    internal object Save(WorkbenchStore store, WorkbenchPaths paths)
    {
        lock (gate)
        {
            string target = paths.Data_dir ?? Target;
            if (!Path.IsPathFullyQualified(target)) throw new WorkbenchError("数据库位置必须使用绝对路径");
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            bool move = !ProjectFolders.Same(target, Root);
            if (move && config == null) throw new WorkbenchError("此实例使用固定数据目录", WorkbenchFailure.Conflict);
            TopicArchive.CheckPath(target);
            Directory.CreateDirectory(target);
            StorageDirectories.CheckDirectory(target);
            using var guard = move || Pending ? LockConfig() : null;
            using var targetLock = move ? WorkbenchMigration.LockOldWorkbench(target) : null;
            if (move)
                WorkbenchMigration.Backup(Root, target, online: true, staging =>
                {
                    using var copy = new WorkbenchStore(Root, databaseFile: staging);
                    copy.SetPaths(paths.Archive_root, paths.Handoffs);
                });
            store.SetPaths(paths.Archive_root, paths.Handoffs);
            if (guard != null) Publish(new(target, Root, move));
            return Settings(store);
        }
    }

    internal void RefreshPendingBackup()
    {
        lock (gate)
        {
            if (!Pending) return;
            using var guard = LockConfig();
            TopicArchive.CheckPath(Target);
            Directory.CreateDirectory(Target);
            using var targetLock = WorkbenchMigration.LockOldWorkbench(Target);
            WorkbenchMigration.Backup(Root, Target, online: true);
        }
    }
}
