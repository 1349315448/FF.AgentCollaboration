using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal RemoteCapsule InvitableTeamRemote(string topic, string leader, string remoteId)
    {
        if (DedicatedReader(leader)) throw new WorkbenchError("专用审阅终端无权邀请成员", WorkbenchFailure.Forbidden);
        var id = WorkbenchInput.Id(remoteId);
        var remote = RemoteRecipients(topic, leader).FirstOrDefault(peer => peer.Id == id)
            ?? throw new WorkbenchError("该外部 Agent 尚未授权本话题组长协作，或授权已失效", WorkbenchFailure.Forbidden);
        if (remote.TeamMember) throw new WorkbenchError("这个外部 Agent 已经是话题成员", WorkbenchFailure.Conflict);
        return remote;
    }

    internal RemoteCapsule AddExternalTeamMember(string topic, string leader, string id, string role, string task)
    {
        // 与提案认领共用写事务，按此刻的组长和胶囊授权复核。
        var peer = InvitableTeamRemote(topic, leader, id);
        Exec("INSERT INTO team_remote_members(topic_id,remote_id,role,task_summary) VALUES($p0,$p1,$p2,$p3)", topic,id,role,task);
        return peer;
    }

    internal List<ExternalTeamMember> ExternalTeamMembers(string topic, string? caller) => Read("""
        SELECT r.id,r.agent,r.name,m.role,m.task_summary,r.connected AND r.online,
               r.status='approved' AND NOT EXISTS(SELECT 1 FROM fed_remote_denial d WHERE d.remote=r.id AND d.agent=$p1)
        FROM team_remote_members m JOIN fed_remote r ON r.id=m.remote_id
        JOIN fed_remote_topic t ON t.remote=m.remote_id AND t.topic=m.topic_id
        WHERE m.topic_id=$p0 ORDER BY m.rowid
        """, r => new ExternalTeamMember(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),
            r.GetBoolean(5),r.GetBoolean(6)),topic,caller);

    internal void RecordTeamTask(string windowId, string task) =>
        Exec("INSERT INTO team_assignments(window_id,task_summary) VALUES($p0,$p1)", windowId, task);

    internal Dictionary<string, string> TeamTasks(string topicId) => Read("""
        SELECT a.window_id,a.task_summary FROM team_assignments a
        JOIN windows w ON w.id=a.window_id WHERE w.topic_id=$p0 AND w.closed=0
        """, row => (Id: row.GetString(0), Task: row.GetString(1)), topicId)
        .ToDictionary(task => task.Id, task => task.Task);

    // 在提案状态的调用方事务中入箱。发送者由平台固定，通知不算新的协作任务。
    internal void QueueTeamNotice(TeamProposal proposal, string notice) =>
        Exec("""
        INSERT INTO inbox(id,source,target,sender,text,files,document,state,topic_id,created_at)
        SELECT $p0,'workbench-system',w.id,'工作台',$p1,'[]',0,'pending',w.topic_id,CURRENT_TIMESTAMP
        FROM topics t JOIN windows w ON w.id=coalesce(t.leader_window_id,$p2) AND w.topic_id=t.id AND w.closed=0
        WHERE t.id=$p3
        """, Guid.NewGuid().ToString("N"), notice, proposal.LeaderWindowId, proposal.TopicId);
}
