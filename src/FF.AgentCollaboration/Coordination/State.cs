using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Coordination;

internal sealed record Dispatch(string Id, string TaskId, string Phase, string Target, string State, long Attempts);
internal sealed record Job(string Id, string Origin, string Worker, string Challenge, string? Document, string? Result, string? Verification);
internal sealed record Window(string Name, string? Session, string Marker, string? Model);
internal sealed record TurnEvidence(string EventId, string? NativeTurn, string? Started, long? HistoryMatches);

// SQLite 是交付文档和投递状态的权威源；Markdown 仅用于查看和交接。
internal sealed class State : LocalState
{
    internal string Root { get; }

    internal State(string root, bool create = false) : base(Path.Combine(root, "state.sqlite"), create)
    {
        Root = Path.GetFullPath(root);
        if (create) Initialize();
    }

    private static string Nonce() => Guid.NewGuid().ToString("N");

    private void Initialize()
    {
        Exec("PRAGMA journal_mode=WAL");
        Atomic(() =>
        {
            Exec("""
                CREATE TABLE windows(name TEXT PRIMARY KEY, session TEXT UNIQUE, marker TEXT NOT NULL, model TEXT, grant_hash TEXT);
                CREATE TABLE tasks(id TEXT PRIMARY KEY, origin TEXT NOT NULL, worker TEXT NOT NULL, challenge TEXT NOT NULL,
                                   document TEXT, result TEXT, verification TEXT);
                CREATE TABLE events(id TEXT PRIMARY KEY, task TEXT NOT NULL, phase TEXT NOT NULL, target TEXT NOT NULL,
                                    state TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, turn TEXT, started TEXT,
                                    history_matches INTEGER, UNIQUE(task,phase));
                """);
            foreach (var name in new[] { "codex", "copilot" })
                Exec("INSERT INTO windows(name,marker) VALUES($p0,$p1)", name, "PRIVATE-" + Nonce()[..12]);
            foreach (var (id, origin, worker) in new[] { ("A-B", "codex", "copilot"), ("B-A", "copilot", "codex") })
            {
                Exec("INSERT INTO tasks(id,origin,worker,challenge) VALUES($p0,$p1,$p2,$p3)", id, origin, worker, "DOC-" + Nonce()[..12]);
                Enqueue(id, "open", origin);
            }
        });
    }

    private void Enqueue(string task, string phase, string target) =>
        Exec("INSERT INTO events(id,task,phase,target,state) VALUES($p0,$p1,$p2,$p3,'pending')", Nonce(), task, phase, target);


    internal Window WindowFor(string name) => Read("SELECT name,session,marker,model FROM windows WHERE name=$p0",
        r => new Window(r.GetString(0), Optional(r, 1), r.GetString(2), Optional(r, 3)), name).Single();
    internal Job TaskFor(string id) => Read("SELECT id,origin,worker,challenge,document,result,verification FROM tasks WHERE id=$p0",
        r => new Job(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Optional(r, 4), Optional(r, 5), Optional(r, 6)), id).Single();
    internal List<Dispatch> Events() => Read("""
        SELECT id,task,phase,target,state,attempts FROM events
        ORDER BY CASE phase WHEN 'open' THEN 0 WHEN 'work' THEN 1 ELSE 2 END, task DESC
        """, r => new Dispatch(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
    internal void Bind(string name, string id, string? model)
    {
        if (string.IsNullOrWhiteSpace(id) || Exec("UPDATE windows SET session=$p1, model=$p2 WHERE name=$p0 AND (session IS NULL OR session=$p1)", name, id, model) != 1)
            throw new InvalidOperationException("引擎未恢复到绑定的原会话");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal string IssueGrant(string name)
    {
        var grant = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Exec("UPDATE windows SET grant_hash=$p1 WHERE name=$p0", name, Hash(grant));
        return grant;
    }
    internal string Caller(string grant) => Read("SELECT name FROM windows WHERE grant_hash=$p0",
        r => r.GetString(0), Hash(grant)).Single();
    internal void Finish(string id, string status) => Exec("UPDATE events SET state=$p1 WHERE id=$p0 AND state IN ('in_flight','delivery_uncertain')", id, status);
    internal void HistoryCount(string id, int count) => Exec("UPDATE events SET history_matches=$p1 WHERE id=$p0", id, count);
    internal List<TurnEvidence> ProtocolEvidence() => Read("SELECT id,turn,started,history_matches FROM events ORDER BY rowid",
        r => new TurnEvidence(r.GetString(0), Optional(r, 1), Optional(r, 2), r.IsDBNull(3) ? null : r.GetInt64(3)));

    internal static string? Effect(Job job, string phase) => phase switch
    {
        "open" => job.Document,
        "work" => job.Result,
        "callback" => job.Verification,
        _ => throw new ArgumentException("phase")
    };

    // [02–04] 身份来自专属进程凭据；幂等回执与下一跳在同一次事务中提交。
    internal string Call(string grant, string tool, string taskId, string? text)
    {
        var phase = tool switch
        {
            "handoff_task" => "open",
            "read_handoff" or "report_task_done" => "work",
            "verify_result" => "callback",
            _ => throw new ArgumentException("Unknown tool")
        };
        string response = "Recorded.";
        Atomic(() =>
        {
            var caller = Caller(grant);
            var job = TaskFor(taskId);
            var dispatch = Events().Single(x => x.TaskId == taskId && x.Phase == phase);
            if (dispatch.Target != caller || dispatch.Attempts != 1)
                throw new InvalidOperationException("该工具调用不属于当前窗口或尚未派发");
            if (tool == "read_handoff") { response = job.Document!; return; }
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
            var previous = Effect(job, phase);
            if (previous != null)
            {
                if (previous != text) throw new InvalidOperationException("该阶段已经保存了不同的交付物");
                return;
            }
            if (phase == "open")
            {
                Exec("UPDATE tasks SET document=$p1 WHERE id=$p0", taskId, text);
                Enqueue(taskId, "work", job.Worker);
            }
            else if (phase == "work")
            {
                Exec("UPDATE tasks SET result=$p1 WHERE id=$p0", taskId, text);
                Enqueue(taskId, "callback", job.Origin);
            }
            else Exec("UPDATE tasks SET verification=$p1 WHERE id=$p0", taskId, text);
        });
        return response;
    }

    internal void Export()
    {
        var directory = Path.Combine(Root, "handoffs");
        Directory.CreateDirectory(directory);
        foreach (string id in new[] { "A-B", "B-A" })
        {
            var j = TaskFor(id);
            foreach (var (name, body) in new[] { ("handoff", j.Document), ("result", j.Result), ("verified", j.Verification) })
                if (body != null) File.WriteAllText(Path.Combine(directory, $"{id}-{name}.md"), body);
        }
    }
}
