param([Parameter(Mandatory)][string]$Baseline,[Parameter(Mandatory)][string]$Candidate,[Parameter(Mandatory)][string]$StateDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$output=Join-Path $PSScriptRoot 'output'
New-Item -ItemType Directory -Force $output | Out-Null
$items=@(@{Name='v1.0.0';Path=[IO.Path]::GetFullPath($Baseline);Port=18740},@{Name='v1.1.0';Path=[IO.Path]::GetFullPath($Candidate);Port=18741})
$processes=@()
try {
    foreach($item in $items){
        $args=@('--serve','--seconds','50','--port',[string]$item.Port,'--state-dir',('"'+[IO.Path]::GetFullPath($StateDirectory)+'"'))
        $p=Start-Process -FilePath $item.Path -ArgumentList $args -WindowStyle Hidden -PassThru
        $processes+=@{Name=$item.Name;Process=$p}
    }
    Start-Sleep -Seconds 10
    $measureStart=[Diagnostics.Stopwatch]::StartNew()
    foreach($item in $processes){$item.Process.Refresh();$item.StartCpu=$item.Process.TotalProcessorTime.TotalMilliseconds;$item.StartWorking=$item.Process.WorkingSet64}
    Start-Sleep -Seconds 25
    $elapsed=$measureStart.Elapsed.TotalSeconds
    $rows=foreach($item in $processes){
        $p=$item.Process;$p.Refresh()
        $cpu=$p.TotalProcessorTime.TotalMilliseconds-$item.StartCpu
        [pscustomobject]@{version=$item.Name;seconds=[Math]::Round($elapsed,3);cpuMs=$cpu;singleCoreCpuPercent=[Math]::Round($cpu/($elapsed*10),4);workingSetMiB=[Math]::Round($p.WorkingSet64/1MB,2);privateMiB=[Math]::Round($p.PrivateMemorySize64/1MB,2)}
    }
    $report=[pscustomobject]@{mode='headless loopback server, real CPAMP, 10s warmup, 25s idle';measuredAt=[DateTimeOffset]::UtcNow.ToString('O');results=@($rows)}
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'idle-comparison.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 5
    foreach($item in $processes){$item.Process.WaitForExit()}
} finally {foreach($item in $processes){if(-not $item.Process.HasExited){$item.Process.WaitForExit(55000) | Out-Null};$item.Process.Dispose()}}
