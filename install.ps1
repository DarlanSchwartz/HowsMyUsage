param(
    [string]$InstallDir,
    [switch]$DesktopShortcut,
    [switch]$StartWithWindows,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
if (!$InstallDir) { $InstallDir = Read-Host 'Install folder on a non-system drive (for example D:\Apps\HowsMyUsage)' }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if ([IO.Path]::GetPathRoot($InstallDir) -eq 'C:\') { throw 'Choose a drive other than C:.' }
$source = Join-Path $PSScriptRoot 'app'
if (!(Test-Path (Join-Path $source 'Usage.exe'))) { throw 'Extract the complete release ZIP before running the installer.' }
if ($InstallDir.TrimEnd('\') -eq $source.TrimEnd('\')) { throw 'Choose a different destination from the extracted app folder.' }
$exe = Join-Path $InstallDir 'Usage.exe'
if (Get-Process Usage -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe) { throw 'Exit HowsMyUsage from the tray before updating.' }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $InstallDir -Recurse -Force
if ($DesktopShortcut) {
    # Explicit opt-in: Windows may keep the Desktop on C:.
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'HowsMyUsage.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = "$exe,0"
    $link.Description = 'Native desktop quota widget'
    $link.Save()
}
if ($StartWithWindows) {
    $ps = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $command = '"{0}" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}"' -f $ps, (Join-Path $InstallDir 'startup.ps1')
    New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name UsageWidget -Value $command -PropertyType String -Force | Out-Null
}
Write-Host "Installed HowsMyUsage in $InstallDir"
if (!$NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $InstallDir -WindowStyle Hidden }
