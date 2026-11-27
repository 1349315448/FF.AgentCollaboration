using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace FF.AgentCollaboration.Application;

// 环回提供本机工作台；选定网卡仅提供固定证书指纹的联邦 HTTPS。
internal sealed class FederationNetwork(WorkbenchStartup startup) : IDisposable
{
    private X509Certificate2? certificate;
    private readonly object certificateLock = new();
    private readonly object settingsLock = new();
    private volatile Uri? publicEndpoint;
    private bool localOnly;
    internal IPAddress? ActiveIp { get; private set; }
    internal int Port { get; set; }
    internal static bool PrivateIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }
    internal static IPAddress[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => n.GetIPProperties()).OrderByDescending(p => p.GatewayAddresses.Count > 0)
        .SelectMany(p => p.UnicastAddresses).Select(a => a.Address).Where(PeerAddress).Distinct().ToArray();

    internal static bool PeerAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] is > 0 and < 224 && b[0] != 127 && !(b[0] == 169 && b[1] == 254);
    }

    private static bool PeerHost(Uri uri) => uri.HostNameType switch
    {
        UriHostNameType.IPv4 => PeerAddress(IPAddress.Parse(uri.Host)),
        UriHostNameType.Dns => !uri.IsLoopback && !uri.IdnHost.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static Uri? PublicOrigin(string? value, int workbenchPort)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (value.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
                throw new WorkbenchError("请填写公网 IPv4、域名或 HTTPS 地址");
            value = "https://" + (value.Contains(':') ? value : $"{value}:{workbenchPort}");
        }
        if (value.Length > 512 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port < 1 || !PeerHost(uri) || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || value.Contains('\\'))
            throw new WorkbenchError("云端地址须为 HTTPS 域名或 IPv4，可带端口");
        return new UriBuilder(Uri.UriSchemeHttps, uri.IdnHost, uri.Port).Uri;
    }

    private static IPAddress? ListenerAddress(IPAddress[] addresses, string? selected, Uri? advertised)
    {
        if (!string.IsNullOrEmpty(selected)) return addresses.FirstOrDefault(a => a.ToString() == selected);
        // 直绑公网 IP 的多网卡主机优先匹配该网卡。端口映射的公网 IP 不在本机，沿用内网网卡。
        if (advertised != null && IPAddress.TryParse(advertised.Host, out var direct) && addresses.Contains(direct))
            return direct;
        return addresses.FirstOrDefault(PrivateIp) ?? (advertised != null ? addresses.FirstOrDefault() : null);
    }

    internal void SelectListener(int requested, bool loopbackOnly)
    {
        Port = requested;
        localOnly = loopbackOnly;
        if (loopbackOnly) return;
        using var store = startup.Ready.IsCompletedSuccessfully ? new WorkbenchStore(startup.Root) : null;
        var addresses = Addresses();
        var node = store?.FederationNode();
        publicEndpoint = PublicOrigin(node?.PublicEndpoint, requested);
        ActiveIp = ListenerAddress(addresses, node?.SelectedIp, publicEndpoint);
        if (requested == 0 && ActiveIp != null)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }
    internal X509Certificate2? Certificate()
    {
        if (!startup.Ready.IsCompletedSuccessfully) return null;
        lock (certificateLock)
        {
            if (certificate != null) return certificate;
            using var store = new WorkbenchStore(startup.Root);
            string? pfx = store.FedSetting("federation_tls");
            if (pfx == null)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=FF Collaboration", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
                using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
                pfx = Convert.ToBase64String(created.Export(X509ContentType.Pfx));
                store.SetFedSetting("federation_tls", pfx);
            }
            return certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pfx), null);
        }
    }

    internal object Describe(WorkbenchStore store)
    {
        var node = store.FederationNode();
        var addresses = Addresses();
        return new { node.NodeId, node.NodeName, node.SelectedIp, node.PublicEndpoint, activeIp = ActiveIp?.ToString(),
            addresses = addresses.Select(a => a.ToString()), restartRequired = !localOnly && !Equals(ActiveIp, ListenerAddress(addresses, node.SelectedIp, publicEndpoint)) };
    }
    internal object Configure(WorkbenchStore store, NodeSettings input)
    {
        if (!string.IsNullOrEmpty(input.SelectedIp) && !Addresses().Any(ip => ip.ToString() == input.SelectedIp))
            throw new WorkbenchError("请选择本机网卡地址");
        var endpoint = PublicOrigin(input.PublicEndpoint, Port);
        lock (settingsLock)
        {
            store.SetFederationNode(FederationPolicy.Name(input.NodeName), input.SelectedIp,
                input.PublicEndpoint == null ? null : endpoint?.GetLeftPart(UriPartial.Authority) ?? "");
            if (input.PublicEndpoint != null) publicEndpoint = endpoint;
            return Describe(store);
        }
    }

    internal bool AdmitsPeer(IPAddress source, HostString host)
    {
        if (PrivateIp(source) && host.Host == ActiveIp?.ToString() && (host.Port ?? 443) == Port) return true;
        var endpoint = publicEndpoint;
        return endpoint != null && (host.Port ?? 443) == endpoint.Port &&
            string.Equals(host.Host, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase);
    }
    internal object ShareLink(WorkbenchStore store, string id, bool restarted = false)
    {
        var window = store.Window(id);
        var share = store.Share(id);
        var node = store.FederationNode();
        string? link = null;
        if (share != null && ActiveIp != null && node.NodeName != null)
        {
            string pin = Certificate()!.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
            var endpoint = publicEndpoint;
            link = $"ffcp://{endpoint?.IdnHost ?? ActiveIp.ToString()}:{endpoint?.Port ?? Port}/share/{share.Value.Token}?node={node.NodeId}&window={window.Id}&pin={pin}&agent={Uri.EscapeDataString(window.Name)}";
        }
        return new { enabled = share != null, permissionMode = share?.Permission ?? "read_only", dedicatedReadOnly=store.DedicatedReader(id), link, restarted };
    }
    internal static string AddRemote(WorkbenchStore store, RemoteApplication input)
    {
        if (input.Link?.Length > 1500 || !Uri.TryCreate(input.Link, UriKind.Absolute, out var uri) || uri.Scheme != "ffcp" ||
            uri.Port == 0 || !PeerHost(uri) ||
            uri.UserInfo != "" || uri.Fragment != "" || !uri.AbsolutePath.StartsWith("/share/", StringComparison.Ordinal))
            throw new WorkbenchError("请粘贴有效的 Agent 分享链接");
        string share = uri.AbsolutePath[7..];
        var query = QueryHelpers.ParseQuery(uri.Query);
        string pin = query["pin"].ToString();
        if (!FederationPolicy.IsSecret(share) || !FederationPolicy.IsSecret(pin)) throw new WorkbenchError("分享凭据或证书指纹无效");
        if (store.FederationNode().NodeName == null) throw new WorkbenchError("请先设置本端节点名称");
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, uri.IdnHost, uri.Port == -1 ? 443 : uri.Port).Uri;
        return store.AddRemote(WorkbenchInput.Id(input.TopicId), endpoint.GetLeftPart(UriPartial.Authority), pin, WorkbenchInput.Id(query["node"]),
            WorkbenchInput.Id(query["window"]), WorkbenchInput.AgentName(query["agent"]), share, WorkbenchInput.Text(input.Purpose, "申请用途", 300),
            input.ContactSelected ? new ContactBinding(WorkbenchInput.Id(input.TopicId),input.AgentId) : null);
    }
    public void Dispose() => certificate?.Dispose();
}
