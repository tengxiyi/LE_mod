# Builds the C# host + Rust core, and optionally deploys them.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files as ANSI unless they carry a
# UTF-8 BOM, which silently corrupts non-ASCII string literals and breaks parsing.
# Chinese documentation lives in README.md instead.
#
# Usage:
#   powershell -File build.ps1              # build everything, then run the P/Invoke harness
#   powershell -File build.ps1 -Deploy      # build, then copy the mod + native core into Mods\
#   powershell -File build.ps1 -SkipHarness # build only
#
# There is no .NET SDK on this machine, so csc from the VS Build Tools is invoked directly.

[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$SkipHarness
)

$ErrorActionPreference = 'Stop'

$gameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Last Epoch'
$csc     = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'

$modRoot = Split-Path -Parent $PSScriptRoot
$refPack = Join-Path $modRoot 'tools\refpack\ref\net6.0'
$rustDir = Join-Path $modRoot 'RustCore'
$srcDir  = Join-Path $modRoot 'CSharpMod'
$binDir  = Join-Path $srcDir  'bin'
$harness = Join-Path $srcDir  'harness'

foreach ($p in @($csc, $refPack, $rustDir, $srcDir)) {
    if (-not (Test-Path $p)) { throw "Required path missing: $p" }
}

# ---------------------------------------------------------------- Rust core
Write-Host '=== 1) Build the Rust core ===' -ForegroundColor Cyan
$env:LENS_REAL_TSV = Join-Path $gameDir 'Mods\AffixAbbrev.tsv'
Push-Location $rustDir
try {
    & cargo test --lib
    if ($LASTEXITCODE -ne 0) { throw 'cargo test failed' }
    & cargo build --release
    if ($LASTEXITCODE -ne 0) { throw 'cargo build --release failed' }
} finally {
    Pop-Location
}

$nativeDll = Join-Path $rustDir 'target\release\lens_core.dll'
if (-not (Test-Path $nativeDll)) { throw "Did not produce $nativeDll" }

# ---------------------------------------------------------------- references
$refArgs = @(Get-ChildItem (Join-Path $refPack '*.dll') | ForEach-Object { "-r:$($_.FullName)" })

# HybridMod needs MelonLoader; the harness does not, but passing the extra references is harmless
# because unreferenced assemblies are never loaded.
$modRefs = $refArgs + @(
    "-r:$(Join-Path $gameDir 'MelonLoader\net6\MelonLoader.dll')"
    "-r:$(Join-Path $gameDir 'MelonLoader\net6\0Harmony.dll')"
    "-r:$(Join-Path $gameDir 'MelonLoader\net6\Il2CppInterop.Runtime.dll')"
    # Il2Cppmscorlib declares Il2CppSystem.Type, which Il2CppInterop.Runtime's
    # `Il2CppType.From(System.Type)` returns. Needed for scene-object discovery in
    # DamageNumberCapture; without it that call cannot be compiled at all.
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\Il2Cppmscorlib.dll')"
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\UnityEngine.CoreModule.dll')"
    # IMGUI (GUI/GUIStyle/Rect for DpsOverlay) and legacy Input (hotkeys F5/F6, [ ]) live in
    # their own Unity modules; the interop copies are what MelonLoader loads at runtime.
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\UnityEngine.IMGUIModule.dll')"
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\UnityEngine.InputLegacyModule.dll')"
    # TextAnchor (label alignment) is declared in the text rendering module.
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\UnityEngine.TextRenderingModule.dll')"
    # Ground-label row cloning needs the game's own settings/UI interop types.
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\Il2CppLE.dll')"
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\UnityEngine.UI.dll')"
    "-r:$(Join-Path $gameDir 'MelonLoader\Il2CppAssemblies\Unity.TextMeshPro.dll')"
)

# Shared by the mod and the harness: no MelonLoader/Unity dependency, so both can compile it.
$commonSrc = @(
    (Join-Path $srcDir 'NullableShims.cs')      # needed by `csc -nostdlib+`; see that file's header
    (Join-Path $srcDir 'RustBridge.cs')
    (Join-Path $srcDir 'DamageTextParser.cs')   # pure rules, covered by the harness
    (Join-Path $srcDir 'DamageNumberFlags.cs')  # pure rules, covered by the harness
    (Join-Path $srcDir 'GroundLabelTransform.cs') # pure rules, covered by the harness
    (Join-Path $srcDir 'GroundLabelRules.cs')     # pure rules, covered by the harness
)

