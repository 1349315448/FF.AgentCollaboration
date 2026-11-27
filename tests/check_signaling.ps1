param([Parameter(Mandatory)][string]$Dll)

$ErrorActionPreference = 'Stop'
$socketPath = '/api/signaling/v1/rooms'
$invitePath = '/api/signaling/v1/invitations'
$issuerKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$start.ArgumentList.Add((Resolve-Path -LiteralPath $Dll).Path)
$start.ArgumentList.Add('--urls')
$start.ArgumentList.Add('http://127.0.0.1:0')
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.Environment['InternalService__Key'] = $issuerKey
$start.Environment['Signaling__PublicOrigin'] = 'https://127.0.0.1:9443'
$process = [System.Diagnostics.Process]::Start($start)

function Assert-Equal($Actual, $Expected) {
    if ($Actual -ne $Expected) { throw "Expected [$Expected], got [$Actual]" }
}

function Request($Path, $Headers, $Body) {
    Invoke-WebRequest -Uri ($base + $Path) -Method Post -Headers $Headers -Body $Body -ContentType 'application/json' -TimeoutSec 5 -SkipHttpErrorCheck
}

function Connect($Room, $Token, $Operation = 'connect') {
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    try {
        $socket.Options.SetRequestHeader('Authorization', 'Bearer ' + $Token)
        $socket.Options.CollectHttpResponseDetails = $true
        $uri = [uri]($base.Replace('http://','ws://') + $socketPath + '/' + $Room + '/' + $Operation)
        [void]$socket.ConnectAsync($uri, $timeout.Token).GetAwaiter().GetResult()
        return $socket
    } catch {
        $statusCode = [int]$socket.HttpStatusCode
        $socket.Dispose()
        if ($statusCode -ne 0) { return $statusCode }
        throw
    }
}

function Send($Socket, $Payload) {
    $bytes = [ArraySegment[byte]]::new([Text.Encoding]::UTF8.GetBytes($Payload))
    [void]$Socket.SendAsync($bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult()
}

function Receive($Socket) {
    $buffer = [byte[]]::new(32768)
    $read = $Socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
    if (!$read.EndOfMessage -or $read.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) {
        throw "Unexpected WebSocket frame: $($read.MessageType)"
    }
    return [Text.Encoding]::UTF8.GetString($buffer, 0, $read.Count)
}

$timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
$hostSocket = $null
$guest = $null
$otherHost = $null
try {
    do {
        $line = $process.StandardOutput.ReadLineAsync().WaitAsync($timeout.Token).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Signaling host exited before advertising its port' }
    } until ($line.StartsWith('FF_SIGNALING '))
    $base = $line.Substring('FF_SIGNALING '.Length)
    $metadata = @{
        nodeId = [guid]::NewGuid().ToString('N')
        windowId = [guid]::NewGuid().ToString('N')
        pin = [string]::new('a', 64)
    } | ConvertTo-Json -Compress
    Assert-Equal (Request $invitePath @{} $metadata).StatusCode 401
    $issuer = @{ 'X-Lx-Service-Key' = $issuerKey }
    Assert-Equal (Request $invitePath $issuer '{"nodeId":"bad"}').StatusCode 400
    $created = Request $invitePath $issuer $metadata
    Assert-Equal $created.StatusCode 201
    $invitation = $created.Content | ConvertFrom-Json
    $other = (Request $invitePath $issuer $metadata).Content | ConvertFrom-Json
    $link = [uri]$invitation.link
    Assert-Equal $link.Scheme 'ffcp'
    Assert-Equal $link.Authority '127.0.0.1:9443'
    Assert-Equal $link.AbsolutePath ('/signal/' + $invitation.roomId)
    if ($link.Fragment.Length -ne 65 -or $link.ToString().Contains($invitation.hostTicket)) {
        throw 'The link must contain only the guest invitation secret'
    }
    $claim = $socketPath + '/' + $invitation.roomId + '/claim'
    Assert-Equal (Request $claim @{Authorization = 'Bearer ' + $invitation.hostTicket} '{}').StatusCode 401
    $join = @{Authorization = 'Bearer ' + $link.Fragment.Substring(1)}
    $redeemed = Request $claim $join '{}'
    Assert-Equal $redeemed.StatusCode 200
    $ticket = ($redeemed.Content | ConvertFrom-Json).ticket
    Assert-Equal (Request $claim $join '{}').StatusCode 401
    Assert-Equal (Connect $invitation.roomId $link.Fragment.Substring(1)) 401
    Assert-Equal (Connect $other.roomId $ticket) 401
    Assert-Equal (Connect $other.roomId $ticket 'relay') 401
    Assert-Equal (Connect $invitation.roomId $link.Fragment.Substring(1) 'relay') 401

    $hostSocket = Connect $invitation.roomId $invitation.hostTicket
    Assert-Equal (Connect $invitation.roomId $invitation.hostTicket) 409
    $guest = Connect $invitation.roomId $ticket
    $otherHost = Connect $other.roomId $other.hostTicket
    Assert-Equal ((Receive $hostSocket | ConvertFrom-Json).type) 'ready'
    $ready = Receive $guest | ConvertFrom-Json
    Assert-Equal $ready.type 'ready'
    if ($ready.turnPassword) {
        $relay = Connect $invitation.roomId $ticket 'relay'
        try {
            Assert-Equal $relay.State 'Open'
            Assert-Equal (Connect $invitation.roomId $ticket 'relay') 409
        } finally { $relay.Dispose() }
    }
    $offer = '{"type":"offer","sdp":"v=0\r\ns=room-offer"}'
    $answer = '{"type":"answer","sdp":"v=0\r\ns=room-answer"}'
    $candidate = '{"type":"ice","candidate":{"candidate":"candidate:1 1 UDP 42 192.0.2.1 12345 typ host","sdpMid":"0"}}'
    Send $hostSocket $offer
    Assert-Equal (Receive $guest) $offer
    Send $guest $answer
    Assert-Equal (Receive $hostSocket) $answer
    Send $guest $candidate
    Assert-Equal (Receive $hostSocket) $candidate

    [void]$guest.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '', $timeout.Token).GetAwaiter().GetResult()
    Assert-Equal ((Receive $hostSocket | ConvertFrom-Json).type) 'peer-offline'
    $guest.Dispose()
    $guest = Connect $invitation.roomId $ticket
    Assert-Equal ((Receive $hostSocket | ConvertFrom-Json).type) 'ready'
    Assert-Equal ((Receive $guest | ConvertFrom-Json).type) 'ready'
    Send $guest $offer
    $close = $guest.ReceiveAsync([ArraySegment[byte]]::new([byte[]]::new(256)), $timeout.Token).GetAwaiter().GetResult()
    Assert-Equal $close.CloseStatus ([Net.WebSockets.WebSocketCloseStatus]::PolicyViolation)
    Assert-Equal ((Receive $hostSocket | ConvertFrom-Json).type) 'peer-offline'
    Write-Output 'PASS issuer authentication, one-time invite, role and room isolation, SDP/ICE exchange and reconnect'
} finally {
    try {
        foreach ($connection in @($hostSocket, $guest, $otherHost)) {
            if ($connection -is [Net.WebSockets.ClientWebSocket]) { $connection.Dispose() }
        }
    } finally {
        $timeout.Dispose()
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
