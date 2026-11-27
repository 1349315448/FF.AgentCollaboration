using System.Collections.Concurrent;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace FF.AgentCollaboration.Application;

internal sealed record TeamEngineQuota(string Status, decimal? PrimaryRemainingPercent, decimal? SecondaryRemainingPercent,
    bool Unlimited, DateTimeOffset? CheckedAt);
internal sealed record TeamEngineAvailability(string Engine, bool Installed, string Account, TeamEngineQuota? Quota,
    string? DefaultModel, IReadOnlyList<ModelChoice> Models);

internal sealed class SessionCatalog(WorkbenchStartup startup) : IDisposable
{
    private readonly SemaphoreSlim quotaQuery = new(1);
    private static readonly TimeSpan QuotaLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, EngineQuota> quotas = new();
    private readonly MemoryCache teamCatalog = new(new MemoryCacheOptions { SizeLimit = 512 });

    internal async Task<IReadOnlyList<ModelChoice>> Models(string name, string? project, CancellationToken request)
    {
        using var state = new WorkbenchStore(startup.Root);
        string folder = ProjectFolders.Resolve(project ?? state.ProjectList().Current ?? Directory.GetCurrentDirectory());
        using var engine = Engine.Create(EngineNames.Require(name), folder);
        using var timeout = Deadline(request);
        var models = await engine.ListModels(timeout.Token);
        teamCatalog.Set(TeamCatalogKey(name, folder, "models"), models,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = QuotaLifetime });
        return models;
    }

    internal async Task<EngineAccount> Account(string name, string project, CancellationToken request)
    {
        string folder = ProjectFolders.Resolve(project);
        var account = await EngineAuthentication.Check(EngineNames.Require(name), folder, request);
        teamCatalog.Set(TeamCatalogKey(name, folder, "account"), account.Status,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = QuotaLifetime });
        return account;
    }

    private static (string Engine, string Project, string Kind) TeamCatalogKey(string engine, string project, string kind) =>
        (engine, OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project, kind);

    // 组队 MCP 只读取已探测的账号 / 模型 / 额度，不拉起原生引擎或刷新远程账号。
    internal TeamEngineAvailability[] AvailableTeamEngines(string project) => TeamEngines(project, this);

    internal static TeamEngineAvailability[] TeamEngines(string project, SessionCatalog? source = null)
    {
        var available = new List<TeamEngineAvailability>();
        foreach (string name in EngineNames.All)
        {
            if (EngineExecutable.TryFindExecutable(name) == null) continue;
            string account = source?.teamCatalog.Get<string>(TeamCatalogKey(name, project, "account")) ?? "unknown";
            var models = source?.teamCatalog.Get<IReadOnlyList<ModelChoice>>(TeamCatalogKey(name, project, "models"));
            if (models == null)
            {
                using var engine = Engine.Create(name, project);
                models = engine.KnownModels;
            }
            var value = source?.quotas.GetValueOrDefault(name);
            TeamEngineQuota? quota = value == null ? null : new(value.Status, value.Primary?.RemainingPercent,
                value.Secondary?.RemainingPercent, value.Unlimited || value.Credits?.Unlimited == true, value.CheckedAt);
            available.Add(new(name, true, account, quota, models.FirstOrDefault(model => model.IsDefault)?.Id, models));
        }
        return available.ToArray();
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
        string folder = ProjectFolders.Resolve(project ?? state.ProjectList().Current ?? Directory.GetCurrentDirectory());
        using var engine = Engine.Create(engineName, folder);
        using var timeout = Deadline(request);
        var history = await engine.ListSessions(timeout.Token, archived);
        var bound = state.Windows().Where(w => w.Engine == engineName && w.Session != null).Select(w => w.Session).ToHashSet();
        var archives = !engine.Capabilities.NativeArchive ? state.ArchivedSessions(engineName) : null;
        return history.Where(s => archives == null || archives.Contains(s.Id) == archived)
            .Select(s => {
                bool nativeInUse = s.MayBeInUse || engine.HasNativeWriter(s.Id);
                return s with { NativeInUse = nativeInUse, MayBeInUse = bound.Contains(s.Id) || nativeInUse };
            })
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

    public void Dispose()
    {
        quotaQuery.Dispose();
        teamCatalog.Dispose();
    }
}
