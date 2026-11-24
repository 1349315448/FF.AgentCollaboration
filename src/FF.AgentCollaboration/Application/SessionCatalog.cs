using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal sealed class SessionCatalog(WorkbenchStartup startup) : IDisposable
{
    private readonly SemaphoreSlim quotaQuery = new(1);
    private static readonly TimeSpan QuotaLifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, EngineQuota> quotas = new();

    internal async Task<IReadOnlyList<ModelChoice>> Models(string name, string? project, CancellationToken request)
    {
        using var state = new WorkbenchStore(startup.Root);
        using var engine = Engine.Create(EngineNames.Require(name), ProjectFolders.Resolve(project ?? state.Project()));
        using var timeout = Deadline(request);
        return await engine.ListModels(timeout.Token);
    }

    internal async Task<EngineQuota> Quota(string name, bool refresh, CancellationToken request)
    {
        string engineName = EngineNames.Require(name);
        await quotaQuery.WaitAsync(request);
        try
        {
            if (!refresh && quotas.TryGetValue(engineName, out var cached) &&
                cached.CheckedAt is { } checkedAt && DateTimeOffset.UtcNow - checkedAt < QuotaLifetime)
                return cached;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var engine = Engine.Create(engineName, Directory.GetCurrentDirectory());
            try { return quotas[engineName] = await engine.ReadQuota(deadline.Token); }
            catch (Exception) when (!request.IsCancellationRequested)
            {
                string label = engineName;
                return quotas[engineName] = new(false, $"暂时无法读取 {label} 额度，请稍后刷新。", CheckedAt: DateTimeOffset.UtcNow);
            }
        }
        finally { quotaQuery.Release(); }
    }

    internal async Task<IReadOnlyList<HistorySession>> Sessions(string name, string? project, CancellationToken request,
        bool archived = false, bool includeBound = false)
    {
        using var state = new WorkbenchStore(startup.Root);
        string engineName = EngineNames.Require(name);
        string folder = ProjectFolders.Resolve(project ?? state.Project());
        using var engine = Engine.Create(engineName, folder);
        using var timeout = Deadline(request);
        var history = await engine.ListSessions(timeout.Token, archived);
        var bound = state.Windows().Where(w => w.Engine == engineName && w.Session != null).Select(w => w.Session).ToHashSet();
        var archives = engineName != "codex" ? state.ArchivedSessions(engineName) : null;
        return history.Where(s => archives == null || archives.Contains(s.Id) == archived)
            .Select(s => s with { MayBeInUse = bound.Contains(s.Id) || s.MayBeInUse || engine.HasNativeWriter(s.Id) })
            .Where(s => includeBound || !s.MayBeInUse)
            .OrderByDescending(session => DateTimeOffset.TryParse(session.UpdatedAt, out var updated) ? updated : DateTimeOffset.MinValue)
            .ToArray();
    }

    internal static CancellationTokenSource Deadline(CancellationToken cancellation)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        return deadline;
    }

    public void Dispose() => quotaQuery.Dispose();
}
