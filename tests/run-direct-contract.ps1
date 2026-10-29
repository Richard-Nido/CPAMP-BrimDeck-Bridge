param([string]$BrimDeckInstall='D:\BrimDeck\BrimDeck',[switch]$Live)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot
$taskBridge=Join-Path $taskRoot 'src/Bridge/bin/Release/net8.0-windows/win-x64/CPAMP-BrimDeck-Bridge.dll'
$taskPwshRoot=Split-Path (Get-Command pwsh).Source
$taskRefs=@(Get-ChildItem (Join-Path $taskPwshRoot 'ref') -Filter '*.dll' | ForEach-Object FullName)+@($taskBridge,(Join-Path $BrimDeckInstall 'BrimDeck.Core.dll'),(Join-Path $BrimDeckInstall 'Jint.dll'),(Join-Path $BrimDeckInstall 'Acornima.dll'),(Join-Path $BrimDeckInstall 'ZstdSharp.dll'))
foreach($taskAssembly in @('Acornima.dll','Jint.dll','ZstdSharp.dll','BrimDeck.Core.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $BrimDeckInstall $taskAssembly))}
[void][Reflection.Assembly]::LoadFrom($taskBridge)
$taskSource="using System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Net.Http; using System.Threading; using System.Threading.Tasks;`n"+(Get-Content (Join-Path $PSScriptRoot 'DirectContract.cs') -Raw)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskRefs -CompilerOptions @('/langversion:preview','/nowarn:1701','/nullable:enable')
exit [DirectContract]::Run($taskRoot,$Live.IsPresent).GetAwaiter().GetResult()
