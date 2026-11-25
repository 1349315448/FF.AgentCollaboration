using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FF.AgentCollaboration.Application;

// 通道只存“状态已变”，消息正文和认领状态始终由 SQLite 管理。
internal sealed class WorkbenchEvents
{
    private readonly Channel<bool> delivery = Channel.CreateBounded<bool>(1);
    private readonly ConcurrentDictionary<Channel<bool>, byte> pages = new();
    internal int? NotifyPort { get; set; }

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

    internal async IAsyncEnumerable<string> Subscribe([EnumeratorCancellation] CancellationToken ct)
    {
        var page = Channel.CreateBounded<bool>(1);
        pages.TryAdd(page, 0);
        try
        {
            // 先订阅再请求快照，首次连接与断线重连走同一条路径。
            yield return "state";
            await foreach (var _ in page.Reader.ReadAllAsync(ct)) yield return "state";
        }
        finally { pages.TryRemove(page, out _); }
    }
}
