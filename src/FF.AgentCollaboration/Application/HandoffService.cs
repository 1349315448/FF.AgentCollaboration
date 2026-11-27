using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal static class HandoffService
{
    private const string ReturnHint = "完成后请用 send_to_agent 回送：结论、改动文件清单、验证结果；详情写入文档并用 document 传路径。";
    internal const string AllMembers = "@全部";

    internal static object Broadcast(WorkbenchStore state, string grant, string? text, string? document, string[] files,
        string? hint, string[] externalTargets)
    {
        var source = state.Caller(grant);
        state.RequireBroadcastLeader(source);
        var external = externalTargets.Select(WorkbenchInput.Id).Distinct().ToArray();
        var authorized = state.ExternalTeamMembers(source.TopicId, source.Id).Where(member => member.Authorized)
            .Select(member => member.Id).ToHashSet();
        if (external.Any(id => !authorized.Contains(id)))
            throw new WorkbenchError("请选择本话题内已授权的外部组员", WorkbenchFailure.Forbidden);
        if (external.Length != 0 && files.Length != 0)
            throw new WorkbenchError("跨端收件支持一份 Markdown，请通过 document 传入");
        string message = Message(text, document, files);
        hint = NormalizeHint(hint);
        var local = state.BroadcastLocal(grant, message, files, hint, document);
        if (local.Length == 0 && external.Length == 0) throw new WorkbenchError("本话题没有其他组员");
        var deliveries = new List<object>(local.Select(item => new { item.Id, item.Target, status = "queued" }));
        foreach (string target in external)
        {
            try
            {
                var item = state.SendFederated(grant, target, message, [], document, hint, false, null, null, topicBroadcast:true)
                    ?? throw new WorkbenchError("该外部组员的授权已变更");
                deliveries.Add(new { item.Id, item.Target, status = "queued" });
            }
            catch (WorkbenchError error)
            {
                deliveries.Add(new { target, status = "failed", error = error.Message });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                deliveries.Add(new { target, status = "failed", error = "无法读取外部投递的交接文档，请检查文件权限或占用情况" });
            }
        }
        return new { deliveries };
    }

    internal static InboxItem Send(WorkbenchStore state, string grant, string? targetValue, string? messageValue,
        string? documentValue, string? resendValue, string[] files, string? hint, bool supersedePrevious, string? replyTo=null, bool? finalReply=null)
    {
        if (resendValue != null)
        {
            if (messageValue != null || documentValue != null || replyTo != null || finalReply != null)
                throw new WorkbenchError("重发时请省略 message 和 document");
            string? address = targetValue == null ? null : WorkbenchInput.Text(targetValue, "收件 Agent", 160);
            string id=WorkbenchInput.Id(resendValue);
            return state.ResendPeer(grant,id,address,supersedePrevious) ?? state.Resend(grant, id, address, supersedePrevious);
        }
        string target = WorkbenchInput.Text(targetValue, "收件 Agent", 160);
        string message = Message(messageValue, documentValue, files);
        hint = NormalizeHint(hint);
        if (finalReply.HasValue && replyTo==null) throw new WorkbenchError("请用 replyTo 指定回信对应的 MSG_ID");
        if (replyTo!=null && hint!=null) throw new WorkbenchError("关联回信不能同时要求对方再次回信");
        return state.SendFederated(grant,target,message,files,documentValue,hint,supersedePrevious,replyTo,finalReply)
            ?? state.Send(grant, target, message, files, hint, documentValue, supersedePrevious,replyTo,finalReply??true);
    }

    private static string Message(string? text, string? document, string[] files)
    {
        if (files.Length > 20) throw new WorkbenchError("一次最多附带 20 个文件");
        return WorkbenchInput.Text(text, document == null ? "消息" : "文档摘要", document == null ? 120_000 : 500);
    }

    private static string? NormalizeHint(string? hint)
    {
        hint = hint?.Trim();
        if (string.IsNullOrEmpty(hint) || hint.Equals("false", StringComparison.OrdinalIgnoreCase)) return null;
        return hint.Equals("true", StringComparison.OrdinalIgnoreCase) ? ReturnHint : WorkbenchInput.Text(hint, "回送提示", 1_000);
    }
}
