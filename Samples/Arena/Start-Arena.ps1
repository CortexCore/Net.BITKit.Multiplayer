param(
    [switch]$NoBuild,
    [switch]$DedicatedHost,
    [ValidateSet('Auto', 'Direct', 'Relay')][string]$Route = 'Auto',
    [ValidateSet('native', 'touchsocket')][string]$Transport = 'native',
    [ValidateRange(1024, 65535)][int]$RelayPort = 17892,
    [switch]$NoInterpolation,
    [ValidateRange(0, 500)][int]$InterpolationMs = 100,
    [ValidateRange(0, 1000)][int]$PoseDelayMs = 0,
    [ValidateRange(0, 500)][int]$PoseJitterMs = 0,
    [ValidateRange(1, 3)][int]$Clients = 1,
    [ValidateRange(1024, 65535)][int]$LobbyPort = 17890,
    [ValidateRange(1024, 65535)][int]$GamePort = 17891
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath (Join-Path $root 'Net.BITKit.Multiplayer.slnx'))) { throw 'Arena repository root not found.' }
if ($LobbyPort -eq $GamePort) { throw 'Lobby and game ports must differ.' }
$useRelay = $Route -eq 'Relay' -or ($Route -eq 'Auto' -and -not $DedicatedHost)
if ($useRelay -and ($RelayPort -eq $LobbyPort -or $RelayPort -eq $GamePort)) { throw 'Relay port must be distinct from Lobby and game port.' }
if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'Net.BITKit.Multiplayer.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$app = Join-Path $root 'Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll'
$lobby = Join-Path $root 'Artifacts/bin/Arena.Lobby/Release/net10.0/Arena.Lobby.dll'
$relay = Join-Path $root 'Artifacts/bin/Arena.Relay/Release/net10.0/Arena.Relay.dll'
foreach ($binary in @($app, $lobby)) { if (-not (Test-Path -LiteralPath $binary)) { throw "Build output missing: $binary" } }
if ($useRelay -and -not (Test-Path -LiteralPath $relay)) { throw 'Build Arena.Relay before launching a Relay room.' }
$run = Join-Path $root ('Artifacts/ArenaRuns/manual-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
[void][IO.Directory]::CreateDirectory($run)
$owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-Node([string]$Name, [string]$Binary, [string[]]$NodeArgs) {
    $quoted = (@($Binary) + $NodeArgs | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $quoted -WorkingDirectory $root -PassThru `
        -RedirectStandardOutput (Join-Path $run ($Name + '.log')) `
        -RedirectStandardError (Join-Path $run ($Name + '.error.log'))
    $owned.Add($process)
    return $process
}
function Wait-Ready([string]$Name, [System.Diagnostics.Process]$Process) {
    $path = Join-Path $run ($Name + '.ready.json')
    $end = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $path)) {
        if ($Process.HasExited) { throw "$Name exited before ready. See $run" }
        if ([DateTime]::UtcNow -gt $end) { throw "$Name did not become ready. See $run" }
        Start-Sleep -Milliseconds 100
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

try {
    $lobbyProcess = Start-Node 'lobby' $lobby @('--port', "$LobbyPort", '--ready-file', (Join-Path $run 'lobby.ready.json'))
    $null = Wait-Ready 'lobby' $lobbyProcess
    if ($useRelay) {
        $relayProcess = Start-Node 'relay' $relay @('--port', "$RelayPort", '--lobby-port', "$LobbyPort",
            '--transport', $Transport,
            '--ready-file', (Join-Path $run 'relay.ready.json'), '--report', (Join-Path $run 'relay.json'))
        $null = Wait-Ready 'relay' $relayProcess
    }
    $effectiveRoute = if ($useRelay) { 'relay' } else { 'direct' }
    $hostArgs = @('--role', 'host', '--name', 'Host', '--lobby-port', "$LobbyPort", '--game-port', "$GamePort",
        '--transport', $Transport,
        '--route', $effectiveRoute, '--relay-port', "$RelayPort",
        '--room-name', 'BITKit Arena', '--report', (Join-Path $run 'host.json'), '--ready-file', (Join-Path $run 'host.ready.json'),
        '--interpolation-ms', "$InterpolationMs",
        '--window-x', '30', '--window-y', '50')
    if ($NoInterpolation) { $hostArgs += '--no-interpolation' }
    if ($DedicatedHost) { $hostArgs += @('--headless', '--no-local-player') }
    $hostProcess = Start-Node 'host' $app $hostArgs
    $hostReady = Wait-Ready 'host' $hostProcess
    for ($i = 1; $i -le $Clients; $i++) {
        $name = 'client' + $i
        $x = if ($DedicatedHost -and $i -eq 1) { 30 } elseif ($i -eq 1 -or ($DedicatedHost -and $i -eq 2)) { 1270 } else { 630 }
        $y = if ($i -gt 1 -and -not $DedicatedHost) { 580 } else { 50 }
        $view = if ($i -eq 1) { '3d' } else { '2d' }
        $clientArgs = @('--role', 'client', '--name', ('Player ' + $i), '--lobby-port', "$LobbyPort",
            '--transport', $Transport,
            '--room', $hostReady.RoomId, '--view', $view, '--report', (Join-Path $run ($name + '.json')),
            '--interpolation-ms', "$InterpolationMs", '--pose-delay-ms', "$PoseDelayMs", '--pose-jitter-ms', "$PoseJitterMs",
            '--ready-file', (Join-Path $run ($name + '.ready.json')), '--window-x', "$x", '--window-y', "$y")
        if ($NoInterpolation) { $clientArgs += '--no-interpolation' }
        $process = Start-Node $name $app $clientArgs
        $null = Wait-Ready $name $process
    }
    Write-Host "Arena running. WASD move, mouse aim, left button fire, F2 view, F3 interpolation, F4 UDP, F5 client rebind."
    Write-Host "Actual room route: $effectiveRoute (Clients follow the Lobby descriptor automatically)."
    Write-Host "Packet transport: $Transport (reliable control/session traffic remains TouchSocket)."
    Write-Host 'Motion uses authenticated UDP; reliable RPC/Health remain on TCP. GC samples are included in node reports.'
    Write-Host "Client pose-display delay=$PoseDelayMs ms, jitter=+/-$PoseJitterMs ms (not full-network RTT)."
    Write-Host "Logs and reports: $run"
    $null = Read-Host 'Press Enter here to stop all sample processes'
}
finally {
    $cleanup = $owned.ToArray()
    [array]::Reverse($cleanup)
    foreach ($process in $cleanup) {
        if (-not $process.HasExited) { [void]$process.CloseMainWindow() }
        if (-not $process.HasExited -and -not $process.WaitForExit(2000)) { $process.Kill() }
        $process.Dispose()
    }
}
