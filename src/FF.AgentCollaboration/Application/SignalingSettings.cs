using System.Security.Cryptography;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace FF.AgentCollaboration.Application;

internal sealed record SignalingSettingsInput(string? Origin, string? Fingerprint, string? SigningKey, RtcIceServer?[]? IceServers = null);
internal sealed record RtcIceServer(string? Url, string? Username, string? Credential);

/// <summary>读取和加密保存工作台信令配置。签发密钥只供服务端使用。</summary>
internal sealed class SignalingSettings(WorkbenchStartup startup, IDataProtectionProvider protection)
{
    internal sealed record Configuration(string? Origin, string? Fingerprint, string? SigningKey, RtcIceServer[]? IceServers = null)
    {
        internal bool IsConfigured => !string.IsNullOrWhiteSpace(Origin) && !string.IsNullOrWhiteSpace(Fingerprint);
        internal RtcIceServer[] Servers => IceServers ?? [];
        internal SignalingTrust? Trust() => SignalingTrust.Parse(Origin, Fingerprint);
    }

    internal sealed record State(Configuration Values, bool ReadError = false);

    private readonly IDataProtector protector = protection.CreateProtector("FF.AgentCollaboration.Signaling.Settings.v1");
    private readonly Lock gate = new();
    private const string StorageKey = "rtc_signaling_settings";

    private State Read(WorkbenchStore db)
    {
        var encrypted = db.FedSetting(StorageKey);
        if (encrypted == null) return new(new(null, null, null));
        try { return new(JsonSerializer.Deserialize<Configuration>(protector.Unprotect(encrypted))!); }
        catch (CryptographicException) { return new(new(null, null, null), ReadError: true); }
    }

    internal State Read()
    {
        using var db = new WorkbenchStore(startup.Root);
        return Read(db);
    }

    internal State Save(SignalingSettingsInput input)
    {
        var trust = SignalingTrust.Parse(input.Origin?.Trim(), input.Fingerprint?.Trim());
        string? key = input.SigningKey?.Trim();
        if (key is { Length: > 512 } || key?.Any(ch => ch < '!' || ch > '~') == true)
            throw new WorkbenchError("签发密钥须使用不含空格的可见 ASCII 字符，最多 512 字符");
        if (trust == null && !string.IsNullOrEmpty(key)) throw new WorkbenchError("请先填写信令服务器地址与公钥指纹");

        lock (gate)
        {
            using var db = new WorkbenchStore(startup.Root);
            var current = Read(db).Values;
            string? origin = trust?.Origin.GetLeftPart(UriPartial.Authority);
            if (trust != null && key == null && string.Equals(current.Origin?.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Fingerprint, trust.Fingerprint, StringComparison.OrdinalIgnoreCase))
                key = current.SigningKey;
            var servers = input.IceServers?.Select(entry => ValidateIceServer(entry, current.Servers)).ToArray() ?? current.Servers;
            if (servers.GroupBy(entry => entry.Url).Any(group => group.Count() > 1))
                throw new WorkbenchError("STUN/TURN 地址不能重复");
            var saved = new Configuration(origin, trust?.Fingerprint, string.IsNullOrEmpty(key) ? null : key, servers);
            db.SetFedSetting(StorageKey, protector.Protect(JsonSerializer.Serialize(saved)));
            return new(saved);
        }
    }

    private static RtcIceServer ValidateIceServer(RtcIceServer? entry, RtcIceServer[] previous)
    {
        string url = entry?.Url?.Trim() ?? "";
        int separator = url.IndexOf(':');
        string scheme = separator > 0 ? url[..separator].ToLowerInvariant() : "";
        bool turn = scheme is "turn" or "turns";
        if (url.Length > 512 || scheme is not ("stun" or "stuns" or "turn" or "turns") ||
            url.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)) || url.Contains('\\') ||
            !Uri.TryCreate("https://" + url[(separator + 1)..], UriKind.Absolute, out var parsed) ||
            parsed.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) || parsed.Port == 0 ||
            parsed.UserInfo.Length != 0 || parsed.AbsolutePath != "/" || parsed.Fragment.Length != 0 ||
            url[(separator + 1)..].Contains('/') || url.Split('?', 2)[0].EndsWith(':') ||
            (parsed.Query.Length != 0 && (!turn || parsed.Query is not ("?transport=udp" or "?transport=tcp"))))
            throw new WorkbenchError("请填写有效的 STUN/TURN 地址，例如 turn:relay.example.com:3478?transport=tcp");

        url = scheme + url[separator..];
        if (!turn) return new(url, null, null);
        string? username = entry?.Username?.Trim();
        string? password = entry?.Credential ?? previous.FirstOrDefault(server => server.Url == url && server.Username == username)?.Credential;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || username.Length > 512 || password.Length > 512 ||
            username.Any(char.IsControl) || password.Any(char.IsControl))
            throw new WorkbenchError("请为 TURN 服务器填写有效的用户名和密码。修改地址或用户名后请重新填写密码");
        return new(url, username, password);
    }
}
