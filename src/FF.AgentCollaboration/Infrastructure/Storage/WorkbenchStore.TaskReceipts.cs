using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private sealed record ReceivedTask(string Id, string Source, string? State, bool ExpectsReply);

    private ReceivedTask ReceivedTaskFor(string window, string reference)
    {
        if (reference.Length is not (8 or 32) || !reference.All(Uri.IsHexDigit))
            throw new WorkbenchError("请提供消息的 MSG_ID 或完整编号");
        var matches = Read("""
            SELECT id,source,work_state,return_hint IS NOT NULL FROM inbox
            WHERE target=$p0 AND id GLOB $p1 AND acknowledged=0 AND work_state IS NOT NULL
            AND state IN ('writing','delivered','delivery_uncertain')
            AND NOT EXISTS(SELECT 1 FROM fed_incoming f JOIN fed_grant g ON g.id=f.grant_id
                           WHERE f.local=inbox.id AND (g.status<>'approved' OR g.paused=1)) LIMIT 2
            """, r => new ReceivedTask(r.GetString(0), r.GetString(1), Optional(r,2), r.GetBoolean(3)), window, reference.ToLowerInvariant() + (reference.Length == 8 ? "*" : ""));
        if (matches.Count > 1) throw new WorkbenchError("短编号重复，请使用完整消息编号", WorkbenchFailure.Conflict);
        var task = matches.SingleOrDefault() ?? throw new WorkbenchError("找不到本窗口可回执的任务",WorkbenchFailure.Forbidden);
        if (DedicatedReader(window) && CurrentPeerRequest(window) != task.Id)
            throw new WorkbenchError("仅可回执当前外部审阅任务",WorkbenchFailure.Forbidden);
        return task;
    }

    internal string ReportTask(string grant, string reference, string status)
    {
        if (status is not ("working" or "need_info" or "completed")) throw new WorkbenchError("协作状态应为 working、need_info 或 completed");
        Atomic(() =>
        {
            var task = ReceivedTaskFor(Caller(grant).Id,reference);
            if (task.ExpectsReply && status=="completed") throw new WorkbenchError("这条任务要求回信，请关联原消息发送最终结果");
            SetTaskState(task.Id,status);
        });
        return status;
    }

    // 与业务回复、接收端状态和出站 SSE 事件共用调用方事务。
    private void SetTaskState(string id, string status)
    {
        var previous=Read("SELECT work_state FROM inbox WHERE id=$p0", r=>Optional(r,0),id).SingleOrDefault();
        if (previous == null || previous == status) return;
        if (previous is "completed" or "replied") throw new WorkbenchError("该任务已结案",WorkbenchFailure.Conflict);
        Exec("UPDATE inbox SET work_state=$p1 WHERE id=$p0",id,status);
        Exec("""
            INSERT INTO fed_event(grant_id,id,kind,request_id,body)
            SELECT grant_id,lower(hex(randomblob(16))),'receipt',request,'work:'||$p1 FROM fed_incoming WHERE local=$p0
            """,id,status);
        if (status == "working")
            Exec("""
                UPDATE fed_incoming SET reader_session=(SELECT w.engine||':'||w.session FROM windows w JOIN inbox i ON i.target=w.id WHERE i.id=$p0)
                WHERE local=$p0 AND active>0
                """,id);
        if (status is "completed" or "replied")
            Exec("UPDATE fed_incoming SET active=0 WHERE local=$p0",id);
    }

    private void RecordTaskReply(ReceivedTask task, bool final)
    {
        if (task.State is "completed" or "replied") throw new WorkbenchError("该任务已结案",WorkbenchFailure.Conflict);
        if (final) SetTaskState(task.Id,task.ExpectsReply ? "replied" : "completed");
        else if (task.State != "need_info") SetTaskState(task.Id,"working");
    }
}
