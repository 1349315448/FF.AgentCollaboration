using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

// 首次安装仅提供目录选择。完成数据库发布后才唤醒后台投递，持有目录锁到宿主退出。
internal sealed class WorkbenchStartup(WorkbenchLocation location, string initial, string? project,
    string? knowledge, string? handoffs) : IDisposable
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private FileStream? owner;
    internal Task Ready => ready.Task;
    internal string Root => location.Root;
    private string DefaultArchive => knowledge == null ? location.Defaults.Archive_root! : Path.Combine(knowledge, "development-tasks");
    private string DefaultHandoffs => handoffs ?? location.Defaults.Handoffs!;

    internal object Status()
    {
        using var store = Ready.IsCompletedSuccessfully ? new WorkbenchStore(Root) : null;
        return new
        {
            initialized = Ready.IsCompletedSuccessfully,
            needsNodeName = false,
            nodeName = store?.FederationNode().NodeName ?? FederationPolicy.DefaultName(),
            data_dir = Root,
            archive_root = DefaultArchive,
            handoffs = DefaultHandoffs,
            fixed_data_dir = location.IsFixed
        };
    }

    private static FileStream Acquire(string path)
    {
        StorageDirectories.CreatePrivateDirectory(path);
        return new FileStream(Path.Combine(path, "workbench.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal void OpenExisting()
    {
        string? source = location.MigrationSource(initial, knowledge);
        if (source == null && !File.Exists(Path.Combine(Root, "workbench.sqlite"))) return;
        owner = Acquire(Root);
        if (source != null) location.Import(source);
        using var store = new WorkbenchStore(Root);
        bool portable = ProjectFolders.Same(Root, WorkbenchLocation.DefaultDirectory);
        string archive = knowledge != null || portable ? DefaultArchive : Path.Combine(Root, "topics");
        string docs = handoffs != null || portable ? DefaultHandoffs : Path.Combine(Root, "handoffs");
        store.Initialize(project, docs, archive, standalone: knowledge == null,
            keepArchive: knowledge == null, keepHandoffs: handoffs == null && source == null);
        if (string.IsNullOrWhiteSpace(store.FederationNode().NodeName)) store.SetFederationNode(FederationPolicy.DefaultName(), null);
        MarkReady();
    }

    internal void Initialize(InitializeWorkbench paths)
    {
        lock (gate)
        {
            if (Ready.IsCompleted) throw new WorkbenchError("工作台已初始化", WorkbenchFailure.Conflict);
            string nodeName = string.IsNullOrWhiteSpace(paths.NodeName) ? FederationPolicy.DefaultName() : FederationPolicy.Name(paths.NodeName);
            string data = StorageDirectories.PrepareDirectory(paths.Data_dir, privateData:true);
            if (location.IsFixed && !ProjectFolders.Same(data, Root)) throw new WorkbenchError("此实例使用固定数据目录", WorkbenchFailure.Conflict);
            string archive = StorageDirectories.PrepareDirectory(paths.Archive_root);
            string docs = StorageDirectories.PrepareDirectory(paths.Handoffs);
            var lease = Acquire(data);
            string staged = Path.Combine(data, ".initialize-" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                if (File.Exists(Path.Combine(data, "workbench.sqlite")))
                    throw new WorkbenchError("此位置已有数据库，请选择空目录或用已有数据库启动", WorkbenchFailure.Conflict);
                using (var state = new WorkbenchStore(data, create: true, databaseFile: staged))
                {
                    state.Initialize(project, docs, archive, standalone: knowledge == null);
                    state.SetFederationNode(nodeName, null);
                    state.SetPaths(archive, docs);
                }
                location.Activate(data, staged);
                owner = lease;
                MarkReady();
            }
            finally
            {
                if (owner != lease) lease.Dispose();
                foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(staged + suffix);
            }
        }
    }

    private void MarkReady()
    {
        CollaborationSkill.Inspect(retireLegacy: true);
        new ManagedSkills(Root).Recover();
        ready.SetResult();
    }

    public void Dispose() => owner?.Dispose();
}
