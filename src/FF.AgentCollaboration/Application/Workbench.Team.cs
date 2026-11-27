using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FF.AgentCollaboration.Application;

internal sealed partial class Workbench
{
    private sealed record TeamCandidate(int Index, AgentWindow Window, string Task);
    private sealed record TeamInvitation(int Index, string RemoteId, string Role, string Task);

    internal async Task<TeamCreationResult> ConfirmTeamProposal(string topicId, string id, TeamConfirmation input, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var topic = state.Topic(topicId);
            var proposal = state.TeamProposal(topicId, id);
            if (proposal.Status != "pending" || topic.Status != "active" || topic.LeaderWindowId != proposal.LeaderWindowId)
                throw new WorkbenchError("组建提案已失效，请重新打开确认窗", WorkbenchFailure.Conflict);
            var invitations = new List<TeamInvitation>();
            var candidates = PrepareCandidates(topic, proposal, input, invitations);
            foreach (var candidate in candidates)
            {
                var window = candidate.Window;
                using var engine = Engine.Create(window.Engine, window.Project);
                RequireSessionOperation(window, engine.Capabilities, "new");
            }
            foreach (var group in candidates.GroupBy(member => member.Window.Engine))
                await EngineAuthentication.Require(group.Key, group.First().Window.Project, ct);
            ct.ThrowIfCancellationRequested();

            // 确认提案和登记全部窗口共用短事务，启动终端在提交后逐个进行。
            var invited = new List<TeamCreatedMember>();
            state.ClaimTeamProposal(topicId, id, current =>
            {
                var names = state.Windows().Where(window => window.TopicId == topicId)
                    .Select(window => window.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var member in candidates)
                {
                    if (!names.Add(member.Window.Name))
                        throw new WorkbenchError($"此话题已有名为 @{member.Window.Name} 的 Agent", WorkbenchFailure.Conflict);
                    state.AddWindow(member.Window);
                    state.RecordTeamTask(member.Window.Id, member.Task);
                }
                foreach (var invitation in invitations)
                {
                    var peer = state.AddExternalTeamMember(topicId, current.LeaderWindowId, invitation.RemoteId, invitation.Role, invitation.Task);
                    invited.Add(new(invitation.Index, peer.AgentName, null, peer.Connected && peer.Online, RemoteId: peer.Id));
                }
            });

            return await CompleteTeamCreation(state, proposal, candidates, invited);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new WorkbenchError("这个 Agent 名称或原生会话已经使用", WorkbenchFailure.Conflict);
        }
        finally
        {
            lifecycle.Release();
            events.Changed();
        }
    }

    private async Task<TeamCreationResult> CompleteTeamCreation(WorkbenchStore state, TeamProposal proposal, TeamCandidate[] candidates,
        List<TeamCreatedMember> results)
    {
        try
        {
            // 用户确认已经持久化。浏览器断开后继续按既有引擎超时完成启动。
            foreach (var candidate in candidates)
                results.Add(await StartTeamCandidate(state, candidate));
            results.Sort((a, b) => a.Index.CompareTo(b.Index));
            for (int i = 0; i < results.Count; i++)
            {
                var member = results[i];
                if (member.WindowId != null && !Terminal(member.WindowId).Online)
                    results[i] = member with { Online = false, Error = "引擎启动后已退出，请检查窗格并重新打开终端" };
            }

            bool hasMembers = results.Any(member => member.WindowId != null || member.RemoteId != null);
            string? rosterWarning = hasMembers ? SyncTeamRoster(proposal.TopicId) : null;
            string status = results.All(member => member.Online || member.RemoteId != null) ? "created" : hasMembers ? "partial" : "failed";
            var selected = results.Select(member => member.Index).ToHashSet();
            string notice = TeamNotice(proposal, selected, results, rosterWarning);
            state.FinishTeamProposal(proposal.TopicId, proposal.Id, status, current => state.QueueTeamNotice(current, notice));
            return new(status, results, rosterWarning);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            const string interrupted = "团队创建中断。请查看已经登记的成员与在线状态。";
            logger.LogWarning("Team creation interrupted: {Type}", error.GetType().Name);
            try
            {
                state.FinishTeamProposal(proposal.TopicId, proposal.Id, "interrupted",
                    current => state.QueueTeamNotice(current, interrupted));
            }
            catch (SqliteException storageError)
            {
                // 无法提交结果时保留已登记的窗口，数据库可用后由启动迁移完成中断标记。
                logger.LogWarning("Could not record interruption of team creation: {Type}", storageError.GetType().Name);
            }
            throw new WorkbenchError(interrupted, WorkbenchFailure.Unavailable);
        }
    }

    private TeamCandidate[] PrepareCandidates(WorkbenchTopic topic, TeamProposal proposal, TeamConfirmation input, List<TeamInvitation> invitations)
    {
        if (input.Members is not { Length: >= 1 and <= 8 } members)
            throw new WorkbenchError("请至少勾选一位建议成员");
        var indices = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<TeamCandidate>(members.Length);
        foreach (var selection in members)
        {
            if (selection is null || selection.Index < 0 || selection.Index >= proposal.Members.Length || !indices.Add(selection.Index))
                throw new WorkbenchError("提案成员编号无效或重复");
            var member = proposal.Members[selection.Index];
            if ((selection.RemoteId ?? member.RemoteId) is { Length: > 0 } remoteId)
            {
                string role = WorkbenchInput.Text(selection.Role ?? member.Role, "团队职责", 200);
                invitations.Add(new(selection.Index, WorkbenchInput.Id(remoteId),
                    WorkbenchInput.AgentRole(role) ?? throw new WorkbenchError("请填写团队职责"), member.TaskSummary));
                continue;
            }
            var request = new CreateAgent(selection.Name ?? member.Name, topic.Project, selection.Engine,
                selection.Model, null, selection.Effort, selection.Mode, NewSession: true, Topic: topic.Id,
                Approval: EmptyToNull(selection.Approval), Role: (selection.Role ?? member.Role).ReplaceLineEndings(" "));
            var window = CreateCandidate(request, topic) with { NewSessionPending = true };
            if (!names.Add(window.Name)) throw new WorkbenchError("已选 Agent 的名称不能重复");
            if (updatingEngines.ContainsKey(window.Engine))
                throw new WorkbenchError("此引擎正在更新，请稍后确认创建", WorkbenchFailure.Conflict);
            candidates.Add(new(selection.Index, window, member.TaskSummary));
        }
        return candidates.OrderBy(member => member.Index).ToArray();
    }

    private async Task<TeamCreatedMember> StartTeamCandidate(WorkbenchStore state, TeamCandidate member)
    {
        try
        {
            var opened = await Open(state, member.Window, CancellationToken.None);
            return new(member.Index, opened.Name, opened.Id, true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string id = member.Window.Id;
            if (terminals.TryRemove(id, out var failed))
            {
                state.Offline(id);
                await Task.Run(failed.Dispose);
            }
            state.RemoveUnstartedWindow(id);
            logger.LogWarning("Team member startup failed ({Engine}): {Type}", member.Window.Engine, error.GetType().Name);
            string reason = error is OperationCanceledException ? "引擎启动超时" : "无法启动所选引擎，请检查账号和启动配置";
            return new(member.Index, member.Window.Name, null, false, reason);
        }
    }

    private static string TeamNotice(TeamProposal proposal, HashSet<int> selected, List<TeamCreatedMember> results, string? rosterWarning)
    {
        var parts = new List<string> { "用户已确认团队组建。" };
        var external = results.Where(member => member.RemoteId != null).Select(member => "@" + member.Name).ToArray();
        var online = results.Where(member => member.WindowId != null && member.Online).Select(member => "@" + member.Name).ToArray();
        var offline = results.Where(member => member.WindowId != null && !member.Online).Select(member => "@" + member.Name).ToArray();
        var failed = results.Where(member => member.WindowId == null && member.RemoteId == null).Select(member => "@" + member.Name).ToArray();
        var skipped = proposal.Members.Where((_, index) => !selected.Contains(index)).Select(member => "@" + member.Name).ToArray();
        if (online.Length > 0) parts.Add($"已创建并在线待命：{string.Join("、", online)}。请组长按 PLAN.md 安排任务。");
        if (external.Length > 0) parts.Add($"已加入外部成员：{string.Join("、", external)}。请组长用成员 ID 投递任务并按原消息跟踪回执。");
        if (offline.Length > 0) parts.Add($"已创建但终端退出：{string.Join("、", offline)}。");
        if (failed.Length > 0) parts.Add($"启动失败：{string.Join("、", failed)}。");
        if (skipped.Length > 0) parts.Add($"用户未选：{string.Join("、", skipped)}。");
        if (rosterWarning != null) parts.Add(rosterWarning);
        return string.Join(' ', parts);
    }

    internal void RejectTeamProposal(string topicId, string id)
    {
        using var state = new WorkbenchStore(Root);
        state.RejectTeamProposal(topicId, id, proposal =>
            state.QueueTeamNotice(proposal, "用户拒绝了本次团队组建提案。请在对话中与用户确认后调整方案。"));
        events.Changed();
    }

    internal string? SyncTeamRoster(string topicId)
    {
        try
        {
            using var state = new WorkbenchStore(Root);
            WorkbenchKnowledge.RefreshTeamRoster(state, topicId);
            return null;
        }
        catch (Exception error) when (error is WorkbenchError or IOException or UnauthorizedAccessException or SqliteException)
        {
            logger.LogWarning("Could not synchronize the team roster: {Type}", error.GetType().Name);
            return "团队配置已保存，暂时无法更新 TEAM.md。请检查项目知识库。";
        }
    }
}
