# =====================================================================
#  启动 Last Epoch（Mod 自动修复版）
# ---------------------------------------------------------------------
#  日常: 双击 启动游戏.bat 即可。
#  游戏更新后: 第一次启动会让 MelonLoader 重新生成程序集（此时 Mod 不
#  加载,属正常）——游戏起来后直接关闭游戏,本脚本会自动修复程序集并
#  再次启动游戏。全程无需手动跑修复工具。
# =====================================================================

$ErrorActionPreference = 'SilentlyContinue'

# ---- 定位修复工具（兼容 分发包/工作区 两种布局） ---------------------------------
$toolDirs = @(
    (Join-Path $PSScriptRoot '修复工具'),
    (Join-Path $PSScriptRoot 'tools')
)
$normDll = $null; $probeDll = $null
foreach ($d in $toolDirs) {
    if ((Test-Path (Join-Path $d 'NormalizeAssemblies.dll')) -and
        (Test-Path (Join-Path $d 'LoadProbe.dll'))) {
        $normDll = Join-Path $d 'NormalizeAssemblies.dll'
        $probeDll = Join-Path $d 'LoadProbe.dll'
        break
    }
}

# ---- 定位游戏目录（Steam 库自动发现） --------------------------------------------
function Find-GameDir {
    $libs = New-Object System.Collections.Generic.List[string]
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steam) {
        $libs.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object {
                $libs.Add($_.Matches[0].Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    foreach ($lib in ($libs | Select-Object -Unique)) {
        $cand = Join-Path $lib 'steamapps\common\Last Epoch'
        if (Test-Path (Join-Path $cand 'Last Epoch.exe')) { return $cand }
    }
    return $null
}

$gameDir = Find-GameDir
if (-not $gameDir) {
    $gameDir = Read-Host '未自动找到游戏目录,请粘贴 Last Epoch 游戏根目录完整路径'
}
$assemblies = Join-Path $gameDir 'MelonLoader\Il2CppAssemblies'
$ga = Join-Path $gameDir 'GameAssembly.dll'
$marker = Join-Path $gameDir 'MelonLoader\.modpatch_state'

if (-not (Test-Path $ga)) {
    Write-Host "[错误] 游戏目录不正确：$gameDir" -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}

if (-not $normDll) {
    Write-Host '[警告] 未找到修复工具,仅直接启动游戏（无自动修复）。' -ForegroundColor Yellow
    Start-Process 'steam://rungameid/899770'
    exit 0
}

if (Get-Process -Name 'Last Epoch' -ErrorAction SilentlyContinue) {
    Write-Host '[提示] 游戏已在运行。'
    exit 0
}

# ---- 更新检测（与上次修复时的游戏本体比对） --------------------------------------
$gaItem = Get-Item $ga
$gaStamp = '{0}|{1}' -f $gaItem.LastWriteTimeUtc.Ticks, $gaItem.Length
$lastStamp = $null
if (Test-Path $marker) { $lastStamp = Get-Content $marker -TotalCount 1 }

if ($lastStamp -eq $gaStamp) {
    Write-Host '游戏无更新,直接启动...'
    Start-Process 'steam://rungameid/899770'
    exit 0
}

Write-Host '===========================================' -ForegroundColor Cyan
Write-Host ' 检测到游戏更新（或首次配置）。流程：' -ForegroundColor Cyan
Write-Host '   1) 现在启动游戏 —— MelonLoader 会重新生成程序集' -ForegroundColor Cyan
Write-Host '      （此时 Mod 不加载,属正常现象）' -ForegroundColor Cyan
Write-Host '   2) 游戏起来后,直接关闭游戏（Alt+F4 或正常退出）' -ForegroundColor Cyan
Write-Host '   3) 脚本自动修复程序集,并再次启动游戏' -ForegroundColor Cyan
Write-Host '===========================================' -ForegroundColor Cyan
Read-Host '按回车开始'

Start-Process 'steam://rungameid/899770'
Write-Host '等待游戏启动...'
$deadline = (Get-Date).AddMinutes(10)
while (-not (Get-Process -Name 'Last Epoch' -ErrorAction SilentlyContinue)) {
    if ((Get-Date) -gt $deadline) {
        Write-Host '[提示] 未检测到游戏进程（可能未登录 Steam）,本次结束。' -ForegroundColor Yellow
        exit 0
    }
    Start-Sleep -Seconds 2
}
Write-Host '游戏已启动。请直接关闭游戏（修复将在游戏退出后自动进行）。'
Wait-Process -Name 'Last Epoch' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Write-Host '游戏已退出,开始修复程序集...'

# ---- 修复（元数据重建 → 验证 → 写回） --------------------------------------------

$tmp = Join-Path $env:TEMP ('le_norm_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
& dotnet $normDll $assemblies $tmp
if ($LASTEXITCODE -ne 0) {
    Write-Host '[错误] 元数据重建失败,游戏目录未被修改。请把上方输出发给分享者。' -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}
& dotnet $probeDll $tmp
$probe = & dotnet $probeDll $tmp | Out-String
if ($probe -match 'FAILED\s*:\s*([1-9]\d*)') {
    Write-Host "[错误] 验证未通过（$($Matches[1]) 个程序集失败）,游戏目录未被修改。重新运行本脚本可重试。" -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}
Copy-Item "$tmp\*" $assemblies -Force
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

# ---- 记录标记并再次启动 ----------------------------------------------------------

Set-Content -Path $marker -Value $gaStamp -Encoding ASCII
Write-Host '修复完成,重新启动游戏...' -ForegroundColor Green
Start-Process 'steam://rungameid/899770'
