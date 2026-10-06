param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
./build.ps1
$stage = Join-Path $PSScriptRoot "dist/package-$Version-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path "$stage/app" -Force | Out-Null
# Use the publish manifest so local settings, sessions, diagnostics and temp files cannot enter a release.
$manifest = Join-Path $PSScriptRoot 'obj/Release/net10.0-windows/win-x64/PublishOutputs.*.txt'
$files = Get-ChildItem $manifest
if (!$files) { throw 'Publish manifest not found.' }
$publish = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist/widget')) + [IO.Path]::DirectorySeparatorChar
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
Copy-Item install.ps1,Install.cmd,README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $stage
Copy-Item -Recurse docs -Destination $stage
$zip = Join-Path $PSScriptRoot "dist/HowsMyUsage-$Version-win-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $([IO.Path]::GetFileName($zip))"
Write-Host "Release: $zip"
