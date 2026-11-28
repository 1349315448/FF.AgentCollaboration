using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal const int PeerFileLimit = 1024 * 1024;
    internal const int PeerBinaryLimit = 20 * 1024 * 1024;
    private static readonly HashSet<string> PeerExtensions = new(
        [".md", ".txt", ".xls", ".xlsx", ".ppt", ".pptx", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".zip"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private string PeerDirectory => Path.Combine(root, "federation-inbox");

    internal static async Task<byte[]> PeerBytes(Stream stream, CancellationToken ct)
        => await PeerAttachmentBytes(stream, "handoff.md", ct);

    internal static int PeerAttachmentLimit(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? PeerFileLimit : PeerBinaryLimit;

    internal static async Task<byte[]> PeerAttachmentBytes(Stream stream, string name, CancellationToken ct)
    {
        int limit = PeerAttachmentLimit(name);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new FederationException(413, "file_too_large");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        ValidatePeerBytes(name, bytes);
        return bytes;
    }

    private static void ValidatePeerBytes(string name, byte[] bytes)
    {
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return;
        if (bytes.Contains((byte)0)) throw new FederationException(415, "invalid_markdown");
        try { StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw new FederationException(415, "invalid_markdown"); }
    }

    internal static string SafePeerName(string value)
    {
        string ext = Path.GetExtension(value);
        if (value.Length > 255 || !PeerExtensions.Contains(ext)) throw new FederationException(415, "file_type_not_allowed");
        string leaf = value.Replace('\\', '/').Split('/')[^1][..^ext.Length];
        string stem = Regex.Replace(leaf, @"[^\p{L}\p{Nd}_-]", "_");
        if (stem.Length > 48) stem = stem[..48];
        if (stem.Length == 0 || Regex.IsMatch(stem, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) stem = "document";
        return stem + ext.ToLowerInvariant();
    }

    private string PeerPath(string relative)
    {
        string path = Path.GetFullPath(relative, PeerDirectory);
        if (!path.StartsWith(PeerDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new FederationException(403, "invalid_file");
        TopicArchive.CheckPath(path);
        return path;
    }

    internal string UploadOwner(string token, string node)
    {
        var grant = Grant(AuthorizePeer(token, node));
        if (grant.Paused || !grant.AllowUpload) throw new FederationException(403, "upload_disabled");
        return grant.Id;
    }

    internal PeerUpload SavePeerFile(string owner, string node, string retry, string fileName, byte[] bytes, Action authorize)
    {
        string name = SafePeerName(fileName), key = WorkbenchInput.Id(retry), id = Guid.NewGuid().ToString("N");
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        var now = DateTimeOffset.UtcNow;
        string relative = Path.Combine(WorkbenchInput.Id(node), now.ToString("yyyy-MM-dd"),
            $"{now:yyyyMMdd-HHmmss}-{id[..8]}-{name}");
        // 先持久化预留空间。即使在写盘途中退出，未完成文件仍有关联记录，可安全重试或回收。
        Atomic(() =>
        {
            authorize();
            var prior = Read("SELECT id,relative,digest,name FROM fed_file WHERE owner=$p0 AND retry=$p1",
                r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), owner, key).SingleOrDefault();
            if (prior.Item1 != null)
            {
                if (prior.Item3 != digest || prior.Item4 != name) throw new FederationException(409, "upload_id_conflict");
                id = prior.Item1; relative = prior.Item2; return;
            }
            long seconds = now.ToUnixTimeSeconds();
            var count = Read("SELECT count(*),count(CASE WHEN created>$p1 THEN 1 END) FROM fed_upload_rate WHERE node=$p0 AND created>$p2",
                r => (r.GetInt64(0), r.GetInt64(1)), node, seconds - 60, seconds - 3600).Single();
            if (count.Item1 >= 100 || count.Item2 >= 20) throw new FederationException(429, "upload_rate_limited");
            var usage = Read("SELECT coalesce(sum(size),0),coalesce(sum(CASE WHEN node=$p0 THEN size END),0) FROM fed_file",
                r => (r.GetInt64(0), r.GetInt64(1)), node).Single();
            if (usage.Item1 + bytes.Length > 500L * PeerFileLimit || usage.Item2 + bytes.Length > 50L * PeerFileLimit)
                throw new FederationException(507, "storage_quota_exceeded");
            Exec("INSERT INTO fed_file(id,owner,node,retry,name,relative,size,digest,created) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)",
                id, owner, node, key, name, relative, bytes.Length, digest, seconds);
            Exec("INSERT INTO fed_upload_rate VALUES($p0,$p1)", node, seconds);
        });
        Atomic(() =>
        {
            authorize();
            if (!Read("SELECT 1 FROM fed_file WHERE id=$p0", r => true, id).Any()) throw new FederationException(410, "file_expired");
            string path = PeerPath(relative), folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            using var guard = WorkspaceDirectoryGuard.Open(folder);
            if (File.Exists(path)) return;
            string partial = path + ".part";
            File.Delete(partial);
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
            File.Move(partial, path);
        });
        return new PeerUpload(id, "federation-inbox/" + relative.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private string BindPeerFile(string owner, string id, string message)
    {
        id = WorkbenchInput.Id(id);
        string relative = Read("""
            SELECT relative FROM fed_file WHERE owner=$p0 AND id=$p1 AND
            (bound=$p2 OR (bound IS NULL AND created>unixepoch()-1800))
            """, r => r.GetString(0), owner, WorkbenchInput.Id(id), message).SingleOrDefault() ?? throw new FederationException(409, "invalid_upload");
        string path = PeerPath(relative);
        if (!File.Exists(path)) throw new FederationException(410, "file_expired");
        if (Exec("UPDATE fed_file SET bound=$p1 WHERE id=$p0 AND owner=$p2", id, message, owner) != 1)
            throw new FederationException(409, "invalid_upload");
        return path;
    }

    internal (string Name, byte[] Bytes) PeerReplyFile(string token, string node, string id)
    {
        string owner = AuthorizePeer(token, node);
        var file = Read("""
            SELECT f.name,f.relative FROM fed_file f JOIN fed_reply_doc d ON d.file_id=f.id
            WHERE d.grant_id=$p0 AND f.owner=$p0 AND f.id=$p1
            """, r => (r.GetString(0), r.GetString(1)), owner, WorkbenchInput.Id(id)).SingleOrDefault();
        if (file.Item1 == null) throw new FederationException(404, "file_expired");
        return (file.Item1, ReadPeerBytes(PeerPath(file.Item2)));
    }

    private static byte[] ReadPeerBytes(string path)
    {
        using var guard = WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(path)!);
        using var file = guard.Read(path);
        return PeerBytes(file, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal object PeerStorage() => Read("SELECT count(*),coalesce(sum(size),0) FROM fed_file",
        r => new { files = r.GetInt64(0), bytes = r.GetInt64(1) }).Single();

    internal void CleanPeerFiles()
    {
        const string expiredCondition = "(created<=unixepoch()-2592000 OR (bound IS NULL AND created<=unixepoch()-1800))";
        var expired = Read("SELECT id,relative FROM fed_file WHERE " + expiredCondition,
            r => (r.GetString(0), r.GetString(1)));
        foreach (var (id, relative) in expired)
        {
            try
            {
                Atomic(() =>
                {
                    if (!Read("SELECT 1 FROM fed_file WHERE id=$p0 AND " + expiredCondition, r => true, id).Any()) return;
                    string path = PeerPath(relative), folder = Path.GetDirectoryName(path)!;
                    if (Directory.Exists(folder))
                    {
                        using var guard = WorkspaceDirectoryGuard.Open(folder);
                        File.Delete(path); File.Delete(path + ".part");
                    }
                    Exec("DELETE FROM fed_file WHERE id=$p0", id);
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkbenchError)
            { /* 单个文件占用时保留其计费记录，在下一轮重试。 */ }
        }
        Exec("DELETE FROM fed_upload_rate WHERE created<=unixepoch()-3600");
        Exec("UPDATE fed_outbox SET content=NULL WHERE created<=unixepoch()-2592000 AND state NOT IN ('pending','sending') AND content IS NOT NULL");
        Exec("""
            UPDATE fed_outbox SET state='rejected' WHERE state='awaiting_user'
            AND (created<=unixepoch()-2592000 OR NOT EXISTS(SELECT 1 FROM windows w WHERE w.id=fed_outbox.window_id AND w.closed=0))
            """);
        Exec("""
            DELETE FROM fed_send_file WHERE dispatch IN (
                SELECT id FROM fed_outbox WHERE state NOT IN ('pending','sending','awaiting_user')
            )
            """);
        Exec("DELETE FROM fed_attachment WHERE file NOT IN (SELECT id FROM fed_file)");
        Exec("DELETE FROM fed_group_upload WHERE file NOT IN (SELECT id FROM fed_file)");
    }
}
