# 打包成单文件 exe
#   普通打包（体积小，需要本机装 .NET 8 桌面运行时）：
#       powershell -ExecutionPolicy Bypass -File publish.ps1
#   完全独立打包（体积大，任何 Windows 都能直接跑）：
#       powershell -ExecutionPolicy Bypass -File publish.ps1 -SelfContained

param([switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$args = @(
    'publish', $root,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $out,
    '-p:PublishSingleFile=true',
    '-p:DebugType=none'
)

if ($SelfContained) {
    $args += '--self-contained=true'
    $args += '-p:IncludeAllContentForSelfExtract=true'
    $args += '-p:EnableCompressionInSingleFile=true'
} else {
    $args += '--self-contained=false'
}

Write-Host '正在打包…' -ForegroundColor Cyan
& dotnet @args

$exe = Join-Path $out 'StardewModManager.exe'
if (-not (Test-Path $exe)) { throw '打包失败，没找到 exe' }

# 顺手复制一份中文名的，方便双击
$cn = Join-Path $out '星露谷Mod管理器.exe'
Copy-Item $exe $cn -Force

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host ("打包完成：" + $exe + "  (" + $size + " MB)") -ForegroundColor Green
Write-Host ("也复制了一份中文名：" + $cn) -ForegroundColor Green
