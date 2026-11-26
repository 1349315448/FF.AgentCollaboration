# Run with PowerShell on .NET 10 and an isolated Release build of the workbench.
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::ChangeExtension((Resolve-Path $Executable).Path, '.dll'))
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
$engines = $assembly.GetType('FF.AgentCollaboration.Engines.Engine', $true)
$windowType = $assembly.GetType('FF.AgentCollaboration.Domain.AgentWindow', $true)
$baseDir = [IO.Path]::GetFullPath([IO.Path]::Combine([IO.Path]::GetTempPath(), 'codex-task'))
$caseDir = [IO.Path]::Combine($baseDir, 'ff-submit-receipt-' + [guid]::NewGuid().ToString('N'))
$project = [IO.Directory]::CreateDirectory([IO.Path]::Combine($caseDir, 'native--project')).FullName
$previousCodex = $env:CODEX_HOME
$previousBuddy = $env:CODEBUDDY_CONFIG_DIR
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-Receipt($receipt, $expected, $case) {
    $actual = $receipt.Check($true).ToString()
    if ($actual -ne $expected) { throw "$case expected $expected, observed $actual" }
}

try {
    foreach ($engine in 'codex', 'codebuddy') {
        $session = [guid]::CreateVersion7().ToString()
        $window = [Activator]::CreateInstance($windowType, $flags, $null,
            [object[]]@('window','agent',$engine,$project,$null,$session,$null,$null,$true,$true,'topic'), $null)
        $env:CODEX_HOME = Join-Path $caseDir 'codex'
        $env:CODEBUDDY_CONFIG_DIR = Join-Path $caseDir 'codebuddy'
        $folder = if ($engine -eq 'codex') {
            Join-Path $env:CODEX_HOME ('sessions/' + (Get-Date -Format 'yyyy/MM/dd'))
        } else {
            # The native directory is allowed to have an older spelling, including a Windows 8.3 path prefix.
            Join-Path $env:CODEBUDDY_CONFIG_DIR 'projects/legacy-folder-spelling'
        }
        $null = [IO.Directory]::CreateDirectory($folder)
        $file = Join-Path $folder $(if ($engine -eq 'codex') {"rollout-test-$session.jsonl"} else {"$session.jsonl"})
        $id = 'aa12bc348f984009a5d40788ba6f1cb7'
        $envelope = 'MSG_ID: aa12bc34 | FROM_AGENT: QA | MSG: 核对 中文 末尾'
        function Record($role, $text, $sid = $session) {
            $msg = @{type='message'; role=$role; content=@(@{type='input_text';text=$text})}
            if ($engine -eq 'codex') { $row = @{type='response_item';payload=$msg} }
            else { $row = $msg; $row.sessionId=$sid }
            return ($row | ConvertTo-Json -Depth 8 -Compress) + "`n"
        }
        function Header($sid) {
            if ($engine -eq 'codex') { return (@{type='session_meta';payload=@{id=$sid}} | ConvertTo-Json -Compress) + "`n" }
            return (@{type='session-meta';sessionId=$sid} | ConvertTo-Json -Compress) + "`n"
        }
        $header = Header $session
        $valid = Record 'user' $envelope
        [IO.File]::WriteAllText($file, $header + $valid, $utf8)
        $adapter = $engines.GetMethod('Create', $flags).Invoke($null, @($engine, $project))
        try {
            $source = $adapter.GetType().GetMethod('SubmissionReceipts', $flags).Invoke($adapter, @($window))
            $receipt = $source.Begin($id, $envelope)
            Assert-Receipt $receipt Awaiting 'historical record predates send'
            [IO.File]::AppendAllText($file, (Record assistant $envelope) + (Record user ($envelope+'!')) +
                (Record user ($envelope.Replace(' 中文 ', '中文'))) + (Record user ('quoted '+$envelope)), $utf8)
            if ($engine -eq 'codebuddy') {
                [IO.File]::AppendAllText($file, (Record user $envelope ([guid]::NewGuid().ToString())), $utf8)
            }
            Assert-Receipt $receipt Awaiting 'role, session, exact body and spaces'
            [IO.File]::AppendAllText($file, $valid.TrimEnd(), $utf8)
            Assert-Receipt $receipt Awaiting 'incomplete final JSONL line'
            [IO.File]::AppendAllText($file, "`n", $utf8)
            Assert-Receipt $receipt Confirmed 'completed native user message'
            $rotated = $source.Begin($id, $envelope)
            [IO.File]::Move($file, $file+'.old')
            [IO.File]::WriteAllText($file, $header + $valid, $utf8)
            Assert-Receipt $rotated Confirmed 'replacement at the same pathname'
            if ($engine -eq 'codex') {
                $wrongFile = $source.Begin($id, $envelope)
                [IO.File]::Move($file, $file+'.old2')
                [IO.File]::WriteAllText($file, (Header ([guid]::NewGuid().ToString()))+$valid, $utf8)
                Assert-Receipt $wrongFile Awaiting 'foreign rollout header'
            }
            Write-Output "PASS $engine native receipt, exact matching, partial write and rotation"
        } finally { $adapter.Dispose() }
    }
} finally {
    $env:CODEX_HOME = $previousCodex
    $env:CODEBUDDY_CONFIG_DIR = $previousBuddy
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($caseDir)) -eq $baseDir -and
        [IO.Path]::GetFileName($caseDir) -match '^ff-submit-receipt-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $caseDir -Recurse -Force
    }
}
