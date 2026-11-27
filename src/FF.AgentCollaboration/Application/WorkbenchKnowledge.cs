using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal static class WorkbenchKnowledge
{
    private const string Disabled = "disabled";
    private const string AgentCollection = "agents";
    private const string TopicCollection = "topics";
    private const string RosterStart = "<!-- ff-acp:team:start -->";
    private const string RosterEnd = "<!-- ff-acp:team:end -->";
    private const string RecordsLink = "[成员任务记录](records/)";

    internal static string GetMode(WorkbenchStore db, string project) => db.KnowledgeMode(db.RegisteredProject(project));

    internal static string Configure(WorkbenchStore db, string project, string? mode, bool register = false)
    {
        project = register ? ProjectFolders.Resolve(project) : db.RegisteredProject(project);
        if (register && mode == null)
        {
            db.SelectProject(project);
            return db.KnowledgeMode(project);
        }
        return InProject(project, () =>
        {
            string selected = mode ?? db.KnowledgeMode(project);
            if (selected is not (Disabled or "shared" or "local_only"))
                throw new WorkbenchError("请选择共享、仅本机或停用知识库");
            var files = new KnowledgeFiles(project);
            db.ConfigureKnowledgeMode(project, selected, register, () =>
            {
                if (selected == Disabled) return;
                KnowledgeFiles.EnsureDirectory(files.Root);
                KnowledgeGitRules.Configure(project, files.Root, selected == "local_only");
                foreach (string section in new[] { "project", TopicCollection, AgentCollection })
                    KnowledgeFiles.EnsureDirectory(Path.Combine(files.Root, section));
                Seed(files, "project/INDEX.md", "project", null, null, "项目知识索引",
                    "记录在整个项目中可复用的入口、设计决定与排查经验。");
                new ProjectScope(db, project, files).PrepareTemplates();
                KnowledgeIndex.Open(files, _ => true);
            });
            return selected;
        });
    }

    internal static KnowledgeHit[] Search(WorkbenchStore db, string project, KnowledgeQuery query,
        AgentWindow? caller = null, CancellationToken ct = default)
    {
        project = Project(db, project, caller);
        if (query.Scope != null) KnowledgeFiles.Scope(query.Scope);
        if (query.Query?.Length > 200 || query.Theme?.Length > 100 || query.Keywords?.Length > 30 ||
            (query.Keywords?.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 80) ?? false))
            throw new WorkbenchError("知识库搜索词过长或关键词数量过多");
        if (query.TopicId != null) query = query with { TopicId = WorkbenchInput.Id(query.TopicId) };
        if (query.AgentId != null) query = query with { AgentId = WorkbenchInput.Id(query.AgentId) };
        if (caller != null)
        {
            if ((query.TopicId != null && query.TopicId != caller.TopicId) || (query.AgentId != null && query.AgentId != caller.Id))
                throw Forbidden();
            query = query with { TopicId = caller.TopicId };
        }
        if (query.Offset < 0 || (query.RecordsOnly && (query.TopicId == null || query.Scope is not (null or "topic"))))
            throw new WorkbenchError("请指定当前话题和有效的分页位置");
        return InProject(project, () =>
        {
            var files = RequireFiles(db, project);
            var scope = new ProjectScope(db, project, files);
            if (caller == null && !query.RecordsOnly) scope.PrepareTemplates(query.TopicId);
            string? recordsPrefix = null;
            if (query.RecordsOnly)
            {
                var topic = db.Topic(query.TopicId!);
                if (!ProjectFolders.Same(project, topic.Project)) throw Forbidden();
                recordsPrefix = scope.TopicDirectory(topic.Id, topic.Name) + "/records/";
            }
            return KnowledgeIndex.Open(files, index =>
            {
                index.Synchronize(files, scope.Identify, ct);
                return index.Search(query, caller, recordsPrefix);
            });
        });
    }

    internal static KnowledgeDocument Read(WorkbenchStore db, string project, string path, AgentWindow? caller = null)
    {
        project = Project(db, project, caller);
        return InProject(project, () =>
        {
            var files = RequireFiles(db, project);
            path = files.MarkdownPath(WorkbenchInput.Text(path, "知识库文档位置", 512));
            var address = new ProjectScope(db, project, files).Identify(path) ?? throw Forbidden();
            if (caller != null && (address.Scope == "topic" && address.TopicId != caller.TopicId ||
                address.Scope == "agent" && address.AgentId != caller.Id)) throw Forbidden();
            var document = files.Read(path, address.Scope, address.TopicId, address.AgentId);
            KnowledgeIndex.Open(files, index => { index.Update(document); return true; });
            return document;
        });
    }

    internal static KnowledgeDocument Write(WorkbenchStore db, string project, KnowledgeWrite input, AgentWindow? caller = null,
        string? grant = null)
    {
        project = Project(db, project, caller);
        string requestedScope = KnowledgeFiles.Scope(input.Scope);
        return InProject(project, () =>
        {
            var files = RequireFiles(db, project);
            var document = db.WithKnowledgeWriteAuthorization(() =>
            {
                var scope = new ProjectScope(db, project, files);
                string path = files.MarkdownPath(WorkbenchInput.Text(input.Path, "知识库文档位置", 512));
                if (caller != null)
                {
                    if (grant == null) throw Forbidden();
                    var current = db.Caller(grant);
                    if (current.Id != caller.Id) throw Forbidden();
                    CheckCaller(db, db.RegisteredProject(project), current);
                    path = scope.AgentWritePath(current, requestedScope, path);
                    caller = current;
                }
                var address = scope.Identify(path) ?? throw Forbidden();
                if (address.Scope != requestedScope) throw Forbidden();
                if (address.Scope == "topic")
                {
                    var relative = path.Split('/', 3)[2];
                    bool plan = relative.Equals("PLAN.md", StringComparison.OrdinalIgnoreCase);
                    if ((plan || relative.Equals("TEAM.md", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("records/", StringComparison.OrdinalIgnoreCase)) &&
                        input.ExpectedVersion == null && files.Exists(path))
                        throw new WorkbenchError("请读取现有团队文档的当前版本并带 expectedVersion 保存", WorkbenchFailure.Conflict);
                    if (plan && input.Content != null && !input.Content.AsSpan().TrimEnd().EndsWith("\n" + RecordsLink, StringComparison.Ordinal))
                        input = input with { Content = input.Content + "\n\n" + RecordsLink + "\n" };
                }
                return files.Write(path, address.Scope, address.TopicId, address.AgentId, caller == null ? null : "@" + caller.Name, input);
            });
            KnowledgeIndex.Open(files, index => { index.Update(document); return true; });
            return document;
        });
    }

    internal static void RefreshTeamRoster(WorkbenchStore db, string topicId)
    {
        string project = db.Topic(topicId).Project;
        InProject(project, () =>
        {
            if (db.KnowledgeMode(project) == Disabled) return false;
            var topic = db.Topic(topicId);
            var files = RequireFiles(db, project);
            var scope = new ProjectScope(db, project, files);
            string directory = scope.TopicDirectory(topic.Id, topic.Name);
            string path = directory + "/TEAM.md";
            var old = files.Exists(path) ? files.Read(path, "topic", topic.Id, null) : null;
            var members = db.Windows().Where(window => window.TopicId == topic.Id).ToArray();
            var tasks = db.TeamTasks(topic.Id);
            string block = TeamRosterBlock(members, topic.LeaderWindowId, tasks, db.ExternalTeamMembers(topic.Id, topic.LeaderWindowId));
            string body = old?.Body ?? "# 团队成员\n";
            string refreshed = ReplaceTeamRoster(body, block);
            if (refreshed != body)
            {
                var written = files.Write(path, "topic", topic.Id, null, null,
                    new("topic", path, refreshed, ExpectedVersion: old?.Version));
                KnowledgeIndex.Open(files, index => { index.Update(written); return true; });
            }
            SeedPlan(files, directory, topic);
            return true;
        });
    }

    private static string TeamRosterBlock(IEnumerable<AgentWindow> members, string? leader, Dictionary<string, string> tasks,
        IEnumerable<ExternalTeamMember> external)
    {
        static string Cell(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "—";
            return string.Concat(value.ReplaceLineEndings(" ").Select(character =>
                "\\|`*_{}[]<>()!#~".Contains(character) ? "\\" + character : character.ToString()));
        }
        var lines = new StringBuilder().AppendLine(RosterStart).AppendLine("## 工作台成员").AppendLine()
            .AppendLine("| Agent | 引擎 | 身份提示词 | 初始分工 |").AppendLine("| --- | --- | --- | --- |");
        foreach (var member in members)
        {
            string label = "@" + Cell(member.Name) + (member.Id == leader ? "（组长）" : "");
            lines.AppendLine($"| {label} | {Cell(member.Engine)} | {Cell(member.Role)} | {Cell(tasks.GetValueOrDefault(member.Id))} |");
        }
        foreach (var peer in external)
            lines.AppendLine($"| @{Cell(peer.Name)}（外部成员 · {Cell(peer.NodeName)}） | — | {Cell(peer.Role)} | {Cell(peer.TaskSummary)} |");
        return lines.Append(RosterEnd).ToString();
    }

    private static string ReplaceTeamRoster(string body, string block)
    {
        int start = body.IndexOf(RosterStart, StringComparison.Ordinal);
        int end = body.IndexOf(RosterEnd, StringComparison.Ordinal);
        if (start < 0 && end < 0) return body + "\n\n" + block + "\n";
        if (start < 0 || end < start || body.IndexOf(RosterStart, start + RosterStart.Length, StringComparison.Ordinal) >= 0 ||
            body.IndexOf(RosterEnd, end + RosterEnd.Length, StringComparison.Ordinal) >= 0)
            throw new WorkbenchError("TEAM.md 的成员段标记不完整，请检查文档", WorkbenchFailure.Conflict);
        // 只刷新平台生成的成员段，保留段前、段后的人工记录。
        return body[..start] + block + body[(end + RosterEnd.Length)..];
    }

    internal static object TeamContext(WorkbenchStore db, AgentWindow caller)
    {
        string project = Project(db, caller.Project, caller);
        return InProject(project, () =>
        {
            var files = RequireFiles(db, project);
            var topic = db.Topic(caller.TopicId);
            if (!ProjectFolders.Same(project, topic.Project)) throw Forbidden();
            var tasks = db.TeamTasks(topic.Id);
            var members = db.Windows().Where(w => w.TopicId == topic.Id)
                .Select(w => new { w.Id, w.Name, w.Engine, w.Model, w.Role, w.Online, leader = w.Id == topic.LeaderWindowId,
                    taskSummary = tasks.GetValueOrDefault(w.Id) }).ToArray();
            var scope = new ProjectScope(db, project, files);
            string topicDirectory = scope.TopicDirectory(topic.Id, topic.Name);
            string personalMemory = scope.AgentDirectory(caller.Id, caller.Name) + "/MEMORY.md";
            object Section(string path, string section, string? agent)
            {
                string body = files.Exists(path) ? files.Read(path, section, topic.Id, agent).Body : "";
                return new { path, summary = body[..Math.Min(1600, body.Length)], truncated = body.Length > 1600 };
            }
            return new
            {
                topic = new { topic.Id, topic.Name, topic.Goal, topic.Status },
                leader = members.FirstOrDefault(member => member.leader),
                members = members.Cast<object>().Concat(db.ExternalTeamMembers(topic.Id, caller.Id).Select(peer => new {
                    peer.Id, peer.Name, peer.NodeName, peer.Role, peer.Online, peer.Authorized, peer.TaskSummary, remote = true, leader = false
                })).ToArray(),
                team = Section(topicDirectory + "/TEAM.md", "topic", null),
                plan = Section(topicDirectory + "/PLAN.md", "topic", null),
                taskRecords = new { tool = "kb_search", arguments = new { scope = "topic", topicId = topic.Id, recordsOnly = true } },
                memory = files.Exists(personalMemory) ? Section(personalMemory, "agent", caller.Id) : null
            };
        });
    }

    private static WorkbenchError Forbidden() => new("无权访问此层级的知识库文档", WorkbenchFailure.Forbidden);

    private static void CheckCaller(WorkbenchStore db, string project, AgentWindow caller)
    {
        if (!ProjectFolders.Same(project, caller.Project) || db.DedicatedReader(caller.Id)) throw Forbidden();
    }

    private static string Project(WorkbenchStore db, string project, AgentWindow? caller)
    {
        string path = db.RegisteredProject(project);
        if (caller != null) CheckCaller(db, path, caller);
        return path;
    }

    private static KnowledgeFiles RequireFiles(WorkbenchStore db, string project)
    {
        if (db.KnowledgeMode(db.RegisteredProject(project)) == Disabled)
            throw new WorkbenchError("此项目暂未启用知识库", WorkbenchFailure.Conflict);
        var files = new KnowledgeFiles(project);
        using var guard = WorkspaceDirectoryGuard.Open(files.Root);
        return files;
    }

    private static T InProject<T>(string project, Func<T> operation)
    {
        string identity = OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        using var gate = new Mutex(false, "FF.ACP.Knowledge." + hash);
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(10)))
                throw new WorkbenchError("知识库正在同步，请稍后重试", WorkbenchFailure.Unavailable);
        }
        catch (AbandonedMutexException) { /* 重新以磁盘 Markdown 为准。 */ }
        try { return operation(); }
        finally { gate.ReleaseMutex(); }
    }

    private static void Seed(KnowledgeFiles files, string path, string scope, string? topic, string? agent, string title, string body)
    {
        if (!files.Exists(path))
            files.Write(path, scope, topic, agent, null, new(scope, path, $"# {title}\n\n{body}\n", Title: title), onlyCreate: true);
    }

    private static void SeedPlan(KnowledgeFiles files, string directory, WorkbenchTopic topic) =>
        Seed(files, directory + "/PLAN.md", "topic", topic.Id, null, "话题总计划", (topic.Goal + "\n\n" + RecordsLink).TrimStart());

    private sealed class ProjectScope
    {
        private readonly KnowledgeFiles files;
        private readonly ILookup<string, WorkbenchTopic> topics;
        private readonly ILookup<string, (string Id, string Name, string TopicId, string Project)> agents;

        internal ProjectScope(WorkbenchStore db, string project, KnowledgeFiles files)
        {
            this.files = files;
            topics = db.Topics().Where(t => ProjectFolders.Same(t.Project, project)).ToLookup(t => t.Id[..6], StringComparer.OrdinalIgnoreCase);
            agents = db.KnowledgeMembers().Where(w => ProjectFolders.Same(w.Project, project)).ToLookup(w => w.Id[..6], StringComparer.OrdinalIgnoreCase);
        }

        internal string TopicDirectory(string id, string name) =>
            Directory(TopicCollection, id, name, topics[id[..6]].Skip(1).Any(), create: false);
        internal string AgentDirectory(string id, string name, bool create = false) =>
            Directory(AgentCollection, id, name, agents[id[..6]].Skip(1).Any(), create);

        private string Directory(string collection, string id, string name, bool ambiguous, bool create)
        {
            if (ambiguous)
                throw new WorkbenchError("知识库短编号重复，需要用户核对目录归属", WorkbenchFailure.Conflict);
            return files.ScopeDirectory(collection, id, name, create);
        }

        // 路径决定权限。用户可编辑的 Frontmatter 不能改变文档的作用域或窗口归属。
        internal (string Scope, string? TopicId, string? AgentId)? Identify(string path)
        {
            string[] parts = path.Split('/');
            if (parts.Length >= 2 && parts[0].Equals("project", StringComparison.OrdinalIgnoreCase)) return ("project", null, null);
            if (parts.Length < 3 || parts[1].Length < 8 || parts[1][^7] != '-') return null;
            string suffix = parts[1][^6..];
            if (!suffix.All(Uri.IsHexDigit)) return null;
            if (parts[0].Equals(TopicCollection, StringComparison.OrdinalIgnoreCase))
            {
                var matches = topics[suffix].Take(2).ToArray();
                return ("topic", matches.Length == 1 ? matches[0].Id : null, null);
            }
            if (parts[0].Equals(AgentCollection, StringComparison.OrdinalIgnoreCase))
            {
                var matches = agents[suffix].Take(2).ToArray();
                return matches.Length == 1 ? ("agent", matches[0].TopicId, matches[0].Id) : ("agent", null, null);
            }
            return null;
        }

        internal string AgentWritePath(AgentWindow caller, string scope, string path)
        {
            var topic = topics[caller.TopicId[..6]].FirstOrDefault(t => t.Id == caller.TopicId);
            bool leader = topic?.LeaderWindowId == caller.Id;
            if (scope != "agent" && !leader && (scope == "project" || topic == null))
                throw new WorkbenchError("仅本话题组长可写入项目知识库", WorkbenchFailure.Forbidden);
            string directory = scope switch
            {
                "project" => "project",
                "topic" => TopicDirectory(topic!.Id, topic.Name),
                _ => AgentDirectory(caller.Id, caller.Name)
            };
            string prefix = directory + "/";
            bool rootQualified = Identify(path).HasValue || path.Split('/')[0].ToLowerInvariant() is "project" or TopicCollection or AgentCollection;
            if (rootQualified && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw Forbidden();
            string relative = rootQualified ? path[prefix.Length..] : path;
            if (scope == "topic" && !leader)
            {
                if (!relative.StartsWith("records/", StringComparison.OrdinalIgnoreCase))
                    throw new WorkbenchError("仅本话题组长可修改总计划、花名册和结案摘要", WorkbenchFailure.Forbidden);
                string owner = AgentDirectory(caller.Id, caller.Name, create: true).Split('/')[1] + "/";
                string record = relative[8..];
                string first = record.Split('/')[0];
                bool assigned = record.Contains('/') && first.Length >= 8 && first[^7] == '-' && first[^6..].All(Uri.IsHexDigit);
                if ((rootQualified || assigned) && !record.StartsWith(owner, StringComparison.OrdinalIgnoreCase)) throw Forbidden();
                relative = "records/" + (record.StartsWith(owner, StringComparison.OrdinalIgnoreCase) ? record : owner + record);
            }
            return prefix + relative;
        }

        internal void PrepareTemplates(string? topicId = null)
        {
            foreach (var topic in topics.SelectMany(group => group).Where(t => topicId == null || t.Id == topicId))
            {
                string path = TopicDirectory(topic.Id, topic.Name);
                Seed(files, path + "/TEAM.md", "topic", topic.Id, null, "团队成员", "通过 kb_team_context 获取当前成员与组长。");
                SeedPlan(files, path, topic);
                Seed(files, path + "/SUMMARY.md", "topic", topic.Id, null, "话题总结", "");
            }
        }
    }
}
