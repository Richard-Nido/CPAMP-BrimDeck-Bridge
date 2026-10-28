param([string]$Dotnet = 'dotnet')
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot 'tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $PSScriptRoot 'tools/nuget'
if($Dotnet -eq 'dotnet' -and (Test-Path 'tools/dotnet/dotnet.exe')) { $Dotnet=Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe' }
& $Dotnet publish src/Bridge/Bridge.csproj -c Release -o dist --nologo -p:DebugType=None -p:DebugSymbols=false
if($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item scripts/*.js dist -Force
Copy-Item README.md,使用教程.md,BUILD.md,TEST_REPORT.md,LICENSE,THIRD_PARTY_NOTICES.md dist -Force
New-Item -ItemType Directory -Force dist/licenses | Out-Null
Copy-Item docs/licenses/* dist/licenses -Force
Write-Output 'Published: dist/CPAMP-BrimDeck-Bridge.exe'
