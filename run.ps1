# 用法：右键“使用 PowerShell 运行”，或在终端里 .\run.ps1
# 为什么要有这个脚本：本机 MSBuild 的并行节点会失败（报「生成失败 / 0 个错误」），
# 所以所有 dotnet 命令都必须带 -m:1。与其让人记，不如包一层。
param(
    [switch]$NoBuild,
    [string]$Index = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

if (-not $Index) { $Index = Join-Path $root '.cache\index.json' }

$cli = Join-Path $root 'src\SolsticeWinter.Cli\bin\Debug\net10.0\SolsticeWinter.Cli.dll'
$app = Join-Path $root 'src\SolsticeWinter.App\bin\Debug\net10.0-windows\SolsticeWinter.App.exe'

if (-not $NoBuild) {
    # 构建前先结束正在跑的实例：否则 SolsticeWinter.*.dll 被占用，MSBuild 会报 MSB3021/MSB3027
    Get-Process SolsticeWinter.App -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "结束正在运行的实例 PID $($_.Id)"
        $_.Kill()
    }
    Start-Sleep -Milliseconds 500

    Write-Host '构建（-m:1）…'
    dotnet build (Join-Path $root 'Solstice-winter.slnx') -m:1 --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
}

if (Test-Path $Index) {
    Write-Host "索引已存在，跳过构建：$Index（要重建就删掉它，或跑 solstice-winter index build）"
}

if (-not (Test-Path $Index)) {
    Write-Host "索引不存在，先建一份：$Index"
    $contentRoot = 'D:\devenv\fmodel\Output\Exports\kards\Content'
    if (-not (Test-Path $contentRoot)) {
        throw "找不到内容根 $contentRoot —— 先改这个脚本里的路径，或用 solstice-winter index build 手动建索引"
    }
    dotnet $cli index build $contentRoot --out $Index | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "建索引失败（退出码 $LASTEXITCODE）" }
}

Write-Host "启动 $app"
& $app $Index


