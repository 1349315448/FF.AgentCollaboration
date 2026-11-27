param([Parameter(Mandatory)][string]$Dll, [string]$WorkbenchDll)

$ErrorActionPreference = 'Stop'
$dllPath = (Resolve-Path -LiteralPath $Dll).Path
$base = Join-Path ([IO.Path]::GetTempPath()) 'codex-task'
$root = Join-Path $base ('ff-signal-tls-' + [Guid]::NewGuid().ToString('N'))
$configPath = Join-Path $root 'appsettings.json'
$certPath = Join-Path $root 'signaling.pfx'
$process = $null

if (!('SignalTlsProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class SignalTlsProbe {
    public static string Pin(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static string Connect(string host, int port, string pin) {
        using var client = new TcpClient(host, port);
        using var stream = new SslStream(client.GetStream(), false, (_, cert, _, errors) => {
            if (cert == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var leaf = new X509Certificate2(cert);
            return leaf.NotAfter > DateTime.Now && leaf.NotBefore <= DateTime.Now &&
                string.Equals(pin, Pin(leaf), StringComparison.Ordinal);
        });
        stream.AuthenticateAsClient(host);
        return stream.RemoteCertificate.GetCertHashString();
    }
}
'@
}

function WorkbenchCheck([uri]$SignalingUri, [string]$Pin, [bool]$Connected) {
    $location = Join-Path $root ('workbench-' + [Guid]::NewGuid().ToString('N'))
    $hostStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @((Resolve-Path -LiteralPath $WorkbenchDll).Path, 'workbench', $location, '--port', '0',
        '--loopback-only', '--fixed-data-dir', '--handoffs', (Join-Path $location 'handoffs'))) {
        $hostStart.ArgumentList.Add($argument)
    }
    $hostStart.WorkingDirectory = $root
    $hostStart.Environment['LOCALAPPDATA'] = Join-Path $root 'profile'
    $hostStart.UseShellExecute = $false
    $hostStart.RedirectStandardOutput = $true
    $workbench = [Diagnostics.Process]::Start($hostStart)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(20))
    try {
        do {
            $marker = $workbench.StandardOutput.ReadLineAsync().WaitAsync($timeout.Token).GetAwaiter().GetResult()
            if ($null -eq $marker) { throw 'Fixture workbench exited before startup' }
        } until ($marker.StartsWith('LX_WORKBENCH '))
        $url = $marker.Substring('LX_WORKBENCH '.Length)
        $setup = Invoke-RestMethod ($url + '/api/initialize') -TimeoutSec 8
        if (!$setup.initialized) {
            $setup.nodeName = 'Signaling fixture'
            [void](Invoke-RestMethod ($url + '/api/initialize') -Method Post -ContentType 'application/json' -Body ($setup | ConvertTo-Json -Depth 8) -TimeoutSec 8)
        }
        $settings = @{ origin = $SignalingUri.GetLeftPart([UriPartial]::Authority); fingerprint = $Pin }
        [void](Invoke-RestMethod ($url + '/api/peers/signaling/settings') -Method Put -ContentType 'application/json' -Body ($settings | ConvertTo-Json) -TimeoutSec 8)
        $status = Invoke-RestMethod ($url + '/api/peers/signaling') -TimeoutSec 10
        if ($status.connected -ne $Connected) { throw "Expected connected=$Connected, got $($status.connected)" }
    } finally {
        $timeout.Dispose()
        if (!$workbench.HasExited) { $workbench.Kill($true); $workbench.WaitForExit() }
        $workbench.Dispose()
    }
}

try {
    [void][IO.Directory]::CreateDirectory($root)
    @{
        Urls = 'https://127.0.0.1:0'
        Signaling = @{ PublicOrigin = 'https://127.0.0.1:5190'; CertificatePath = 'signaling.pfx' }
        InternalService = @{ Key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM

    # An about-to-expire certificate exercises renewal immediately without changing the host clock.
    $key = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=127.0.0.1', $key, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $names.AddIpAddress([Net.IPAddress]::Loopback)
        $request.CertificateExtensions.Add($names.Build())
        $old = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $pin = [SignalTlsProbe]::Pin($old)
            $oldThumbprint = $old.Thumbprint
            [IO.File]::WriteAllBytes($certPath, $old.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12))
        } finally { $old.Dispose() }
    } finally { $key.Dispose() }

    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($dllPath)
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($start)
    $watch = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(20))
    try {
        do {
            $line = $process.StandardOutput.ReadLineAsync().WaitAsync($watch.Token).GetAwaiter().GetResult()
            if ($null -eq $line) { throw 'Signal host exited before startup' }
        } until ($line.StartsWith('FF_SIGNALING '))
        $address = [uri]$line.Substring('FF_SIGNALING '.Length)
    } finally { $watch.Dispose() }
    try { $newThumbprint = [SignalTlsProbe]::Connect($address.Host, $address.Port, $pin) }
    catch { throw "Pinned handshake failed: $($_.Exception.GetBaseException().Message)" }
    if ($oldThumbprint -eq $newThumbprint) { throw 'The expiring certificate was not replaced' }
    $rejectsWrongPin = $false
    try { [void][SignalTlsProbe]::Connect($address.Host, $address.Port, [string]::new('0', 64)) }
    catch [Security.Authentication.AuthenticationException], [IO.IOException] { $rejectsWrongPin = $true }
    if (!$rejectsWrongPin) { throw 'The validation probe accepted the wrong public key' }
    Write-Output 'PASS appsettings, IP SAN TLS, automatic renewal and stable public-key fingerprint'
    if ($WorkbenchDll) {
        WorkbenchCheck $address $pin $true
        WorkbenchCheck $address ([string]::new('0', 64)) $false
        Write-Output 'PASS isolated workbenches accept the configured pin and reject a mismatched pin'
    }
} finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $root -PathType Container) {
        $verified = (Resolve-Path -LiteralPath $root).Path
        $parent = (Resolve-Path -LiteralPath $base).Path
        if ([IO.Path]::GetDirectoryName($verified) -ne $parent -or
            [IO.Path]::GetFileName($verified) -ne [IO.Path]::GetFileName($root)) { throw 'Unexpected TLS fixture path' }
        Remove-Item -LiteralPath $verified -Recurse -Force
    }
}
