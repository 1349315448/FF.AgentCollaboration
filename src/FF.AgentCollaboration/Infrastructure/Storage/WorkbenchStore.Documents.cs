using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed record TopicDocument(string Id, string Name, string Path);

internal sealed partial class WorkbenchStore
{
    private const string TopicDocumentRows = """
        FROM inbox i LEFT JOIN windows s ON s.id=i.source LEFT JOIN topics st ON st.id=s.topic_id
        WHERE (i.topic_id=$p0 OR s.topic_id=$p0) AND (i.document=1 OR i.document_ref IS NOT NULL)
        """;

    internal TopicDocument[] WorkspaceDocuments(string topic)
    {
        RegisteredProject(Topic(topic).Project);
        string handoffs = Handoffs;
        return Read("SELECT i.id,i.document,coalesce(i.document_ref,i.id) " + TopicDocumentRows + " ORDER BY i.rowid DESC",
            row => {
                string reference = row.GetString(2);
                string path = row.GetBoolean(1) ? DocumentPath(handoffs, reference) : reference;
                return new TopicDocument(row.GetString(0), Path.GetFileName(path), path);
            }, topic).DistinctBy(x => x.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    internal string WorkspaceDocument(string topic, string id)
    {
        RegisteredProject(Topic(topic).Project);
        var item = Read("SELECT i.document,coalesce(i.document_ref,i.id),s.project,st.folder " + TopicDocumentRows + " AND i.id=$p1",
            row => new { Managed = row.GetBoolean(0), Reference = row.GetString(1), Project = Optional(row,2), Folder = Optional(row, 3) },
            topic, id).SingleOrDefault() ?? throw new WorkbenchError("该文档不属于当前话题", WorkbenchFailure.Forbidden);
        return item.Managed ? DocumentPath(Handoffs, item.Reference) : ProjectMarkdown(item.Project ?? throw new WorkbenchError("找不到文档来源项目"), item.Folder, item.Reference);
    }
}
