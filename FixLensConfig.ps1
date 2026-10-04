# FixLensConfig.ps1 - restores the LEns preference that ProphecySpamGuard v3 clobbered.
#
# Background
# ----------
# ProphecySpamGuard v3 called `MelonPreferences_Category.SaveToFile()`, which rewrites the whole
# category from MelonPreferences' own copy of the config file. LEns caches its entries in static
# fields and only writes them back via `entry.set_Value`, so while the two disagreed the save
# reverted `EconomyHudEnabled` from true to false. That is the F6 economy HUD toggle.
#
# v4 no longer saves anything, so this cannot recur.
#
# ASCII-only: Windows PowerShell 5.1 reads .ps1 as ANSI unless it has a UTF-8 BOM, which corrupts
# non-ASCII literals. See CSharpMod/build.ps1 for the same note.
#
# Usage:
#   powershell -File FixLensConfig.ps1            # report only
#   powershell -File FixLensConfig.ps1 -Apply     # write the fix

[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'

$cfg = 'C:\Program Files (x86)\Steam\steamapps\common\Last Epoch\UserData\LEns.cfg'

if (-not (Test-Path $cfg)) { throw "Not found: $cfg" }

# The game writes every preference back on exit, so editing the file while it runs is pointless.
if (Get-Process -Name 'Last Epoch' -ErrorAction SilentlyContinue) {
    Write-Host 'Last Epoch is RUNNING.' -ForegroundColor Yellow
    Write-Host 'Close the game completely first, otherwise it overwrites this file on exit.'
    Write-Host 'Then run this script again with -Apply.'
    exit 1
}

$text = Get-Content -LiteralPath $cfg -Raw -Encoding UTF8

# Only this one key was changed by the mod; everything else is left untouched.
$before = $text
$text = [regex]::Replace($text, '(?m)^(\s*EconomyHudEnabled\s*=\s*)false\s*$', '${1}true')

Write-Host 'Current values:'
foreach ($m in [regex]::Matches($before, '(?m)^(\s*[A-Za-z_]+\s*=\s*)(true|false)\s*$')) {
    Write-Host ("   {0}{1}" -f $m.Groups[1].Value, $m.Groups[2].Value)
}

if ($text -eq $before) {
    Write-Host ''
    Write-Host 'EconomyHudEnabled is already true; nothing to change.' -ForegroundColor Green
    exit 0
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'Would set EconomyHudEnabled = true. Re-run with -Apply to write it.' -ForegroundColor Yellow
    exit 0
}

# Keep a timestamped backup next to the original before touching it.
$backup = "$cfg.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $cfg -Destination $backup
Write-Host ''
Write-Host "Backup written: $backup"

Set-Content -LiteralPath $cfg -Value $text -Encoding UTF8 -NoNewline

Write-Host 'Updated values:'
foreach ($m in [regex]::Matches((Get-Content -LiteralPath $cfg -Raw -Encoding UTF8),
                                '(?m)^(\s*[A-Za-z_]+\s*=\s*)(true|false)\s*$')) {
    Write-Host ("   {0}{1}" -f $m.Groups[1].Value, $m.Groups[2].Value)
}

Write-Host ''
Write-Host 'Done. Launch the game and F6 should bring the economy HUD up.' -ForegroundColor Green
exit 0
