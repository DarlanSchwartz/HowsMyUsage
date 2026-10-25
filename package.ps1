param([string]$Version = '1.1.0')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
./build.ps1
$stage = Join-Path $PSScriptRoot "dist/package-$Version-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path "$stage/app" -Force | Out-Null
try {
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
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md -Destination "$stage/app"
New-Item -ItemType Directory -Force "$stage/app/docs" | Out-Null
Copy-Item -Recurse docs/licenses -Destination "$stage/app/docs"
Copy-Item installer/Uninstall.ps1 -Destination "$stage/app"
$installedFiles = @((Get-ChildItem "$stage/app" -File -Recurse).FullName | ForEach-Object { $_.Substring((Join-Path $stage 'app').Length + 1) }) + 'install-files.json'
ConvertTo-Json -InputObject $installedFiles | Set-Content -LiteralPath "$stage/app/install-files.json" -Encoding UTF8
Copy-Item install.ps1,Install.cmd,README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $stage
Copy-Item -Recurse docs -Destination $stage
$zip = Join-Path $PSScriptRoot "dist/HowsMyUsage-$Version-win-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $([IO.Path]::GetFileName($zip))"
Write-Host "Release: $zip"
$setup = Join-Path $PSScriptRoot "dist/HowsMyUsage-$Version-Setup.exe"
& ./installer/build.ps1 -Payload "$stage/app" -Output $setup -Version $Version
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  $([IO.Path]::GetFileName($setup))"
Write-Host "Installer: $setup"
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $allowed = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedStage.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
