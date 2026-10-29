param([string]$Version = '1.2.3')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
./scripts/build.ps1 -Version $Version
$stage = Join-Path $projectRoot "dist/package-$Version-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path "$stage/app" -Force | Out-Null
try {
# Use the publish manifest so local settings, sessions, diagnostics and temp files cannot enter a release.
$manifest = Join-Path $projectRoot 'src/HowsMyUsage/obj/Release/net10.0-windows/win-x64/PublishOutputs.*.txt'
$files = Get-ChildItem $manifest
if (!$files) { throw 'Publish manifest not found.' }
$publish = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist/widget')) + [IO.Path]::DirectorySeparatorChar
foreach ($path in (Get-Content $files.FullName | Sort-Object -Unique)) {
    $full = [IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($publish, [StringComparison]::OrdinalIgnoreCase)) { continue }
    if ([IO.Path]::GetExtension($full) -eq '.pdb') { continue }
    $relative = $full.Substring($publish.Length)
    $target = Join-Path "$stage/app" $relative
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath $full -Destination $target
}
if (!(Test-Path "$stage/app/Usage.exe")) { throw 'Publish manifest did not include Usage.exe.' }
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md -Destination "$stage/app"
New-Item -ItemType Directory -Force "$stage/app/docs" | Out-Null
Copy-Item -Recurse docs/licenses -Destination "$stage/app/docs"
$setup = Join-Path $projectRoot "dist/HowsMyUsage-$Version-Setup.exe"
& ./installer/build.ps1 -Payload "$stage/app" -Output $setup -Version $Version
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  $([IO.Path]::GetFileName($setup))"
Write-Host "Installer: $setup"
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedStage.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
