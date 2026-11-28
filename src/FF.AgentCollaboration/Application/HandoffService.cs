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
        var local = state.PostAgentChat(grant,[],Message(text,document,files),files,document,NormalizeHint(hint),
            allMembers:true,external:externalTargets.Select(WorkbenchInput.Id).ToArray());
        return DeliveryList(local);
    }

    internal static object SendGroup(WorkbenchStore state, string grant, string[] targets, string? text, string? document,
        string[] files, string? hint, bool supersede) => DeliveryList(state.PostAgentChat(grant, targets.Select(target =>
            WorkbenchInput.Text(target,"收件 Agent",160)).ToArray(),Message(text,document,files),files,document,NormalizeHint(hint),
            allMembers:false,external:[],supersede:supersede));

    private static object DeliveryList(InboxItem[] items) =>
        new { deliveries = items.Select(item => new { item.Id, item.Target, status = "queued" }).ToArray() };

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
        if (target == InboxItem.ChatUserAddress)
        {
            if (hint != null || (replyTo == null && files.Length == 0 && documentValue == null && !HasDeliveryLink(message)))
                throw new WorkbenchError("回复工作台用户时请关联 MSG_ID；主动交付时请附上文件或网址");
            return state.PostToUserChat(grant,replyTo,message,files,documentValue,finalReply ?? true);
        }
        return state.SendGroupCollaboration(grant,target,message,files,documentValue,hint,supersedePrevious,replyTo,finalReply)
            ?? state.SendFederated(grant,target,message,files,documentValue,hint,supersedePrevious,replyTo,finalReply)
            ?? state.Send(grant, target, message, files, hint, documentValue, supersedePrevious,replyTo,finalReply??true);
    }

    private static bool HasDeliveryLink(string message) =>
        System.Text.RegularExpressions.Regex.Matches(message, """https?://[^\s<>"«»“”，。！？；、]+""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Any(m => Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.UserInfo.Length == 0);

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
