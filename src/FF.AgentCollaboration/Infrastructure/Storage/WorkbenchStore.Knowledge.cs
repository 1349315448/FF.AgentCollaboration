namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private static string KnowledgeModeKey(string project) =>
        "knowledge_mode:" + (OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project);

    internal string KnowledgeMode(string project) =>
        Read("SELECT value FROM settings WHERE key=$p0", row => row.GetString(0), KnowledgeModeKey(project)).SingleOrDefault()
        ?? "disabled";

    internal List<(string Id, string Name, string TopicId, string Project)> KnowledgeMembers() =>
        Read("SELECT id,name,topic_id,project FROM windows", row => (row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)));

    internal void ConfigureKnowledgeMode(string project, string mode, bool register, Action prepare) => Atomic(() =>
    {
        if (register) SaveProject(project);
        else RegisteredProject(project);
        prepare();
        Exec("""
            INSERT INTO settings(key,value) VALUES($p0,$p1)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """, KnowledgeModeKey(project), mode);
    });

    // 与换组长/关闭窗格使用同一个状态库事务，提交文件前的授权不能被并发撤销跳过。
    internal T WithKnowledgeWriteAuthorization<T>(Func<T> write)
    {
        T result = default!;
        Atomic(() => result = write());
        return result;
    }
}
