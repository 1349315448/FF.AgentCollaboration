using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FF.AgentCollaboration.Updates;

internal sealed record UpdateAsset(string Product,string Rid,string Url,string Sha256);
internal sealed record UpdateRelease(string Version,string Notes,UpdateAsset[] Assets);
internal sealed record SignedUpdate(string Payload,string Signature);
internal sealed record UpdateRequest(string Action,string? Version,string? Mode,string[]? Tasks);
internal sealed record UpdateProgress(string Phase,string? Error=null);
internal sealed record UpdateStatus(string Current,string? Available,string? Notes,bool Managed,bool Configured,UpdateProgress Progress,UpdateResult? Last);
internal sealed class UpdateFailure(string message) : Exception(message);

// 发布清单由本机配置的公钥验证，下载内容以清单中的 SHA-256 校验。
internal sealed class ManagedUpdates(string product,string version,IConfiguration config,IHostApplicationLifetime lifetime)
{
    private const long ArchiveLimit=512L*1024*1024;
    private const long FilesLimit=2L*1024*1024*1024;
    private const int ManifestLimit=256*1024;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http=new(new SocketsHttpHandler {UseCookies=false})
    {Timeout=TimeSpan.FromMinutes(10)};
    private readonly Lock gate=new();
    private volatile UpdateProgress progress=new("idle");
    private CancellationTokenSource? active;
    private string? Home => UpdateSupervisor.InstallHome;
    private string? KeyPath => config["Updates:PublicKeyFile"] is {Length:>0} path
        ? Path.GetFullPath(path,Home??AppContext.BaseDirectory):null;
    private bool Configured => KeyPath is string path && File.Exists(path) && ManifestUri!=null;
    private Uri? ManifestUri => Https(config["Updates:ManifestUrl"] ??
        "https://github.com/1349315448/FF.AgentCollaboration/releases/latest/download/ff-acp-update.json");

    internal UpdateStatus Status => new(version,null,null,UpdateSupervisor.Root!=null,Configured,progress,
        UpdateSupervisor.Root is { } root?UpdateSupervisor.Read<UpdateResult>(root,"result.json"):null);

    internal async Task<UpdateStatus> Check(CancellationToken ct)
    {
        var status=Status;
        if(!status.Configured)return status;
        try
        {
            var (release,asset)=await Latest(ct);
            return asset!=null && Newer(release.Version) ? status with {Available=release.Version,Notes=release.Notes}:status;
        }
        catch(Exception error) when (!ct.IsCancellationRequested &&
            error is HttpRequestException or IOException or JsonException or FormatException or CryptographicException or UpdateFailure or ArgumentException or OperationCanceledException)
        { return status with {Progress=new("check_failed","无法读取或验证发布清单")}; }
    }

