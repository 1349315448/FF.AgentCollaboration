using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private void InitializeChatLinkMembers()
    {
        var columns = Read("PRAGMA table_info(chat_group_link)", row => row.GetString(1));
        if (!columns.Contains("first_mode")) Exec("ALTER TABLE chat_group_link ADD COLUMN first_mode TEXT NOT NULL DEFAULT 'legacy'");
        if (!columns.Contains("second_mode")) Exec("ALTER TABLE chat_group_link ADD COLUMN second_mode TEXT NOT NULL DEFAULT 'legacy'");
        if (!columns.Contains("first_members"))
            Exec("ALTER TABLE chat_group_link ADD COLUMN first_members TEXT NOT NULL DEFAULT '[]'");
        if (!columns.Contains("second_members"))
            Exec("ALTER TABLE chat_group_link ADD COLUMN second_members TEXT NOT NULL DEFAULT '[]'");
    }

    private static GroupPair ReadChatPair(Microsoft.Data.Sqlite.SqliteDataReader row) =>
        new(row.GetString(0), row.GetString(1), JsonSerializer.Deserialize<string[]>(row.GetString(2))!,
            JsonSerializer.Deserialize<string[]>(row.GetString(3))!,row.GetString(4),row.GetString(5));

    private const string ChatPairFields="first,second,first_members,second_members,first_mode,second_mode";

    internal List<GroupPair> GroupPairs() =>
        Read($"SELECT {ChatPairFields} FROM chat_group_link", ReadChatPair);

    // 本组消息照常送达。跨组只核对这一对小组的名单和现任组长。
    private bool GroupRoute(WorkbenchTopic from,string? sender,WorkbenchTopic to,string target)
    {
        if (to.Status != "active") return false;
        if (from.Id == to.Id) return true;
        if (from.Status != "active" || !ProjectFolders.Same(from.Project,to.Project) ||
            sender == null || from.LeaderWindowId == null || to.LeaderWindowId == null) return false;
        var pair = Read($"""
            SELECT {ChatPairFields} FROM chat_group_link
            WHERE first=min($p0,$p1) AND second=max($p0,$p1)
            """,ReadChatPair,from.Id,to.Id).SingleOrDefault();
        if (pair == null) return false;
        bool first = pair.First == from.Id;
        bool allowed(string id,string? leader,string[] members,string mode) =>
            mode=="all" || members.Contains(id) || mode=="legacy" && leader==id;
        return (sender==InboxItem.ChatUser || allowed(sender,from.LeaderWindowId,
                first?pair.FirstMembers:pair.SecondMembers,first?pair.FirstMode:pair.SecondMode) && !DedicatedReader(sender)) &&
            allowed(target,to.LeaderWindowId,first?pair.SecondMembers:pair.FirstMembers,first?pair.SecondMode:pair.FirstMode) &&
            !DedicatedReader(target);
    }

    // 一次保存可以修改多对合作关系。任一名单无效时，整个保存回滚。
    internal void ConfigureChatLinks(string id,ChatLinkSelection[] changes) => Atomic(() =>
    {
        if (changes.Length > 100 || changes.Any(change => change == null) ||
            changes.Select(change => change.Group).Distinct(StringComparer.Ordinal).Count() != changes.Length)
            throw new WorkbenchError("请选择有效且不重复的小组，每次最多保存 100 个");
        var source = Topic(id);
        RegisteredProject(source.Project);
        foreach (var change in changes)
        {
            var target = Topic(WorkbenchInput.Id(change.Group));
            if (source.Id == target.Id || !ProjectFolders.Same(source.Project,target.Project))
                throw new WorkbenchError("只能关联同一项目中的其他小组",WorkbenchFailure.Forbidden);
            if (!change.Enabled)
            {
                Exec("DELETE FROM chat_group_link WHERE first=min($p0,$p1) AND second=max($p0,$p1)",source.Id,target.Id);
                continue;
            }
            var local = ChatLinkMembers(source,change.Members,change.MemberMode);
            var peer = ChatLinkMembers(target,change.PeerMembers,change.PeerMode);
            Exec("""
                INSERT INTO chat_group_link(first,second,first_members,second_members,first_mode,second_mode)
                VALUES(min($p0,$p1),max($p0,$p1),CASE WHEN $p0<$p1 THEN $p2 ELSE $p3 END,
                    CASE WHEN $p0<$p1 THEN $p3 ELSE $p2 END,
                    CASE WHEN $p0<$p1 THEN $p4 ELSE $p5 END,CASE WHEN $p0<$p1 THEN $p5 ELSE $p4 END)
                ON CONFLICT(first,second) DO UPDATE SET first_members=excluded.first_members,second_members=excluded.second_members,
                    first_mode=excluded.first_mode,second_mode=excluded.second_mode
                """,source.Id,target.Id,JsonSerializer.Serialize(local),JsonSerializer.Serialize(peer),change.MemberMode??"legacy",change.PeerMode??"legacy");
        }
    });

    private string[] ChatLinkMembers(WorkbenchTopic topic,string[]? members,string? mode)
    {
        if (topic.Status != "active" || topic.LeaderWindowId is not string leader)
            throw new WorkbenchError("请先为参与合作的小组设置组长",WorkbenchFailure.Conflict);
        return SelectGroupMembers(Window(leader),members ?? [],mode);
    }
}
