using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed record RestartTask(string Id, string Window, string Name, string Body, bool Assigned);

internal sealed partial class WorkbenchStore
{
    private void InitializeRestartTasks() => Exec("""
        CREATE TABLE IF NOT EXISTS restart_tasks(
            id TEXT PRIMARY KEY, window_id TEXT NOT NULL, engine TEXT NOT NULL, session TEXT NOT NULL,
            body TEXT NOT NULL, operation TEXT, released INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS restart_one_draft ON restart_tasks(window_id) WHERE operation IS NULL AND released=0;
        """);

    internal string SaveRestartTask(string grant, string body)
    {
        string id = Guid.NewGuid().ToString("N");
        Atomic(() =>
        {
            var caller = Caller(grant);
            if (DedicatedReader(caller.Id))
                throw new WorkbenchError("专用审阅会话不可登记启动恢复任务", WorkbenchFailure.Forbidden);
            if (caller.Session == null)
                throw new WorkbenchError("请先绑定当前会话，再登记重启后继续的任务", WorkbenchFailure.Conflict);
            Exec("""
                INSERT INTO restart_tasks(id,window_id,engine,session,body) VALUES($p0,$p1,$p2,$p3,$p4)
                ON CONFLICT(window_id) WHERE operation IS NULL AND released=0
                DO UPDATE SET engine=excluded.engine,session=excluded.session,body=excluded.body
                """, id,caller.Id,caller.Engine,caller.Session,body);
            id=Read("SELECT id FROM restart_tasks WHERE window_id=$p0 AND operation IS NULL AND released=0",
                r=>r.GetString(0),caller.Id).Single();
        });
        return id;
    }

    internal List<RestartTask> PendingRestartTasks() => Read("""
        SELECT r.id,r.window_id,w.name,r.body,r.operation IS NOT NULL FROM restart_tasks r JOIN windows w ON w.id=r.window_id
        WHERE r.released=0 AND w.closed=0 ORDER BY r.rowid
        """,r=>new RestartTask(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetBoolean(4)));

    internal void BindRestartTasks(string operation, string[] selected) => Atomic(() =>
    {
        foreach (string id in selected.Distinct())
            Exec("UPDATE restart_tasks SET operation=$p1 WHERE id=$p0 AND released=0",WorkbenchInput.Id(id),operation);
    });

    internal string[] StartupWindows(string? operation) => Read("""
        SELECT w.id FROM windows w JOIN topics t ON t.id=w.topic_id
        WHERE w.closed=0 AND t.status='active' AND (w.auto_start=1 OR EXISTS(
            SELECT 1 FROM restart_tasks r WHERE r.operation=$p0 AND r.released=0 AND r.window_id=w.id
            AND r.engine=w.engine AND r.session=w.session))
        ORDER BY w.rowid
        """,r=>r.GetString(0),operation).ToArray();

    // 只在启动器确认此次启动后，由已成功打开原会话的窗格调用。
    internal void ReleaseRestartTasks(string operation, string window) => Atomic(() =>
    {
        var agent=Read($"SELECT {AgentColumns} FROM windows WHERE id=$p0 AND closed=0 AND online=1",Agent,window).SingleOrDefault();
        if (agent==null || DedicatedReader(window) || Topic(agent.TopicId).Status!="active") return;
        var tasks = Read("""
            SELECT id,body FROM restart_tasks WHERE operation=$p0 AND window_id=$p1 AND engine=$p2 AND session=$p3 AND released=0
            """,r=>(Id:r.GetString(0),Body:r.GetString(1)),operation,window,agent.Engine,agent.Session);
        foreach (var item in tasks)
        {
            const string sender="重启恢复";
            SaveChat(item.Id,new(InboxItem.RestartSource,sender,agent.TopicId,agent.Project),item.Body,[]);
            LinkChat(item.Id,item.Id,agent.TopicId,window,agent.Name);
            Exec("""
                INSERT INTO inbox(id,source,target,sender,text,files,document,state,topic_id,created_at,work_state)
                VALUES($p0,$p1,$p2,$p3,$p4,'[]',0,'pending',$p5,CURRENT_TIMESTAMP,'queued')
                """,item.Id,InboxItem.RestartSource,window,sender,item.Body,agent.TopicId);
            Exec("UPDATE restart_tasks SET released=1 WHERE id=$p0",item.Id);
        }
    });
}