    internal static bool AuthorizeAdmin(IConfiguration config,string? supplied)
    {
        string? hash=config["Updates:AdminKeyHash"];
        if(hash?.Length!=64 || !hash.All(char.IsAsciiHexDigit) || supplied?.Length!=64 || !supplied.All(char.IsAsciiHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash),
            SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(supplied)));
    }

    internal string Schedule(UpdateRequest request,Func<bool> idle,Action<string,string[]> bind)
    {
        if(UpdateSupervisor.Root is not { } root) throw new UpdateFailure("请使用支持更新的启动器打开服务");
        if(request.Action is not ("apply" or "restart") || request.Mode is not (null or "idle" or "now") ||
            request.Tasks is {Length:>100}) throw new UpdateFailure("更新请求无效");
        if(request.Action=="apply" && (!Configured || !Newer(request.Version)))
            throw new UpdateFailure("没有已验证的更新版本");
        string id=Guid.NewGuid().ToString("N");
        lock(gate)
        {
            if(active!=null || File.Exists(Path.Combine(root,"pending.json")))
                throw new UpdateFailure("已有更新或重启正在执行");
            progress=new("downloading");
            var operation=active=CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _=Task.Run(()=>Apply(root,id,request,idle,bind,operation));
        }
        return id;
    }

    internal bool Cancel()
    {
        lock(gate)
        {
            if(active==null || progress.Phase=="restarting") return false;
            active.Cancel();
            return true;
        }
    }

    private async Task Apply(string root,string id,UpdateRequest request,Func<bool> idle,Action<string,string[]> bind,CancellationTokenSource operation)
    {
        var ct=operation.Token;
        string? candidate=null;
        try
        {
            if(request.Action=="apply")
            {
                var (release,asset)=await Latest(ct);
                if(asset==null || release.Version!=request.Version || !Newer(release.Version))
                    throw new UpdateFailure("可安装版本已变化，请重新检查");
                candidate=id;
                await Install(root,id,asset,ct);
            }
            if(request.Mode=="idle")
            {
                progress=new("waiting");
                while(!idle())await Task.Delay(TimeSpan.FromSeconds(1),ct);
            }
            lock(gate)
            {
                ct.ThrowIfCancellationRequested();
                bind(id,request.Tasks??[]);
                UpdateSupervisor.Write(root,"pending.json",new UpdatePlan(id,candidate,request.Action=="restart"?version:request.Version!));
                progress=new("restarting");
            }
            lifetime.StopApplication();
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { progress=new("cancelled"); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or
            FormatException or ArgumentException or InvalidOperationException or CryptographicException or UpdateFailure or OperationCanceledException)
        { progress=new("failed",error is UpdateFailure?error.Message:"更新准备失败，当前程序可继续使用"); }
        finally { lock(gate) { active=null; operation.Dispose(); } }
    }

    private async Task<(UpdateRelease Release,UpdateAsset? Asset)> Latest(CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        ct=deadline.Token;
        using var response=await Http.GetAsync(ManifestUri!,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        await using var body=await response.Content.ReadAsStreamAsync(ct);
        using var content=new MemoryStream();
        await CopyBounded(body,content,ManifestLimit,ct);
        var signed=JsonSerializer.Deserialize<SignedUpdate>(content.ToArray(),Json) ?? throw new UpdateFailure("缺少发布签名");
        var payload=Convert.FromBase64String(signed.Payload);
        using var key=ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(KeyPath!,ct));
        if(!key.VerifyData(payload,Convert.FromBase64String(signed.Signature),HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))
            throw new UpdateFailure("发布签名校验失败");
        var release=JsonSerializer.Deserialize<UpdateRelease>(payload,Json) ?? throw new UpdateFailure("发布格式无效");
        if(!System.Version.TryParse(release.Version,out _) || release.Assets==null)throw new UpdateFailure("发布版本无效");
        string rid=RuntimeInformation.RuntimeIdentifier;
        var assets=release.Assets.Where(asset=>asset.Product==product && asset.Rid==rid).ToArray();
        if(assets.Length>1)throw new UpdateFailure("发布清单包含重复架构");
        return (release,assets.SingleOrDefault());
    }

    private bool Newer(string? next) => System.Version.TryParse(next,out var candidate) &&
        System.Version.TryParse(version,out var current) && candidate>current;

    private static Uri? Https(string? url) => Uri.TryCreate(url,UriKind.Absolute,out var uri) &&
        uri.Scheme==Uri.UriSchemeHttps && uri.UserInfo.Length==0 && uri.Fragment.Length==0 ? uri:null;

    private async Task Install(string root,string id,UpdateAsset asset,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        ct=deadline.Token;
        var url=Https(asset.Url)??throw new UpdateFailure("发布包地址无效");
        var expected=Convert.FromHexString(asset.Sha256);
        if(expected.Length!=32)throw new UpdateFailure("发布包校验值无效");
        string staging=Path.Combine(root,id+".staging");
        string archive=Path.Combine(root,id+".package");
        try
        {
            using(var response=await Http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input=await response.Content.ReadAsStreamAsync(ct);
                await using var output=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                await CopyBounded(input,output,ArchiveLimit,ct);
            }
            await using(var data=File.OpenRead(archive))
            {
                byte[] actual=await SHA256.HashDataAsync(data,ct);
                if(!CryptographicOperations.FixedTimeEquals(expected,actual))
                    throw new UpdateFailure("发布包内容未通过校验");
            }
            Directory.CreateDirectory(staging);
            if(OperatingSystem.IsWindows())await UnpackZip(archive,staging,ct);
            else await UnpackTar(archive,staging,ct);
            string app=UpdateSupervisor.Executable(product);
            var folders=Directory.EnumerateDirectories(staging).Take(2).ToArray();
            string package=File.Exists(Path.Combine(staging,app))?staging:
                folders.Length==1?folders[0]:throw new UpdateFailure("更新包目录无效");
            if(!File.Exists(Path.Combine(package,app)) ||
                !File.Exists(Path.Combine(package,UpdateSupervisor.Family(product)+".runtimeconfig.json")) ||
                product=="workbench" && !File.Exists(Path.Combine(package,"wwwroot","index.html")))
                throw new UpdateFailure("更新包缺少程序或运行时");
            var destination=UpdateSupervisor.CandidatePath(root,id);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(package,destination);
        }
        finally
        {
            File.Delete(archive);
            if(Directory.Exists(staging)) Directory.Delete(staging,true);
        }
    }

    private static async Task UnpackZip(string archive,string root,CancellationToken ct)
    {
        using var zip=ZipFile.OpenRead(archive);
        long remaining=FilesLimit;
        foreach(var entry in zip.Entries)
        {
            string path=SafePath(root,entry.FullName);
            if(entry.FullName.EndsWith('/')) {Directory.CreateDirectory(path);continue;}
            if(entry.Length>remaining)throw new UpdateFailure("更新包解压后过大");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input=entry.Open();
            await using var output=new FileStream(path,FileMode.CreateNew);
            remaining-=await CopyBounded(input,output,entry.Length,ct);
        }
    }

    private static async Task UnpackTar(string archive,string root,CancellationToken ct)
    {
        await using var compressed=File.OpenRead(archive);
        await using var gzip=new GZipStream(compressed,CompressionMode.Decompress);
        using var tar=new TarReader(gzip);
        long remaining=FilesLimit;
        while(await tar.GetNextEntryAsync(copyData:false,ct) is { } entry)
        {
            string path=SafePath(root,entry.Name);
            if(entry.EntryType==TarEntryType.Directory) {Directory.CreateDirectory(path);continue;}
            if(entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length>remaining)
                throw new UpdateFailure("更新包包含不安全的文件或超出解压上限");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using(var output=new FileStream(path,FileMode.CreateNew))
                if(entry.DataStream is { } input)remaining-=await CopyBounded(input,output,entry.Length,ct);
            if(!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,entry.Mode & (UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute));
        }
    }

    private static string SafePath(string root,string relative)
    {
        if(relative.Contains('\\') || relative.Contains(':') || Path.IsPathRooted(relative) ||
            relative.Split('/').Any(part=>part is "." or ".."))
            throw new UpdateFailure("更新包包含无效路径");
        string path=Path.GetFullPath(relative,root);
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal))
            throw new UpdateFailure("更新包文件超出目标目录");
        return path;
    }

    private static async Task<long> CopyBounded(Stream input,Stream output,long max,CancellationToken ct)
    {
        var buffer=new byte[64*1024];
        long total=0;
        while(await input.ReadAsync(buffer,ct) is int count && count>0)
        {
            total+=count;
            if(total>max)throw new UpdateFailure("更新包超过大小限制");
            await output.WriteAsync(buffer.AsMemory(0,count),ct);
        }
        return total;
    }
}
