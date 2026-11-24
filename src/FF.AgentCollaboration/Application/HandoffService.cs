using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal static class HandoffService
{
    private const string ReturnHint = "完成后请用 send_to_agent 回送：结论、改动文件清单、验证结果；详情写入文档并用 document 传路径。";

    internal static InboxItem Send(WorkbenchStore state, string grant, string? targetValue, string? messageValue,
        string? documentValue, string? resendValue, string[] files, string? hint)
    {
        if (resendValue != null)
        {
            if (messageValue != null || documentValue != null)
                throw new WorkbenchError("重发时请省略 message 和 document");
            string? address = targetValue == null ? null : WorkbenchInput.Text(targetValue, "收件 Agent", 160);
            return state.Resend(grant, WorkbenchInput.Id(resendValue), address);
        }
        string target = WorkbenchInput.Text(targetValue, "收件 Agent", 160);
        string message = WorkbenchInput.Text(messageValue, documentValue == null ? "消息" : "文档摘要", documentValue == null ? 120_000 : 500);
        if (files.Length > 20) throw new WorkbenchError("一次最多附带 20 个文件");
        if (hint != null)
        {
            hint = hint.Trim();
            if (hint.Equals("true", StringComparison.OrdinalIgnoreCase))
                hint = ReturnHint;
            else if (hint.Equals("false", StringComparison.OrdinalIgnoreCase) || hint.Length == 0)
                hint = null;
            else
                hint = WorkbenchInput.Text(hint, "回送提示", 1_000);
        }
        return state.Send(grant, target, message, files, hint, documentValue);
    }
}
