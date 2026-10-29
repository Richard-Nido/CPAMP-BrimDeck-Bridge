param([string]$BrimDeckInstall='D:\BrimDeck\BrimDeck',[string]$BridgeExe='')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot
$taskBridge=Join-Path $taskRoot 'src/Bridge/bin/Release/net8.0-windows/win-x64/CPAMP-BrimDeck-Bridge.dll'
$taskPwshRoot=Split-Path (Get-Command pwsh).Source
$taskRefs=@(Get-ChildItem (Join-Path $taskPwshRoot 'ref') -Filter '*.dll' | ForEach-Object FullName)+@($taskBridge,(Join-Path $BrimDeckInstall 'BrimDeck.Core.dll'),(Join-Path $BrimDeckInstall 'Jint.dll'),(Join-Path $BrimDeckInstall 'Acornima.dll'),(Join-Path $BrimDeckInstall 'ZstdSharp.dll'))
foreach($taskAssembly in @('Acornima.dll','Jint.dll','ZstdSharp.dll','BrimDeck.Core.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $BrimDeckInstall $taskAssembly))}
[void][Reflection.Assembly]::LoadFrom($taskBridge)
$taskSource="using System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Net.Http; using System.Threading; using System.Threading.Tasks;`n"+(Get-Content (Join-Path $PSScriptRoot 'BrimDeck.Contract/Program.cs') -Raw)
$taskTypes=Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskRefs -PassThru -CompilerOptions @('/langversion:preview','/nowarn:1701','/nullable:enable')
$taskProgram=$taskTypes | Where-Object Name -eq 'Program'
$taskMethod=$taskProgram.GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$taskArguments=[string[]]@($taskRoot)
if($BridgeExe){$taskArguments+=[IO.Path]::GetFullPath($BridgeExe)}
$taskInvokeArgs=New-Object object[] 1
$taskInvokeArgs[0]=[string[]]$taskArguments
$taskMethod.Invoke($null,$taskInvokeArgs).GetAwaiter().GetResult()
exit [Environment]::ExitCode