# Only the harness compiles these: the reference implementation exists purely to be compared against
# the Rust core, so it must not ship inside the mod.
$harnessOnlySrc = @(
    (Join-Path $srcDir 'DpsReference.cs')
)

# Only the mod compiles these: they reference MelonLoader/Harmony.
$modOnlySrc = @(
    (Join-Path $srcDir 'DamageNumberCapture.cs')
    (Join-Path $srcDir 'HybridMod.cs')
    (Join-Path $srcDir 'DpsOverlay.cs')
    (Join-Path $srcDir 'GroundLabelRestyle.cs')
    (Join-Path $srcDir 'TooltipTier.cs')
    (Join-Path $srcDir "ViewDistance.cs")
    (Join-Path $srcDir 'LensPanelPolicy.cs')
)

New-Item -ItemType Directory -Path $binDir, $harness -Force | Out-Null

# ---------------------------------------------------------------- MelonMod
Write-Host ''
Write-Host '=== 2) Build HybridMod.dll ===' -ForegroundColor Cyan
& $csc -nologo -target:library -optimize+ -nostdlib+ -noconfig `
       "-out:$binDir\HybridMod.dll" @modRefs @commonSrc @modOnlySrc
if ($LASTEXITCODE -ne 0) { throw 'HybridMod.dll failed to compile' }

# ---------------------------------------------------------------- harness
Write-Host ''
Write-Host '=== 3) Build the standalone P/Invoke harness ===' -ForegroundColor Cyan
& $csc -nologo -target:exe -optimize+ -nostdlib+ -noconfig `
       "-out:$harness\CSharpHarness.dll" @modRefs @commonSrc @harnessOnlySrc (Join-Path $srcDir 'Harness.cs')
if ($LASTEXITCODE -ne 0) { throw 'CSharpHarness.dll failed to compile' }

$rc = @{ runtimeOptions = @{ tfm = 'net6.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = '6.0.0' } } } |
      ConvertTo-Json -Depth 5
Set-Content -Path (Join-Path $harness 'CSharpHarness.runtimeconfig.json') -Value $rc -Encoding UTF8

Write-Host ''
Write-Host 'Artifacts:' -ForegroundColor Green
Get-Item $nativeDll, "$binDir\HybridMod.dll", "$harness\CSharpHarness.dll" |
    Select-Object @{n='KB';e={[math]::Round($_.Length/1KB,1)}},Name | Format-Table -AutoSize

# ---------------------------------------------------------------- harness run
if (-not $SkipHarness) {
    Write-Host ''
    Write-Host '=== 4) Run the P/Invoke harness (verifies the managed/native boundary) ===' -ForegroundColor Cyan
    & dotnet "$harness\CSharpHarness.dll" (Join-Path $rustDir 'target\release')
    if ($LASTEXITCODE -ne 0) { throw 'The harness reported failures' }
}

# ---------------------------------------------------------------- deploy
if ($Deploy) {
    Write-Host ''
    Write-Host '=== 5) Deploy into Mods\ ===' -ForegroundColor Cyan
    $mods = Join-Path $gameDir 'Mods'
    if (-not (Test-Path $mods)) { throw "Not found: $mods" }

    if (Get-Process -Name 'Last Epoch' -ErrorAction SilentlyContinue) {
        Write-Warning 'The game is running; the new mod loads on the next launch.'
    }

    Copy-Item "$binDir\HybridMod.dll" (Join-Path $mods 'HybridMod.dll') -Force
    Copy-Item $nativeDll              (Join-Path $mods 'lens_core.dll')  -Force
    Write-Host 'Copied HybridMod.dll and lens_core.dll' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green

# Leave an explicit success code. PowerShell would otherwise adopt whatever $LASTEXITCODE the last
# native command left behind (cargo can exit non-zero purely for a deprecation notice), which makes
# the script look failed to any caller that checks.
exit 0
