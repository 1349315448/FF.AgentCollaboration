using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FF.AgentCollaboration.Signaling;

/// <summary>保存在部署目录中的 TLS 身份。续期签发新证书，并保留两端已信任的公钥。</summary>
internal sealed class SignalCertificate(string path, IPAddress address) : IDisposable
{
    private X509Certificate2 current = LoadOrRenew(path, address);
    private X509Certificate2? previous;

    internal X509Certificate2 Current => Volatile.Read(ref current);
    internal string Pin => Convert.ToHexStringLower(SHA256.HashData(Current.PublicKey.ExportSubjectPublicKeyInfo()));

    internal async Task Maintain(ILogger logger, CancellationToken stopping)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            while (await timer.WaitForNextTickAsync(stopping))
            {
                if (!NeedsRenewal(Current, address, DateTimeOffset.UtcNow)) continue;
                try
                {
                    var next = Renew(path, address, Current, DateTimeOffset.UtcNow);
                    previous?.Dispose();
                    previous = Interlocked.Exchange(ref current, next);
                    logger.LogInformation("Signaling TLS certificate renewed; the public key is unchanged.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    logger.LogError("Signaling TLS certificate renewal failed: {ErrorType}", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static X509Certificate2 LoadOrRenew(string path, IPAddress address)
    {
        var now = DateTimeOffset.UtcNow;
        if (!File.Exists(path)) return Renew(path, address, null, now);
        var stored = X509CertificateLoader.LoadPkcs12FromFile(path, null, KeyStorage);
        if (!NeedsRenewal(stored, address, now)) return stored;
        using (stored) return Renew(path, address, stored, now);
    }

    private static bool NeedsRenewal(X509Certificate2 certificate, IPAddress address, DateTimeOffset now) =>
        certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime.AddDays(30) ||
        certificate.NotBefore.ToUniversalTime() > now.UtcDateTime ||
        !certificate.MatchesHostname(address.ToString(), allowWildcards: false, allowCommonName: false);

    private static X509Certificate2 Renew(string path, IPAddress address, X509Certificate2? old, DateTimeOffset now)
    {
        using RSA key = old == null ? RSA.Create(3072) :
            old.GetRSAPrivateKey() ?? throw new CryptographicException("The stored signaling certificate has no RSA private key.");
        var request = new CertificateRequest($"CN={address}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(90));
        byte[] blob = generated.Export(X509ContentType.Pkcs12);
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(staged, options))
            {
                file.Write(blob);
                file.Flush(flushToDisk: true);
            }
            File.Move(staged, path, overwrite: old != null);
            return X509CertificateLoader.LoadPkcs12(blob, null, KeyStorage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            File.Delete(staged);
        }
    }

    // Windows Schannel needs a key container; other platforms can keep the imported key in memory.
    private static X509KeyStorageFlags KeyStorage => X509KeyStorageFlags.Exportable |
        (OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

    public void Dispose()
    {
        current.Dispose();
        previous?.Dispose();
    }
}
