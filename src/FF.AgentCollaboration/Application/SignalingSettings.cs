using System.Security.Cryptography;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace FF.AgentCollaboration.Application;

internal sealed record SignalingSettingsInput(string? Origin, string? Fingerprint, string? SigningKey);

/// <summary>读取和加密保存工作台信令配置。签发密钥只供服务端使用。</summary>
internal sealed class SignalingSettings(WorkbenchStartup startup, IDataProtectionProvider protection)
{
    internal sealed record Configuration(string? Origin, string? Fingerprint, string? SigningKey)
    {
        internal bool IsConfigured => !string.IsNullOrWhiteSpace(Origin) && !string.IsNullOrWhiteSpace(Fingerprint);
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
            var saved = new Configuration(origin, trust?.Fingerprint, string.IsNullOrEmpty(key) ? null : key);
            db.SetFedSetting(StorageKey, protector.Protect(JsonSerializer.Serialize(saved)));
            return new(saved);
        }
    }
}
