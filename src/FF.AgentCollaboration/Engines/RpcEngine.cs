using System.Text.Json.Nodes;

namespace FF.AgentCollaboration.Engines;

internal abstract class RpcEngine : Engine
{
    private RpcPipe? connection;
    protected RpcPipe Rpc => connection ?? throw new InvalidOperationException("引擎还未连接");
    protected void Connect(RpcPipe pipe)
    {
        connection?.Dispose();
        connection = pipe;
    }

    protected static readonly object Client = new { name = "lx-agent-collaboration", version = "0.2" };
    internal abstract Task<NativeSession> Open(string? existingId, string? model, EngineMcp mcp, string instructions,
        string sandbox, string approvalPolicy, CancellationToken ct);
    internal abstract Task Turn(string session, string eventId, string prompt, Action<string?> started, Action<string> delta,
        Action? toolCompleted, CancellationToken ct);

    // 两个原生 RPC 协议都使用 nextCursor，但请求、响应字段和分页大小各自定义。
    protected static async IAsyncEnumerable<JsonNode> Items(Func<string?, Task<JsonNode>> page, string items)
    {
        string? cursor = null;
        do
        {
            var result = await page(cursor);
            foreach (var item in result[items]!.AsArray()) yield return item!;
            cursor = (string?)result["nextCursor"];
        }
        while (!string.IsNullOrEmpty(cursor));
    }

    protected abstract bool ProbeWriter(string id);
    internal override bool HasNativeWriter(string id)
    {
        if (id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
        try { return ProbeWriter(id); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    protected static string[] Efforts(JsonNode? items, string? property = null) =>
        items is JsonArray array
            ? array.Select(item => property == null ? (string?)item : (string?)item?[property])
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
            : [];

    internal void Abort() => Rpc.Abort();
    public override void Dispose() => Interlocked.Exchange(ref connection, null)?.Dispose();
}

internal sealed class EngineTurnFailed(string message) : Exception(message);
