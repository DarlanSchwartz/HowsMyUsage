param([Parameter(Mandatory)][string]$Payload, [Parameter(Mandatory)][string]$Output, [string]$Version = '1.2.4')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$scratch = Join-Path $root ('.build/setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch
try {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'Visual Studio C++ x64 build tools are required.' }
    $vc = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    $icon = (Join-Path $root 'assets/usage.ico').Replace('\', '/')
    $manifest = (Join-Path $PSScriptRoot 'setup.manifest').Replace('\', '/')
    @"
1 ICON "$icon"
1 24 "$manifest"
"@ | Set-Content -LiteralPath "$scratch/uninstall.rc" -Encoding UTF8
    $uninstallSource = Join-Path $PSScriptRoot 'Uninstall.cpp'
    $uninstallOutput = Join-Path $Payload 'Uninstall.exe'
    $compileUninstall = "call `"$vc`" >nul && rc /nologo /fo `"$scratch/uninstall.res`" `"$scratch/uninstall.rc`" && cl /nologo /O2 /MT /std:c++17 /EHsc /utf-8 /DUNICODE /D_UNICODE `"$uninstallSource`" `"$scratch/uninstall.res`" /Fo`"$scratch/uninstall.obj`" /Fe`"$uninstallOutput`" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO user32.lib shell32.lib ole32.lib uuid.lib advapi32.lib comctl32.lib"
    & cmd.exe /d /c $compileUninstall
    if ($LASTEXITCODE -ne 0) { throw 'Native uninstaller compilation failed.' }
    $relativeFiles = @((Get-ChildItem -LiteralPath $Payload -File -Recurse).FullName | ForEach-Object { $_.Substring($Payload.TrimEnd('\', '/').Length + 1) }) + 'install-files.txt'
    [IO.File]::WriteAllLines((Join-Path $Payload 'install-files.txt'), $relativeFiles, [Text.UTF8Encoding]::new($false))
    $files = Get-ChildItem -LiteralPath $Payload -File -Recurse | Sort-Object FullName
    $ddf = @('.OPTION EXPLICIT', '.Set Cabinet=on', '.Set Compress=on', '.Set CompressionType=LZX', '.Set CompressionMemory=21',
        '.Set MaxDiskSize=0', '.Set CabinetNameTemplate=app.cab', ".Set DiskDirectoryTemplate=$scratch", ".Set RptFileName=$scratch/report.txt", ".Set InfFileName=$scratch/setup.inf")
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Payload.TrimEnd('\', '/').Length + 1)
        $ddf += '"' + $file.FullName + '" "' + $relative + '"'
    }
    Set-Content -LiteralPath "$scratch/payload.ddf" -Value $ddf -Encoding ASCII
    & makecab.exe /F "$scratch/payload.ddf" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cabinet creation failed.' }
    $cab = "$scratch/app.cab".Replace('\', '/')
    $icon = (Join-Path $root 'assets/usage.ico').Replace('\', '/')
    $manifest = (Join-Path $PSScriptRoot 'setup.manifest').Replace('\', '/')
    $versionTuple = $Version.Replace('.', ',') + ',0'
    @"
1 ICON "$icon"
101 RCDATA "$cab"
1 24 "$manifest"
1 VERSIONINFO
FILEVERSION $versionTuple
PRODUCTVERSION $versionTuple
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "ProductName", "HowsMyUsage"
   VALUE "FileDescription", "HowsMyUsage Setup"
   VALUE "FileVersion", "$Version"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@ | Set-Content -LiteralPath "$scratch/setup.rc" -Encoding UTF8
    $vc = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    $source = Join-Path $PSScriptRoot 'Setup.cpp'
    Set-Content -LiteralPath "$scratch/version.h" -Value "#define SETUP_VERSION L`"$Version`"" -Encoding ASCII
    $command = "call `"$vc`" >nul && rc /nologo /fo `"$scratch/setup.res`" `"$scratch/setup.rc`" && cl /nologo /O2 /MT /std:c++17 /EHsc /utf-8 /DUNICODE /D_UNICODE /FI`"$scratch/version.h`" `"$source`" `"$scratch/setup.res`" /Fo`"$scratch/setup.obj`" /Fe`"$Output`" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO user32.lib gdi32.lib gdiplus.lib shell32.lib ole32.lib uuid.lib setupapi.lib advapi32.lib dwmapi.lib uxtheme.lib comctl32.lib"
    & cmd.exe /d /c $command
    if ($LASTEXITCODE -ne 0) { throw 'Native installer compilation failed.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root '.build')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid scratch directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
