param([ValidateSet('LiteNetLib','GCBaseline')][string]$Work = 'LiteNetLib')
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!(Test-Path -LiteralPath $root)) { throw 'Repository root missing.' }
if ($Work -eq 'LiteNetLib') {
    throw 'LiteNetLib worktree integration is retired: use the independent Net.BITKit.Multiplayer.LiteNetLib repository. Historical sources are preserved in Net.BITKit.Multiplayer.LiteNetLib.LegacyWorktree; do not restore the old in-Core layout.'
} else {
    $source = 'D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer.GCBaseline'
    $baseline = Join-Path $root 'Artifacts/AgentBaselines/20261003-084537'
    $files = @('Tools/NetRpcPerformance/NetRpcPerformance.csproj','Tools/NetRpcPerformance/Program.cs','Tools/NetRpcPerformance/Node.cs','Tools/NetRpcPerformance/Model.cs','Tools/NetRpcPerformance/Contracts/NetRpcPerformance.Contracts.csproj','Tools/NetRpcPerformance/Contracts/Workload.cs','Tools/NetRpcPerformance.Tests/NetRpcPerformance.Tests.csproj','Tools/NetRpcPerformance.Tests/GuardTests.cs','Docs/netrpc-gc-baseline.md')
}
if (!(Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath $baseline)) { throw 'Verified source or frozen baseline missing.' }
$manifest = [System.IO.File]::ReadAllText((Join-Path $baseline 'manifest.json')) | ConvertFrom-Json
$expected = @{}; foreach ($entry in $manifest.Files) { $expected[$entry.Path] = $entry.SHA256 }
# Verify every target before any overwrite. Source scope is the reviewed explicit allowlist, never directories/caches.
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file))) { throw "Reviewed source missing: $file" }
    $target = Join-Path $root $file
    if (Test-Path -LiteralPath $target) {
        if (!$expected.ContainsKey($file) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected[$file]) { throw "Concurrent target change; refusing overwrite: $file" }
    }
}
$records = @()
foreach ($file in $files) {
    $from = Join-Path $source $file; $target = Join-Path $root $file
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
    $hash = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash
    [System.IO.File]::Copy($from, $target, $true)
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw "Integration hash mismatch: $file" }
    $records += [pscustomobject]@{Path=$file; SHA256=$hash}
}
$evidence = Join-Path $baseline ("integrated-$Work.json")
[System.IO.File]::WriteAllText($evidence, (@{Work=$Work; Source=$source; Target=$root; Files=$records; IntegratedUtc=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
"VERIFIED integrated=$($files.Count) work=$Work manifest=$evidence"
