# 校验「UI 实际渲染」与「无头反编译」的节点数是否一致。
#
# 为什么需要它：新反编译器曾经在 UI 线程上崩过，异常被吞掉后**静默回退旧投影**——
# 画布上显示的是另一张图，而 --vmdump 走无头路径完全正常，两边看起来都「成功」。
# 只有把两边数字摆在一起才看得出问题。
#
# 用法：pwsh -File tools/check-ui.ps1 [-Asset <虚拟路径>] [-Functions a,b,c]
param(
    [string]$Asset = '/Game/Blueprints/Logic/BP_Logic',
    [string[]]$Functions = @('CanIDoAnything', 'GetGraphicInfoString'),
    [string]$Exe = 'src\SolsticeWinter.App\bin\Debug\net10.0-windows\SolsticeWinter.App.exe',
    [int]$WaitSeconds = 12
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if (-not (Test-Path $Exe)) { throw "找不到 $Exe，先构建。" }

    $bad = 0
    foreach ($name in $Functions) {
        $null = Start-Process -FilePath $Exe -WorkingDirectory $repo `
            -ArgumentList '--vmdump', $Asset, $name -Wait -PassThru
        $headlessText = Get-Content '.cache\probe\vmdump.txt' -Raw -Encoding UTF8
        if ($headlessText -match 'UI 节点 (\d+)') { $headless = [int]$Matches[1] } else { $headless = -1 }

        Remove-Item '.cache\ui.log' -ErrorAction SilentlyContinue
        $proc = Start-Process -FilePath $Exe -WorkingDirectory $repo `
            -ArgumentList '--open', $Asset, $name -PassThru
        Start-Sleep -Seconds $WaitSeconds
        Get-Process SolsticeWinter.App -ErrorAction SilentlyContinue | Stop-Process -Force

        $log = Get-Content '.cache\ui.log' -ErrorAction SilentlyContinue
        $uiLine = ($log | Select-String -Pattern '新反编译器' | Select-Object -Last 1).Line
        $ui = if ($uiLine -and $uiLine -match '(\d+) 节点') { [int]$Matches[1] } else { -1 }
        $failures = ($log | Select-String -Pattern '失败' | Measure-Object).Count

        $ok = ($headless -eq $ui) -and ($ui -ge 0) -and ($failures -eq 0)
        if (-not $ok) { $bad++ }

        $mark = if ($ok) { 'OK  ' } else { 'FAIL' }
        Write-Output "$mark $name  无头=$headless  UI=$ui  失败行=$failures"
    }

    if ($bad -gt 0) {
        Write-Output ""
        Write-Output "$bad 个函数不一致——UI 可能回退到了旧投影，或者渲染阶段抛了异常。"
        exit 1
    }

    Write-Output ""
    Write-Output "全部一致（$($Functions.Count) 个函数）。"
}
finally {
    Pop-Location
}
