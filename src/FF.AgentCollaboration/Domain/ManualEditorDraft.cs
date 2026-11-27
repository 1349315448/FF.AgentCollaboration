namespace FF.AgentCollaboration.Domain;

internal sealed record ManualEditorDraft(string Generation, long Revision, string Text)
{
    internal bool Restored { get; set; }
}
