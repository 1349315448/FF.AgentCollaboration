namespace FF.AgentCollaboration.Domain;

internal sealed record InboxItem(string Id, string Target, string From, string Text, string[] Files,
    string? Document, string? ReturnHint, string State)
{
    internal bool ProjectDocument { get; init; }
    public string? FromTopic { get; init; }
    public string? Source { get; init; }
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
        if (ReturnHint != null) sections.Add("RETURN_HINT: " + ReturnHint);
        var text = string.Join(" | ", sections).ReplaceLineEndings(" ");
        var clean = text.Select(character => char.IsControl(character) ? ' ' : character);
        return new string(clean.ToArray());
    }
}
