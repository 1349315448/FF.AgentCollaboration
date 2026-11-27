using System.Text.Json;
using FF.AgentCollaboration.Domain;
using Microsoft.Data.Sqlite;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private const string TeamProposalColumns =
        "p.id,p.topic_id,p.leader_window_id,coalesce(w.name,p.leader_name),p.summary,p.members,p.status,p.created_at";

    private void InitializeTeamProposals()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS team_proposals(
                id TEXT PRIMARY KEY, topic_id TEXT NOT NULL, leader_window_id TEXT NOT NULL,
                leader_name TEXT NOT NULL, summary TEXT NOT NULL, members TEXT NOT NULL, status TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS team_proposals_open_topic ON team_proposals(topic_id)
                WHERE status IN ('pending','creating');
            CREATE TABLE IF NOT EXISTS team_assignments(
                window_id TEXT PRIMARY KEY, task_summary TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS team_remote_members(
                topic_id TEXT NOT NULL, remote_id TEXT NOT NULL, role TEXT NOT NULL, task_summary TEXT NOT NULL,
                PRIMARY KEY(topic_id,remote_id)
            );
            CREATE TRIGGER IF NOT EXISTS invalidate_team_proposal AFTER UPDATE OF leader_window_id,status ON topics
            WHEN OLD.leader_window_id IS NOT NEW.leader_window_id OR NEW.status='finished'
            BEGIN
                UPDATE team_proposals SET status='stale' WHERE topic_id=NEW.id AND status='pending';
            END;
            UPDATE team_proposals SET status='interrupted' WHERE status='creating';
            """);
    }

    private static TeamProposal ReadTeamProposal(SqliteDataReader row) => new(
        row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4),
        JsonSerializer.Deserialize<TeamProposalMember[]>(row.GetString(5))!, row.GetString(6), row.GetString(7));

    internal List<TeamProposal> PendingTeamProposals() => Read($"""
        SELECT {TeamProposalColumns} FROM team_proposals p
        JOIN topics t ON t.id=p.topic_id AND t.status='active' AND t.leader_window_id=p.leader_window_id
        JOIN windows w ON w.id=p.leader_window_id AND w.closed=0
        WHERE p.status='pending' ORDER BY p.rowid
        """, ReadTeamProposal);

    internal TeamProposal TeamProposal(string topicId, string id) => Read($"""
        SELECT {TeamProposalColumns} FROM team_proposals p LEFT JOIN windows w ON w.id=p.leader_window_id
        WHERE p.topic_id=$p0 AND p.id=$p1
        """, ReadTeamProposal, topicId, id).SingleOrDefault()
        ?? throw new WorkbenchError("找不到该话题的组建提案", WorkbenchFailure.NotFound);

    internal TeamProposal ProposeTeam(string grant, TeamProposalInput input)
    {
        TeamProposal? proposal = null;
        Atomic(() =>
        {
            var caller = Caller(grant);
            var topic = Topic(caller.TopicId);
            if (topic.LeaderWindowId != caller.Id || DedicatedReader(caller.Id))
                throw new WorkbenchError("仅本话题组长拥有组建 Agent 的权限，请由用户在界面设置组长", WorkbenchFailure.Forbidden);
            if (topic.Status != "active") throw new WorkbenchError("请先重新打开这个话题", WorkbenchFailure.Conflict);
            var summary = WorkbenchInput.Text(input.Summary, "团队组建说明", 2000);
            if (input.Members is not { Length: >= 1 and <= 8 })
                throw new WorkbenchError("每次可提议组建 1 至 8 位 Agent");
            var names = Read("SELECT name FROM windows WHERE topic_id=$p0 AND closed=0", row => row.GetString(0), topic.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var members = new List<TeamProposalMember>();
            var invited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in input.Members)
            {
                if (member == null) throw new WorkbenchError("请填写有效的 Agent 建议");
                string name = WorkbenchInput.AgentName(member.Name);
                string role = WorkbenchInput.AgentRole(WorkbenchInput.Text(member.Role, "身份提示词", 200))
                    ?? throw new WorkbenchError("请填写身份提示词");
                string task = WorkbenchInput.Text(member.TaskSummary, "初始分工", 500);
                if (member.RemoteId != null)
                {
                    var remote = InvitableTeamRemote(topic.Id, caller.Id, member.RemoteId);
                    if (!invited.Add(remote.Id)) throw new WorkbenchError("提案重复邀请了同一外部成员", WorkbenchFailure.Conflict);
                    members.Add(new(remote.AgentName, role, task, RemoteId: remote.Id));
                    continue;
                }
                if (!names.Add(name)) throw new WorkbenchError("这个话题或提案已有同名 Agent：" + name, WorkbenchFailure.Conflict);
                string? engine = member.SuggestedEngine == null ? null : EngineNames.Require(member.SuggestedEngine);
                members.Add(new(name, role, task, engine, TeamSuggestion(member.SuggestedModel, "推荐模型"),
                    TeamSuggestion(member.SuggestedEffort, "推荐推理档位")));
            }
            if (Read("SELECT 1 FROM team_proposals WHERE topic_id=$p0 AND status='creating'", row => row.GetInt32(0), topic.Id).Count != 0)
                throw new WorkbenchError("这个话题正在创建团队，请等待创建结果", WorkbenchFailure.Conflict);
            string id = Guid.NewGuid().ToString("N");
            Exec("UPDATE team_proposals SET status='superseded' WHERE topic_id=$p0 AND status='pending'", topic.Id);
            Exec("""
                INSERT INTO team_proposals(id,topic_id,leader_window_id,leader_name,summary,members,status)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,'pending')
                """, id, topic.Id, caller.Id, caller.Name, summary, JsonSerializer.Serialize(members));
            proposal = TeamProposal(topic.Id, id);
        });
        return proposal!;
    }

    private static string? TeamSuggestion(string? value, string label)
    {
        if (value == null) return null;
        string option = WorkbenchInput.Text(value, label, 200);
        if (option.Any(char.IsControl)) throw new WorkbenchError(label + "不能包含控制字符");
        return option;
    }

    private TeamProposal RequirePendingTeamProposal(string topicId, string id)
    {
        var proposal = TeamProposal(topicId, id);
        var topic = Topic(topicId);
        if (proposal.Status != "pending" || topic.Status != "active" || topic.LeaderWindowId != proposal.LeaderWindowId)
            throw new WorkbenchError("此提案已失效或已处理，请重新查看组建提案", WorkbenchFailure.Conflict);
        return proposal;
    }

    internal TeamProposal ClaimTeamProposal(string topicId, string id, Action<TeamProposal> register)
    {
        TeamProposal? proposal = null;
        Atomic(() =>
        {
            proposal = RequirePendingTeamProposal(topicId, id);
            Exec("UPDATE team_proposals SET status='creating' WHERE id=$p0", id);
            register(proposal);
        });
        return proposal!;
    }

    internal void FinishTeamProposal(string topicId, string id, string status, Action<TeamProposal>? notice = null) => Atomic(() =>
    {
        if (status is not ("created" or "partial" or "failed" or "interrupted"))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (Exec("UPDATE team_proposals SET status=$p2 WHERE topic_id=$p0 AND id=$p1 AND status='creating'", topicId, id, status) != 0)
            notice?.Invoke(TeamProposal(topicId, id));
    });

    internal void RejectTeamProposal(string topicId, string id, Action<TeamProposal>? notice = null) => Atomic(() =>
    {
        var proposal = RequirePendingTeamProposal(topicId, id);
        Exec("UPDATE team_proposals SET status='rejected' WHERE id=$p0", id);
        notice?.Invoke(proposal);
    });
}
