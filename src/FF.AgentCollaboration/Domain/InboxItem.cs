namespace FF.AgentCollaboration.Domain;

internal sealed record InboxItem(string Id, string Target, string From, string Text, string[] Files,
    string? Document, string? ReturnHint, string State)
{
    internal const string ChatUser = "workbench-user";
    internal const string ChatUserAddress = "@工作台用户";

    internal bool ProjectDocument { get; init; }
    public string? FromTopic { get; init; }
    public string? Source { get; init; }
    public string? ReplyTo { get; init; }
    public bool? FinalReply { get; init; }
    public bool Acknowledged { get; init; }
    public string? NativeOffer { get; init; }
    internal string? QueueHint { get; init; }

    internal string AsText()
    {
        string sender = FromTopic == null ? From : FromTopic + "/" + From;
        var sections = new List<string> { $"MSG_ID: {Id[..8]}", $"FROM_AGENT: {sender}" };
        if (Document == null) sections.Add("MSG: " + Text);
        else
        {
            if (!string.IsNullOrWhiteSpace(Text)) sections.Add("SUMMARY: " + Text);
            sections.Add("HANDOFF_DOC: " + Document);
        }
        if (Files.Length != 0) sections.Add("ATTACH_FILES: " + string.Join("; ", Files));
        if (ReplyTo != null) sections.Add($"REPLY_TO: {ReplyTo} | REPLY_KIND: {(FinalReply == false ? "progress" : "result")}");
        if (ReturnHint != null) sections.Add("RETURN_HINT: " + ReturnHint);
        else if (Source == ChatUser)
            sections.Add($"GROUP_REPLY: 回答用户的提问或讨论时，用 {CollaborationTools.Send} 向 {ChatUserAddress} 发送答复，replyTo 填本条 MSG_ID。");
        var text = string.Join(" | ", sections).ReplaceLineEndings(" ");
        var clean = text.Select(character => char.IsControl(character) ? ' ' : character);
        return new string(clean.ToArray());
    }
}
