using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Application;

/// <summary>以部署时带外交付的公钥指纹信任信令服务，普通证书续期不改变信任关系。</summary>
internal sealed class SignalingTrust
{
    private readonly byte[] expected;
    internal Uri Origin { get; }
    internal string Fingerprint => Convert.ToHexString(expected);
    internal string Identity => Origin.AbsoluteUri + "|" + Fingerprint;

    private SignalingTrust(Uri origin, byte[] expected)
    {
        Origin = origin;
        this.expected = expected;
    }

    internal static SignalingTrust? Parse(string? endpoint, string? pin)
    {
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(pin)) return null;
        if (endpoint?.Length > 512 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            !IPAddress.TryParse(origin.IdnHost.Trim('[', ']'), out _) || origin.Port == 0 || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new WorkbenchError("信令服务器地址须为 HTTPS IP 地址，可带端口");
        const string consoleLabel = "FF_SIGNALING_SPKI_SHA256";
        string? fingerprint = pin?.Trim();
        if (fingerprint?.StartsWith(consoleLabel, StringComparison.OrdinalIgnoreCase) == true)
            fingerprint = fingerprint[consoleLabel.Length..];
        if (fingerprint != null) fingerprint = string.Concat(fingerprint.Where(ch => !char.IsWhiteSpace(ch)));
        if (fingerprint?.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            throw new WorkbenchError("请填写信令服务器输出的 64 位十六进制公钥指纹");
        return new SignalingTrust(origin, Convert.FromHexString(fingerprint));
    }

    internal async Task<bool> Check(CancellationToken ct)
    {
        using var client = CreateClient();
        try
        {
            using var response = await client.GetAsync("status", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
        { return false; }
    }

    internal HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        UseCookies = false,
        UseProxy = false,
        AllowAutoRedirect = false,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate }
    })
    {
        BaseAddress = new Uri(Origin, "/api/signaling/v1/"), Timeout = TimeSpan.FromSeconds(8)
    };

    internal async Task<ClientWebSocket> Open(string room, string ticket, CancellationToken ct, bool relay = false)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = Validate;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + ticket);
        var uri = new UriBuilder(Origin) { Scheme = "wss", Port = Origin.Port,
            Path = "/api/signaling/v1/rooms/" + room + (relay ? "/relay" : "/connect") }.Uri;
        try
        {
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate == null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var leaf = new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        return leaf.NotBefore.ToUniversalTime() <= now && now < leaf.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(leaf.PublicKey.ExportSubjectPublicKeyInfo()));
    }
}
