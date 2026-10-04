# =====================================================================
#  游戏更新后重打补丁（Last Epoch Mod 整合包专用）
# ---------------------------------------------------------------------
#  什么时候用: Steam 更新了 Last Epoch 之后,首次启动游戏让 MelonLoader
#  重新生成程序集,然后【退出游戏】再运行本脚本。
#
#  它做什么: 用 Mono.Cecil 把 MelonLoader\Il2CppAssemblies 里全部 213 个
#  程序集做一次元数据重建(修复 Cpp2IL 生成的元数据缺陷),并用 LoadProbe
#  验证全部可加载后写回游戏目录。
#
#  用法: powershell -ExecutionPolicy Bypass -File 本文件名.ps1
# =====================================================================

$ErrorActionPreference = 'Stop'

$workspace = 'C:\Users\tomas\Documents\LastEpoch_Mod_Workspace'
$gameDir   = 'C:\Program Files (x86)\Steam\steamapps\common\Last Epoch'
$assemblies = Join-Path $gameDir 'MelonLoader\Il2CppAssemblies'
$tempOut   = Join-Path $workspace 'out\interop_normalized'
$tools     = Join-Path $workspace 'tools'

Write-Host '=== 0) 前置检查 ===' -ForegroundColor Cyan
if (Get-Process -Name 'Last Epoch' -ErrorAction SilentlyContinue) {
    Write-Error '游戏正在运行,请先完全退出游戏再运行本脚本。'
    exit 1
}
foreach ($p in @("$tools\NormalizeAssemblies.dll", "$tools\LoadProbe.dll", $assemblies)) {
    if (-not (Test-Path $p)) { Write-Error "缺少: $p"; exit 1 }
}
Write-Host 'OK: 游戏已关闭,工具齐备。'

Write-Host '=== 1) 元数据重建(全部互操作程序集) ===' -ForegroundColor Cyan
if (Test-Path $tempOut) { Remove-Item "$tempOut\*" -Force -ErrorAction SilentlyContinue }
& dotnet (Join-Path $tools 'NormalizeAssemblies.dll') $assemblies $tempOut
if ($LASTEXITCODE -ne 0) { Write-Error '规范化失败,查看上方输出。'; exit 1 }

Write-Host '=== 2) LoadProbe 验证(必须全部可加载) ===' -ForegroundColor Cyan
& dotnet (Join-Path $tools 'LoadProbe.dll') $tempOut
# LoadProbe 以自身退出码之外还会打印汇总;失败则中止
$probeOut = & dotnet (Join-Path $tools 'LoadProbe.dll') $tempOut | Out-String
if ($probeOut -match 'FAILED\s*:\s*([1-9]\d*)') {
    Write-Error "仍有 $($Matches[1]) 个程序集加载失败,已中止,游戏目录未被修改。"
    exit 1
}

Write-Host '=== 3) 写回游戏目录 ===' -ForegroundColor Cyan
Copy-Item "$tempOut\*" $assemblies -Force
Write-Host "已写回 $((Get-ChildItem $assemblies -Filter *.dll).Count) 个程序集。"

Write-Host '=== 完成。现在可以启动游戏了。 ===' -ForegroundColor Green
exit 0
