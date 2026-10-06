param(
    [string]$Godot = '',
    [switch]$Relay,
    [switch]$LiteNetLib,
    [switch]$Auto,
    [switch]$Headless,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if ($LiteNetLib -and $Relay) { throw 'LiteNetLib Relay is not implemented; DIRECT only.' }
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
if (!(Test-Path -LiteralPath $root)) { throw 'Repository root not found.' }
function Build([string]$Project, [string]$Configuration) {
    & dotnet build (Join-Path $root $Project) -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Project" }
}
if (!$SkipBuild) {
    Build 'Samples/NetRpcGodot/Host/NetRpcGodot.Host.csproj' 'Release'
    Build 'Samples/NetRpcGodot/Relay/NetRpcGodot.Relay.csproj' 'Release'
    Build 'Samples/NetRpcGodot/E2E/NetRpcGodot.E2E.csproj' 'Release'
    Build 'Samples/NetRpcGodot/Godot/NetRpcGodot.Client.csproj' 'Debug'
    & $Godot --headless --editor --path (Join-Path $PSScriptRoot 'Godot') --import
    if ($LASTEXITCODE -ne 0) { throw 'Godot import failed.' }
}
if ($Auto) {
    $arguments = @((Join-Path $root 'Artifacts/bin/NetRpcGodot.E2E/Release/net10.0/NetRpcGodot.E2E.dll'), '--godot', $Godot)
    if ($Relay) { $arguments += '--relay' }
    if ($LiteNetLib) { $arguments += '--litenetlib' }
    if (!$Headless) { $arguments += '--visible' }
    & dotnet @arguments
    exit $LASTEXITCODE
}
function Launch([string]$Executable, [string[]]$Arguments) {
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    Start-Process -FilePath $Executable -ArgumentList $quoted -WorkingDirectory $root -PassThru
}
if ($Relay) { $relayProcess = Launch 'dotnet' @((Join-Path $root 'Artifacts/bin/NetRpcGodot.Relay/Release/net10.0/NetRpcGodot.Relay.dll'), '--port', '28771') }
$hostArguments = @((Join-Path $root 'Artifacts/bin/NetRpcGodot.Host/Release/net10.0/NetRpcGodot.Host.dll'), '--port', '28770')
if ($Relay) { $hostArguments += @('--relay-port', '28771') }
if ($LiteNetLib) { $hostArguments += '--litenetlib' }
$hostProcess = Launch 'dotnet' $hostArguments
Start-Sleep -Seconds 1
$port = if ($Relay) { '28771' } else { '28770' }
foreach ($slot in 1,2) {
    $position = if ($slot -eq 1) { '20,70' } else { '650,160' }
    $clientArgs = @('--path', (Join-Path $PSScriptRoot 'Godot'), '--position', $position, '--', '--connect', '--slot', "$slot", '--port', $port)
    if ($LiteNetLib) { $clientArgs += '--litenetlib' }
    $client = Launch $Godot $clientArgs
    "Godot player $slot pid=$($client.Id)"
}
"Host pid=$($hostProcess.Id); endpoint=127.0.0.1:$port. WASD / SPACE / E / R. Close Clients and stop the Host console with Ctrl+C when finished."
