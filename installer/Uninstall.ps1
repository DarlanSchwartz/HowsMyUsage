$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$exe = Join-Path $root 'Usage.exe'
if (Get-Process Usage -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe) { throw 'Exit HowsMyUsage from the tray before uninstalling.' }
$files = Get-Content -LiteralPath (Join-Path $root 'install-files.json') -Raw | ConvertFrom-Json
foreach ($relative in $files) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!$target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid uninstall manifest.' }
    for ($ancestor = $target; $ancestor; $ancestor = Split-Path -Path $ancestor -Parent) {
        $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Uninstall refuses junctions and symbolic links.' }
    }
}
$key = 'HKCU:\Software\HowsMyUsage'
if ((Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallDir -eq $root) {
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $command = (Get-ItemProperty -Path $run -ErrorAction SilentlyContinue).UsageWidget
    if ($command -and $command.Contains((Join-Path $root 'startup.ps1'))) { Remove-ItemProperty -Path $run -Name UsageWidget }
    Remove-Item -LiteralPath $key -Force
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HowsMyUsage' -Force -ErrorAction SilentlyContinue
}
foreach ($relative in $files) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
}
# Preserve settings, encrypted sessions and other user-created files.
Write-Host 'HowsMyUsage uninstalled. Your settings remain in the installation folder.'
