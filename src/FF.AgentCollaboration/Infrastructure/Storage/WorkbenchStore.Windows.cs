using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private const string AgentColumns = "id,name,engine,project,model,session,effort,mode,auto_receive,online,topic_id,session_title,approval,receive_mode,role,new_session_pending";

    private static AgentWindow Agent(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2),
        row.GetString(3), Optional(row, 4), Optional(row, 5), Optional(row, 6), Optional(row, 7), row.GetBoolean(8), row.GetBoolean(9), row.GetString(10))
        { SessionTitle = Optional(row, 11), Approval = Optional(row, 12), ReceiveMode = row.GetString(13), Role = Optional(row,14), NewSessionPending = row.GetBoolean(15) };

    // Copilot 旧表沿用原 ID。Claude 用独立前缀以免两个引擎碰巧使用相同 UUID。
    internal HashSet<string> ArchivedSessions(string engine)
    {
        string prefix = engine == "copilot" ? "" : engine + ":";
        return Read("SELECT session FROM copilot_session_archives", row => row.GetString(0))
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Select(id => id[prefix.Length..]).ToHashSet(StringComparer.Ordinal);
    }
    internal void SetArchive(string engine, string id, bool archived) =>
        Exec(archived ? "INSERT OR IGNORE INTO copilot_session_archives VALUES($p0)" :
            "DELETE FROM copilot_session_archives WHERE session=$p0", engine == "copilot" ? id : engine + ":" + id);

    internal List<AgentWindow> Windows() => Read($"SELECT {AgentColumns} FROM windows WHERE closed=0 ORDER BY rowid", Agent);
    internal AgentWindow Window(string id) => Read($"SELECT {AgentColumns} FROM windows WHERE id=$p0 AND closed=0", Agent, id)
        .SingleOrDefault() ?? throw new WorkbenchError("找不到这个 Agent 窗格", WorkbenchFailure.NotFound);

    internal static string NewGrant() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal void AddWindow(AgentWindow agent)
    {
        if (Exec("""
            INSERT INTO windows(id,name,engine,project,model,session,effort,mode,topic_id,auto_receive,role)
            SELECT $p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,id,$p9,$p10 FROM topics WHERE id=$p8 AND status='active'
            """, agent.Id, agent.Name, agent.Engine, agent.Project, agent.Model, agent.Session, agent.Effort, agent.Mode, agent.TopicId, agent.AutoReceive, agent.Role) == 0)
            throw new WorkbenchError("请先重新打开这个话题", WorkbenchFailure.Conflict);
    }
    internal void RenameWindow(string id, string name)
    {
        try
        {
            if (Exec("UPDATE windows SET name=$p1 WHERE id=$p0 AND closed=0", id, name) == 0)
                throw new WorkbenchError("找不到这个 Agent 窗格", WorkbenchFailure.NotFound);
        }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
        {
            throw new WorkbenchError("这个话题已有同名 Agent", WorkbenchFailure.Conflict);
        }
    }
    internal void RemoveUnstartedWindow(string id) => Exec("DELETE FROM windows WHERE id=$p0 AND online=0", id);
    internal void SaveConfiguration(AgentWindow w) =>
        Exec("""
            UPDATE windows SET name=$p1,role=$p2,engine=$p3,model=$p4,effort=$p5,mode=$p6,approval=$p7,
                session=$p8,session_title=$p9,new_session_pending=$p10 WHERE id=$p0 AND closed=0
            """, w.Id,w.Name,w.Role,w.Engine,w.Model,w.Effort,w.Mode,w.Approval,w.Session,w.SessionTitle,w.NewSessionPending);

    internal void StartConfigured(AgentWindow w, string grant) => Atomic(() => { SaveConfiguration(w); Start(w, grant); });

    internal void Start(AgentWindow window, string grant) =>
        Exec("UPDATE windows SET grant_hash=$p1,online=1,new_session_pending=0,session=$p2,session_title=$p3,approval=$p4 WHERE id=$p0 AND closed=0",
            window.Id, Hash(grant), window.Session, window.SessionTitle, window.Approval);
    internal void RestoreSession(AgentWindow previous) =>
        Exec("UPDATE windows SET grant_hash=NULL,online=0,session=$p1,session_title=$p2,approval=$p3,new_session_pending=$p4 WHERE id=$p0 AND closed=0",
            previous.Id, previous.Session, previous.SessionTitle, previous.Approval, previous.NewSessionPending);
    internal void Offline(string id) => Exec("UPDATE windows SET grant_hash=NULL,online=0 WHERE id=$p0", id);
    internal void StopWindow(string id) => Atomic(() => { ClearWake(id); Offline(id); });
    internal void Close(string id) => Exec("UPDATE windows SET grant_hash=NULL,online=0,closed=1 WHERE id=$p0", id);
    internal void AutoReceive(string id, bool? enabled, string? mode) =>
        Exec("UPDATE windows SET auto_receive=COALESCE($p1,auto_receive),receive_mode=COALESCE($p2,receive_mode) WHERE id=$p0 AND closed=0", id, enabled, mode);
    internal AgentWindow Caller(string grant) =>
        Read($"SELECT {AgentColumns} FROM windows WHERE grant_hash=$p0 AND online=1 AND closed=0", Agent, Hash(grant)).SingleOrDefault()
        ?? throw new WorkbenchError("Agent 连接已失效", WorkbenchFailure.Forbidden);
}
