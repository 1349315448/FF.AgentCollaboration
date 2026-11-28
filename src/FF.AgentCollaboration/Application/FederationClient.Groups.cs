using System.Net.Http.Headers;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal sealed partial class FederationClient
{
    // 请求方用原 HTTPS 或 WebRTC 通道交换双向队列，接收回执前保留消息和附件。
    private async Task FollowGroup(string id,CancellationToken ct)
    {
        string[] track=[];
        GroupReceipt? acknowledgement=null;
        GroupRoster? advertised=null;
        DateTimeOffset nextFileAttempt=DateTimeOffset.MinValue;
        int delay=3;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var db=Store();
                var connection=db.GroupConnection(id);
                if (connection==null) return;
                var remote=connection.Transport;
                using var client=Connect(remote);
                string route="groups/"+id;
                if (remote.Token==null)
                {
                    using var ask=connection.Revoking?Request(HttpMethod.Post,route+"/withdraw",remote.Claim):connection.Application!=null
                        ? Request(HttpMethod.Post,"groups",remote.Share!,connection.Application)
                        : Request(HttpMethod.Get,route+"/application",remote.Claim);
                    using var response=await client.SendAsync(ask,HttpCompletionOption.ResponseHeadersRead,ct);
                    var session=await Read<GroupSession>(response,ct,131072);
                    if (db.ReceiveGroupSession(id,session)) events.RefreshPages();
                    if (session.Status is "revoked" or "rejected") return;
                }
                else
                {
                    var push=db.PrepareGroupPush(id,track,acknowledgement);
                    GroupPacket? outbound=push.Exchange.Message;
                    if (outbound!=null && push.Files.Length>0)
                    {
                        if (DateTimeOffset.UtcNow<nextFileAttempt || !SameRoster(advertised,push.Exchange.Roster)) outbound=null;
                        else try { outbound=outbound with {Files=await UploadGroupFiles(client,route,remote.Token,outbound,push.Files,ct)}; }
                        catch(FederationException error) when(GroupFileDenied(error))
                        {
                            if (db.ReceiveGroupReceipts(id,[new(outbound.Id,GroupFileFailure(error),null)])) events.RefreshPages();
                            outbound=null;
                        }
                        catch(FederationException error) when(error.Status==429)
                        {
                            nextFileAttempt=DateTimeOffset.UtcNow.AddSeconds(10);
                            outbound=null;
                        }
                    }
                    using var exchange=Request(HttpMethod.Post,route+"/exchange",remote.Token,push.Exchange with {Message=outbound});
                    using var response=await client.SendAsync(exchange,HttpCompletionOption.ResponseHeadersRead,ct);
                    var answer=await Read<GroupExchangeResult>(response,ct,131072);
                    advertised=push.Exchange.Roster;
                    bool changed=db.ReceiveGroupSession(id,answer.Session);
                    if (answer.Session.Status!="approved") { if(changed) events.Changed(); return; }
                    if(response.Headers.Contains("X-FF-Rtc-Pairs"))
                        await ConfirmRtcPair(remote,group:true,client,ct);
                    changed|=db.ReceiveGroupReceipts(id,answer.Receipts);
                    track=answer.Track;
                    acknowledgement=null;
                    if (answer.Message is GroupPacket packet)
                    {
                        try
                        {
                            acknowledgement=db.CheckGroupPull(id,packet);
                            if (acknowledgement==null && (packet.Files.Length==0 || DateTimeOffset.UtcNow>=nextFileAttempt))
                            {
                                var received=packet with {Files=await DownloadGroupFiles(db,client,route,remote,packet,ct)};
                                acknowledgement=db.ReceiveGroupPull(id,received);
                                changed=true;
                            }
                        }
                        catch (FederationException error) when(GroupFileDenied(error))
                        { acknowledgement=new(packet.Id,GroupFileFailure(error),null); }
                        catch (FederationException error) when(error.Status==429)
                        { nextFileAttempt=DateTimeOffset.UtcNow.AddSeconds(10); }
                    }
                    if (changed) events.Changed();
                }
                delay=3;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or
                FederationException or WorkbenchError or JsonException or FormatException or InvalidOperationException)
            {
                bool permanent=error is FederationException { Status: 403 or 404 or 409, Message:
                    "invalid_share" or "invalid_claim" or "invalid_group_grant" or "group_link_conflict" or "group_link_exists" or
                    "group_reader_unavailable" or "peer_unavailable" };
                using var db=Store();
                if (db.GroupConnectFailed(id,permanent)) events.RefreshPages();
                if (permanent) return;
                delay=Math.Min(delay*2,30);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delay),ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private static bool GroupFileDenied(FederationException error) =>
        error.Status is 400 or 403 or 404 or 409 or 410 or 413 or 415 or 507;

    private static string GroupFileFailure(FederationException error) =>
        error.Message=="upload_disabled" ? "upload_disabled" : "attachment_failed";

    private static bool SameRoster(GroupRoster? previous,GroupRoster? current) =>
        previous!=null && current!=null && previous.Name==current.Name && previous.Leader==current.Leader &&
        previous.Active==current.Active && previous.Members.SequenceEqual(current.Members);

    private async Task<GroupFileRef[]> UploadGroupFiles(HttpClient client,string route,string token,GroupPacket packet,
        OutgoingAttachment[] files,CancellationToken ct)
    {
        var result=new List<GroupFileRef>();
        foreach(var file in files)
        {
            using var request=Request(HttpMethod.Post,route+"/files",token);
            request.Headers.Add("X-FF-File-Name",Uri.EscapeDataString(file.Name));
            request.Headers.Add("X-FF-Upload-Key",file.Id);
            request.Headers.Add("X-FF-Group-Message",packet.Id);
            if (packet.Sender!=null) request.Headers.Add("X-FF-Group-Sender",packet.Sender);
            if (packet.Recipient!=null) request.Headers.Add("X-FF-Group-Recipient",packet.Recipient);
            request.Content=new ByteArrayContent(file.Content);
            request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            var saved=await Read<PeerUpload>(response,ct);
            result.Add(new(WorkbenchInput.Id(saved.UploadId),file.Name));
        }
        return result.ToArray();
    }

    private async Task<GroupFileRef[]> DownloadGroupFiles(WorkbenchStore db,HttpClient client,string route,RemoteConnection remote,
        GroupPacket packet,CancellationToken ct)
    {
        if (packet.Files is not {Length:<=20}) throw new InvalidDataException();
        var approval=new GroupFileRoute(packet.Id,packet.Sender,packet.Recipient);
        var result=new List<GroupFileRef>();
        long total=0;
        foreach(var file in packet.Files)
        {
            string id=WorkbenchInput.Id(file.Id),name=WorkbenchStore.SafePeerName(file.Name);
            void Check() => db.CheckGroupPullFile(remote.Id,approval);
            Check();
            using var request=Request(HttpMethod.Get,route+"/files/"+id,remote.Token!);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if (!response.IsSuccessStatusCode) await Read<JsonElement>(response,ct);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes=await WorkbenchStore.PeerAttachmentBytes(input,name,deadline.Token);
            total+=bytes.Length;
            if (total>WorkbenchStore.ChatUploadLimit) throw new InvalidDataException();
            var saved=db.SavePeerFile(remote.Id,remote.Provider,id,name,bytes,Check);
            db.StageGroupUpload(remote.Id,approval,saved.UploadId);
            result.Add(new(saved.UploadId,name));
        }
        return result.ToArray();
    }
}
