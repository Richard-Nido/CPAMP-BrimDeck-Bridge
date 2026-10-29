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
New-Item -ItemType Directory -Force dist/images | Out-Null
Copy-Item docs/images/account-cards-preview.png dist/images -Force
Copy-Item docs/SOURCE_INSPECTION.md dist/SOURCE_INSPECTION.md -Force
$readme=Get-Content README.md -Raw
$readme.Replace('docs/images/','images/').Replace('docs/SOURCE_INSPECTION.md','SOURCE_INSPECTION.md').Replace('(scripts/','(').Replace('](scripts)','](.)').TrimEnd()+"`n" | Set-Content dist/README.md -Encoding utf8 -NoNewline
Write-Output 'Published: dist/CPAMP-BrimDeck-Bridge.exe'
