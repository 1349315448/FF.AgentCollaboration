using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FF.AgentCollaboration.Application;

// 通道只存“状态已变”，消息正文和认领状态始终由 SQLite 管理。
internal sealed class WorkbenchEvents
{
    private readonly Channel<bool> delivery = Channel.CreateBounded<bool>(1);
    private readonly ConcurrentDictionary<Channel<bool>, byte> pages = new();
    private readonly object ownerLock = new();
    private Guid? owner;
    private CancellationTokenSource? lifetime;
    internal int? NotifyPort { get; set; }

    internal Guid OpenPage()
    {
        var id = Guid.NewGuid();
        CancellationTokenSource? previous;
        lock (ownerLock)
        {
            previous = lifetime;
            owner = id;
            lifetime = new CancellationTokenSource();
        }
        previous?.Cancel();
        previous?.Dispose();
        RefreshPages();
        return id;
    }

    internal CancellationToken PageClosed(Guid id, bool resumeAfterRestart = false)
    {
        lock (ownerLock)
        {
            // 宿主重启后，仍打开的页面可以恢复订阅；旧连接永远不能替换一个现有页面。
            if (owner is null && resumeAfterRestart)
            {
                owner = id;
                lifetime = new CancellationTokenSource();
            }
            return owner == id ? lifetime!.Token : new CancellationToken(true);
        }
    }

    internal void Wake() => delivery.Writer.TryWrite(true);
    internal ValueTask<bool> WaitAsync(CancellationToken ct) => delivery.Reader.ReadAsync(ct);

    internal void Changed()
    {
        Wake();
        RefreshPages();
    }

    internal void RefreshPages()
    {
        foreach (var page in pages.Keys) page.Writer.TryWrite(true);
    }

    internal async IAsyncEnumerable<string> Subscribe([EnumeratorCancellation] CancellationToken ct, Guid? pageId = null)
    {
        var page = Channel.CreateBounded<bool>(1);
        pages.TryAdd(page, 0);
        try
        {
            // 先订阅再请求快照，首次连接与断线重连走同一条路径。
            var closed = pageId is Guid id ? PageClosed(id, resumeAfterRestart:true) : CancellationToken.None;
            while (!closed.IsCancellationRequested)
            {
                yield return "state";
                await page.Reader.ReadAsync(ct);
            }
            yield return "replaced";
        }
        finally { pages.TryRemove(page, out _); }
    }
}
