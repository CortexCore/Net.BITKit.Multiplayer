param([string]$Target = 'D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer.LiteNetLib', [string]$Branch = 'feature/litenetlib-direct')
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Target))
if (!(Test-Path -LiteralPath $root) -or !(Test-Path -LiteralPath $parent)) { throw 'Repository/target parent does not exist.' }
if (Test-Path -LiteralPath $Target) { throw 'Refusing to overwrite an existing worktree.' }
$allowed = @('CodeGen','Docs','Projects','RemoteCompiler','Samples','Src','Tests','Tools','.gitbook.yaml','.gitignore','AGENTS.md','Directory.Build.props','Net.BITKit.Multiplayer.slnx','README.md','Start-Arena-Latency.cmd','Start-Arena-Relay.cmd','Start-Arena.cmd','Start-Godot-Sync-Lab.cmd')
$files = @(& git -C $root ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'Cannot enumerate the source baseline.' }
foreach ($file in $files) {
    if (!$allowed.Contains(($file -split '/')[0]) -or $file -match '(^|/)(\.git|\.godot|Artifacts|bin|obj)(/|$)' -or $file -match '(^|/)\.\.') { throw "Unexpected baseline path: $file" }
}
$baseline = Join-Path $root ('Artifacts/AgentBaselines/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
if (!(Test-Path -LiteralPath (Join-Path $root 'Artifacts'))) { throw 'Artifacts parent missing.' }
[System.IO.Directory]::CreateDirectory($baseline) | Out-Null
& git -C $root worktree add --orphan -b $Branch $Target
if ($LASTEXITCODE -ne 0) { throw 'Cannot create orphan worktree.' }
$records = @()
foreach ($file in $files) {
    $source = Join-Path $root $file
    $dest = Join-Path $Target $file
    $frozen = Join-Path $baseline ('sources/' + $file)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($dest)) | Out-Null
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($frozen)) | Out-Null
    $before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    [System.IO.File]::Copy($source, $frozen, $false)
    [System.IO.File]::Copy($frozen, $dest, $false)
    $after = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
    $sourceAfter = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ($before -ne $after -or $before -ne $sourceAfter) { throw "Concurrent baseline change or copy mismatch: $file" }
    $records += [pscustomobject]@{ Path=$file; SHA256=$after }
}
$manifest = [pscustomobject]@{ Source=$root; Worktree=$Target; Branch=$Branch; Kind='Verified uncommitted source snapshot; no commit created'; CreatedUtc=[DateTime]::UtcNow.ToString('O'); Files=$records }
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText((Join-Path $baseline 'manifest.json'), $json, [System.Text.UTF8Encoding]::new($false))
"VERIFIED files=$($records.Count) worktree=$Target baseline=$baseline"
