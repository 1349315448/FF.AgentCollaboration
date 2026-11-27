using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;

namespace FF.AgentCollaboration.Application;

// 每个外部胶囊只建立一条向外的 SSE。游标和事件在同一事务提交，中断后从已落盘的位置恢复。
internal sealed class FederationClient(WorkbenchStartup startup, WorkbenchEvents events) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private WorkbenchStore Store() => new(startup.Root);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var workers = new Dictionary<string, Task>();
        DateTime nextCleanup = DateTime.MinValue;
        try
        {
            await startup.Ready.WaitAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                foreach (var id in workers.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
                {
                    await workers[id];
                    workers.Remove(id);
                }
                using (var db = Store())
                {
                    foreach (string id in db.ActiveRemotes())
                        if (!workers.ContainsKey(id)) workers[id] = Follow(id, ct);
                    if (DateTime.UtcNow >= nextCleanup)
                    {
                        nextCleanup = DateTime.UtcNow.AddMinutes(5);
                        try { db.CleanPeerFiles(); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkbenchError)
                        { Console.Error.WriteLine("联邦文件清理暂缓：" + error.GetType().Name); }
                    }
                    foreach (var d in db.Dispatches())
                    {
                        if (!db.ClaimDispatch(d.Id)) continue;
                        try
                        {
                            await Send(d.Remote, new(d.Id, d.Text), ct, d);
                            db.FinishDispatch(d, "accepted");
                        }
                        catch (FederationException e)
                        {
                            bool transient = e.Status >= 500 && e.Status != 507 || e.Message is "rate_limited" or "upload_rate_limited" or "node_not_ready";
                            bool refused = e.Message is "rate_limited" or "upload_rate_limited" or "node_not_ready" or "lan_only";
                            db.FinishDispatch(d, refused ? "unaccepted" : transient ? "retry" : e.Message);
                        }
                        catch (Exception error) when (error is JsonException or KeyNotFoundException or WorkbenchError or FormatException or InvalidOperationException)
                        { db.FinishDispatch(d, "invalid_peer_response"); }
                        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
                        { db.FinishDispatch(d, "retry"); }
                        events.RefreshPages();
                    }
                    foreach (var (id, item) in db.Downloads()) await DownloadReply(db, id, item, ct);
                }
                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(workers.Values); }
    }

    private static HttpClient Connect(RemoteConnection remote)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectCallback = ConnectPeer };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null &&
            string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), remote.Pin, StringComparison.OrdinalIgnoreCase);
        return new HttpClient(handler) { BaseAddress = new Uri(remote.Endpoint + "/api/federation/"), Timeout = TimeSpan.FromSeconds(8) };
    }

    private static async ValueTask<Stream> ConnectPeer(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        // 仅连接本次解析后实际通过筛选的地址，避免校验后再次解析域名。
        var resolved = await Dns.GetHostAddressesAsync(endpoint.Host, AddressFamily.InterNetwork, ct);
        var addresses = resolved.Where(FederationNetwork.PeerAddress).ToArray();
        if (addresses.Length == 0) throw new HttpRequestException("对端没有可连接的 IPv4 地址");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    private HttpRequestMessage Request(HttpMethod method, string route, string credential, object? body = null)
    {
        using var db = Store();
        var node = db.FederationNode();
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("X-FF-Node-Id", node.NodeId);
        request.Headers.Add("X-FF-Node-Name", Uri.EscapeDataString(FederationPolicy.Name(node.NodeName)));
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        // 对端也属于输入边界；不把远端任意错误 HTML / 文本带回本机控制台。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        ct = deadline.Token;
        await response.Content.LoadIntoBufferAsync(65536, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            string code = "peer_unavailable";
            try
            {
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (payload.TryGetProperty("error", out var field) && field.GetString() is { } s && s.Length <= 60 && s.All(c => char.IsAsciiLetter(c) || c == '_')) code = s;
                if (code == "quota_exceeded") throw new FederationException(status, code, new
                {
                    error = code,
                    quotaMode = payload.GetProperty("quotaMode").GetString(),
                    used = payload.GetProperty("used").GetInt64(),
                    limit = payload.GetProperty("limit").GetInt64(),
                    resetAt = payload.GetProperty("resetAt").Clone()
                });
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { }
            throw new FederationException(status, code);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("缺少对端响应");
    }

    internal async Task<object> Send(string id, PeerMessage input, CancellationToken ct, PeerDispatch? dispatch = null)
    {
        using var db = Store();
        var remote = db.Remote(id);
        if (remote?.Status != "approved" || remote.Token == null) throw new FederationException(403, "peer_not_approved");
        var message = new PeerMessage(WorkbenchInput.Id(input.Id), WorkbenchInput.Text(input.Text, "消息", 8000),ExpectsReply:dispatch?.ExpectsReply??input.ExpectsReply);
        string? sender = dispatch?.Window ?? db.ManualContactAgent(id,input.TopicId);
        if (sender != null) message = message with
        {
            SenderKey = WorkbenchStore.ScopedContactId(id,sender),
            ContactKey = dispatch == null ? null : db.SenderContactKey(id,sender)
        };
        if (dispatch == null) db.TrackManual(id, message with { TopicId = input.TopicId });
        using var client = Connect(remote);
        if (dispatch?.Content != null)
        {
            using var upload = Request(HttpMethod.Post, "uploads", remote.Token);
            upload.Headers.Add("X-FF-Upload-Key", dispatch.Id);
            upload.Headers.Add("X-FF-File-Name", Uri.EscapeDataString(dispatch.FileName!));
            upload.Content = new ByteArrayContent(dispatch.Content);
            using var uploaded = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, ct);
            var metadata = await Read<JsonElement>(uploaded, ct);
            message = message with { UploadId = WorkbenchInput.Id(metadata.GetProperty("uploadId").GetString()) };
        }
        using var request = Request(HttpMethod.Post, "messages", remote.Token, message);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var receipt = await Read<JsonElement>(response, ct);
        if (dispatch == null) db.ManualSent(id, message);
        return receipt;
    }

    private async Task DownloadReply(WorkbenchStore db, string id, PeerEvent item, CancellationToken ct)
    {
        try
        {
            var remote = db.Remote(id);
            if (remote?.Status != "approved" || remote.Token == null) throw new FederationException(403, "grant_closed");
            using var client = Connect(remote);
            using var download = Request(HttpMethod.Get, "documents/" + item.DocumentId, remote.Token);
            using var file = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!file.IsSuccessStatusCode) await Read<JsonElement>(file, ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await using var content = await file.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await WorkbenchStore.PeerBytes(content, deadline.Token);
            var upload = db.SavePeerFile(id, remote.Provider, item.DocumentId!, item.FileName!, bytes, () =>
            {
                if (db.Remote(id)?.Status != "approved") throw new FederationException(403, "grant_closed");
            });
            db.FinishDownload(id, item, upload.UploadId);
        }
        catch (FederationException e) when (e.Status is 400 or 403 or 404 or 409 or 410 or 413 or 415 or 507)
        {
            db.FinishDownload(id, item with { Text = item.Text + " [回信文档未接收：" + e.Message + "]" }, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException or FederationException or WorkbenchError or UnauthorizedAccessException)
        { db.RetryDownload(id, item.Id); }
        events.Changed();
    }

    private async Task Follow(string id, CancellationToken ct)
    {
        int backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            RemoteConnection? remote;
            using (var db = Store()) remote = db.Remote(id,includeWithdrawal:true);
            if (remote == null || remote.Status is not ("applying" or "pending" or "approved")) return;
            try
            {
                using var client = Connect(remote);
                if (remote.Status == "approved" && remote.Token != null)
                {
                    if (remote.Detached)
                    {
                        using var withdraw = Request(HttpMethod.Put,"contacts",remote.Token,Array.Empty<AdvertisedContact>());
                        using var withdrawn = await client.SendAsync(withdraw,ct);
                        if (withdrawn.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone))
                            withdrawn.EnsureSuccessStatusCode();
                        using var store = Store();
                        store.ContactsPublished(id,false);
                        return;
                    }
                    using var req = Request(HttpMethod.Get, "events", remote.Token);
                    req.Headers.Add("Last-Event-ID", remote.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    req.Headers.Add("X-FF-Return-Contacts","1");
                    req.Headers.Add("X-FF-Contact-Removal","1");
                    using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode) await Read<JsonElement>(response, ct);
                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new InvalidDataException();
                    bool supportsContacts = response.Headers.Contains("X-FF-Return-Contacts");
                    List<AdvertisedContact>? published = null;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    await foreach (string payload in DataFrames(stream, ct))
                    {
                        using var db = Store();
                        if (db.Remote(id)?.Endpoint != remote.Endpoint) return;
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.TryGetProperty("state", out var state))
                        {
                            ApplySession(db, remote, state.Deserialize<PeerSession>(Json)!, true);
                            if (supportsContacts)
                            {
                                var contacts = db.AdvertisedContacts(id);
                                if (published == null || !published.SequenceEqual(contacts))
                                {
                                    // 服务端可能已保存而响应丢失，移除最后一个胶囊时仍须撤销该地址簿。
                                    if (contacts.Count > 0) db.ContactsPublished(id,true);
                                    using var update = Request(HttpMethod.Put,"contacts",remote.Token,contacts);
                                    using var synced = await client.SendAsync(update,ct);
                                    synced.EnsureSuccessStatusCode();
                                    if (contacts.Count == 0) db.ContactsPublished(id,false);
                                    published = contacts;
                                }
                            }
                        }
                        else if (doc.RootElement.TryGetProperty("item", out var field))
                        {
                            var item = field.Deserialize<PeerEvent>(Json);
                            if (item == null || item.Id <= 0 || item.Kind is not ("receipt" or "reply" or "contact" or "contact-removed") || item.Text is not { Length: <= 8000 } ||
                                !Guid.TryParseExact(item.RequestId, "N", out _)) throw new InvalidDataException();
                            db.ReceivePeerEvent(id, item);
                            events.Changed();
                        }
                        backoff = 1;
                    }
                    throw new IOException();
                }
                bool apply = remote.Status == "applying";
                using var call = apply ? Request(HttpMethod.Post, "applications", remote.Share!, new PeerApplication(remote.Claim, remote.Purpose)) :
                    Request(HttpMethod.Get, "application", remote.Claim);
                using var reply = await client.SendAsync(call, HttpCompletionOption.ResponseHeadersRead, ct);
                var session = await Read<PeerSession>(reply, ct);
                using (var db = Store()) ApplySession(db, remote, session, false);
                backoff = 1;
                if (session.Status == "approved") continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or FederationException or
                JsonException or InvalidDataException or WorkbenchError or FormatException or InvalidOperationException)
            {
                string? stopped = e is FederationException { Status: 403 or 404 or 409, Message: not "lan_only" } ? remote.Status == "approved" ? "revoked" : "rejected" : null;
                using var db = Store();
                if (db.RemoteFailure(id, e is FederationException f ? f.Message : "对端连接中断", stopped)) events.RefreshPages();
                if (stopped != null) return;
                backoff = Math.Min(backoff * 2, 30);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private void ApplySession(WorkbenchStore db, RemoteConnection remote, PeerSession peer, bool stream)
    {
        if (peer == null || peer.NodeId != remote.Provider || peer.WindowId != remote.WindowId || peer.Status is not ("pending" or "approved" or "revoked" or "rejected") ||
            stream && (peer.Status != "approved" || peer.Token != null) ||
            !stream && peer.Status == "approved" && !FederationPolicy.IsSecret(peer.Token)) throw new InvalidDataException();
        if (peer.Status is "revoked" or "rejected")
        {
            if (db.RemoteFailure(remote.Id, peer.Status, peer.Status)) events.RefreshPages();
            return;
        }
        FederationPolicy.Name(peer.NodeName);
        WorkbenchInput.AgentName(peer.AgentName);
        if (db.RemoteStatus(remote.Id, peer, stream)) events.RefreshPages();
    }

    // 仅在空行结束后消费完整事件；限制行长度与心跳停顿，避免失联胶囊一直显示在线。
    private static async IAsyncEnumerable<string> DataFrames(Stream source, [EnumeratorCancellation] CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        byte[] buffer = new byte[8192];
        using var line = new MemoryStream();
        string? data = null;
        while (true)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            int size = await source.ReadAsync(buffer, deadline.Token);
            if (size == 0) yield break;
            for (int start = 0; start < size;)
            {
                int end = Array.IndexOf(buffer, (byte)10, start, size - start);
                int count = (end < 0 ? size : end) - start;
                if (line.Length + count > 65536) throw new InvalidDataException();
                line.Write(buffer, start, count);
                if (end < 0) break;
                string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                line.SetLength(0);
                if (text.Length == 0)
                {
                    if (data != null) yield return data;
                    data = null;
                }
                else if (text.StartsWith("data: ", StringComparison.Ordinal))
                {
                    if (data != null) throw new InvalidDataException();
                    data = text[6..];
                }
                start = end + 1;
            }
        }
    }
}
