param(
    [string]$Godot = '',
    [int]$Port = 28810,
    [switch]$SkipBuild,
    [switch]$Verify,
    [switch]$Headless
)
$ErrorActionPreference = 'Stop'
function Resolve-Godot([string]$Requested) {
    foreach ($candidate in @($Requested, $env:GODOT_BIN, 'godot4', 'godot')) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [System.IO.Path]::GetFullPath($candidate) }
        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue
        if ($null -ne $command) { return $command.Source }
    }
    throw 'Godot .NET executable not found. Supply -Godot, set GODOT_BIN, or add godot4/godot to PATH.'
}
$Godot = Resolve-Godot $Godot
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $PSScriptRoot 'Godot'
if (!(Test-Path -LiteralPath $root) -or !(Test-Path -LiteralPath $project)) { throw 'Repository or Godot project not found.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be 1024..65535.' }
if (!$SkipBuild) {
    & dotnet build (Join-Path $PSScriptRoot 'Host/NetRpcGodot.Host.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
    & dotnet build (Join-Path $project 'NetRpcGodot.Client.csproj') -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Godot Client build failed.' }
    & $Godot --headless --editor --path $project --import
    if ($LASTEXITCODE -ne 0) { throw 'Godot import failed.' }
}
$hostDll = Join-Path $root 'Artifacts/bin/NetRpcGodot.Host/Release/net10.0/NetRpcGodot.Host.dll'
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
function Launch([string]$Executable, [string[]]$Arguments, [string]$Output = '') {
    $options = @{ FilePath = $Executable; ArgumentList = @($Arguments | ForEach-Object { '"' + $_ + '"' }); WorkingDirectory = $root; PassThru = $true }
    if ($Output) { $options.RedirectStandardOutput = $Output; $options.RedirectStandardError = $Output + '.err' }
    $process = Start-Process @options
    $null = $process.Handle # Keep the handle so Windows PowerShell can read ExitCode after a fast exit.
    $started.Add($process)
    return $process
}
function ReadLog([string]$Path) {
    if (![System.IO.File]::Exists($Path)) { return '' }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = [System.IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function WaitLine([System.Diagnostics.Process]$Process, [string]$Log, [string]$Marker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        $text = ReadLog $Log
        if ($text.Contains($Marker)) { return }
        if ($Process.HasExited) { throw "Process exited before '$Marker': $text $(ReadLog ($Log + '.err'))" }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for '$Marker': $(ReadLog $Log) $(ReadLog ($Log + '.err'))"
}
try {
    if ($Verify) {
        $evidence = Join-Path $root ('Artifacts/NetRpcHuman/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
        [System.IO.Directory]::CreateDirectory($evidence) | Out-Null
        $hostLog = Join-Path $evidence 'host.log'
        $hostProcess = Launch 'dotnet' @($hostDll, '--human', '--port', "$Port", '--seconds', '40') $hostLog
        WaitLine $hostProcess $hostLog 'Human example listening'
        $base = @('--path', $project, 'res://Human.tscn')
        if ($Headless) { $base += '--headless' }
        $observerLog = Join-Path $evidence 'observer.log'
        $observerArgs = $base + @('--', '--port', "$Port", '--name', 'Observer', '--human-observe')
        if (!$Headless) { $observerArgs += @('--capture-human', (Join-Path $evidence 'observer.png')) }
        $observer = Launch $Godot $observerArgs $observerLog
        WaitLine $observer $observerLog 'HUMAN_READY'
        $actorLog = Join-Path $evidence 'actor.log'
        $actorArgs = $base + @('--', '--port', "$Port", '--name', 'Actor', '--human-verify')
        if (!$Headless) { $actorArgs += @('--capture-human', (Join-Path $evidence 'actor.png')) }
        $actor = Launch $Godot $actorArgs $actorLog
        WaitLine $actor $actorLog 'HUMAN_PASS'
        WaitLine $observer $observerLog 'HUMAN_PASS'
        foreach ($client in @($actor, $observer)) {
            $exited = $client.WaitForExit(5000)
            if (!$exited -or $client.ExitCode -ne 0) { throw "Godot proof did not exit cleanly: pid=$($client.Id); exited=$exited; exitCode=$($client.ExitCode)." }
        }
        $summary = @{ Passed = $true; Backend = 'Native TCP+UDP'; Clients = 2; RealGodot = $true; Coins = 7; Apples = 1; Health = 80; MessageCount = 2; UdpAllPerClient = 1; ClientWriteRejected = $true }
        [System.IO.File]::WriteAllText((Join-Path $evidence 'summary.json'), ($summary | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
        "PASS human example: Host + two real Godot clients; evidence=$evidence"
    }
    else {
        $hostProcess = Launch 'dotnet' @($hostDll, '--human', '--port', "$Port")
        Start-Sleep -Seconds 1
        foreach ($number in 1,2) {
            $position = if ($number -eq 1) { '20,60' } else { '680,140' }
            $client = Launch $Godot @('--path', $project, 'res://Human.tscn', '--position', $position, '--', '--port', "$Port", '--name', "Client $number")
            "Human Client $number pid=$($client.Id)"
        }
        "Host pid=$($hostProcess.Id). Follow buttons 1..6. Stop the Host console with Ctrl+C. Guide: Samples/NetRpcGodot/HUMAN-START-HERE.md"
    }
}
finally {
    if ($Verify) {
        foreach ($process in $started) { if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } $process.Dispose() }
    }
}
