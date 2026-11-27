namespace FF.AgentCollaboration.Domain;

internal sealed record KnowledgeQuery(string? Query = null, string? Scope = null, string? Theme = null,
    string[]? Keywords = null, string? TopicId = null, int? Limit = null, string? AgentId = null,
    bool RecordsOnly = false, int Offset = 0);

internal sealed record KnowledgeWrite(string? Scope, string? Path, string? Content, string? Title = null, string? Theme = null,
    string[]? Keywords = null, string? Status = null, string? ExpectedVersion = null);

internal sealed record KnowledgeMetadata(string Id, string Scope, string? TopicId, string? AgentId, string Title,
    string? Theme, string[] Keywords, string? Owner, string? Status, string Updated);

internal sealed record KnowledgeDocument(string Path, string Content, string Body, KnowledgeMetadata Frontmatter, string Version)
{
    internal DateTime Modified { get; init; }
    internal long Size { get; init; }
}

internal sealed record KnowledgeHit(string Path, string Scope, string? TopicId, string? AgentId, string Title,
    string? Theme, string[] Keywords, string? Owner, string? Status, string Snippet, string Updated);
