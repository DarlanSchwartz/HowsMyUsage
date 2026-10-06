$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ([IO.Path]::GetPathRoot($PSScriptRoot) -eq 'C:\') { throw 'Execute o projeto fora do C:.' }
$buildRoot = Join-Path $PSScriptRoot '.build'
foreach ($folder in 'temp','cli','packages','http') { New-Item -ItemType Directory -Force (Join-Path $buildRoot $folder) | Out-Null }
$env:TEMP = Join-Path $buildRoot 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $buildRoot 'cli'
$env:NUGET_PACKAGES = Join-Path $buildRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $buildRoot 'http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/widget
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação.' }
