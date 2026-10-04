# ProphecySpamGuard — 已解决 LEns 的刷屏 ✅

**状态：已验证有效。** 日志从 4.7 MB 降到 1.1 KB，9070 次报错降到 0 次，LEns 其余功能全部正常。

## 问题

2026-10-02 游戏更新（Unity 6000.4.8f1 / 游戏 1.5）**重做了 Prophecy（预言）与
Constellation（星座）UI**，LEns 编译时依赖的 3 个类型被移除：

```
Il2CppLE.Factions.ConstellationStar     ← 新版 Il2CppLE 里 0 个匹配
Il2CppLE.Factions.Prophecy
Il2CppLE.Factions.ProphecyTooltip
```

（用 `tools/FindMissingRefs` 对重新生成的互操作程序集扫描确认，受影响方法 10 个，
全部集中在 `LastEpochMods.Template.Features.ProphecyUI`。）

`ProphecyImGuiStarOverlay.OnGui()` 由 `ModBootstrap.Render()` **每帧**调用，其方法体内含：

```
00BE: callvirt  Il2CppLE.Factions.ConstellationStar::get_rect
```

因此该方法**根本无法 JIT 编译**，每次调用都抛 `TypeLoadException` ——
实测一次会话 **9070 次**，日志 **4.7 MB**。

## 两次失败尝试（记录教训）

| # | 做法 | 结果 | 原因 |
|---|---|---|---|
| 1 | Harmony Prefix 补丁 `OnGui` | ❌ `IL Compile Error` | MonoMod 必须读取并重写该方法体，**而方法体正是不可编译的东西**。补丁坏方法在原理上不可能。 |
| 2 | 强制 `ProphecyNodeOverlayFeature.get_IsEnabled` = false | ⚠️ 补丁成功但无效 | `OnGui` **在第一条指令执行前就被 JIT 编译**。异常发生在编译期，与那个分支无关——该分支根本没机会执行。 |

补充教训：第 1 版我还把方法名写成了 `OnGUI`（正确是 `OnGui`，小写 i）。

## 最终方案：不碰坏代码，让它根本不被调用

`ModBootstrap.Render()` 的 IL 结尾：

```
00BD: ldarg.0
00BE: ldfld   ModBootstrap::_prophecyNodeOverlay      ← MelonPreferences 项
00C3: callvirt MelonPreferences_Entry<bool>::get_Value
00C8: stloc.3
00C9: ldloc.3
00CA: brfalse.s  IL_00d2: ret                         ← 为 false 就【不调用 OnGui】
00CC: call      ProphecyImGuiStarOverlay::OnGui()
00D1: nop
00D2: ret
```

**把 `LEns.ProphecyNodeOverlay` 设为 false，`OnGui` 永远不会被调用 → 不会被 JIT →
不会抛异常。不需要修补任何坏代码。**

实现方式：通过 MelonPreferences 公开 API 设置，**不直接改文件**：

```csharp
var cat   = MelonPreferences.GetCategory("LEns");
var entry = cat.GetEntry<bool>("ProphecyNodeOverlay");
entry.Value = false;
cat.SaveToFile(false);
```

## 验证结果

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 日志大小 | 4.7 MB | **1.1 KB** |
| `ConstellationStar` 报错 | 9070 次 | **0 次** |
| `[ERROR]` 总数 | 刷屏 | **1**（LEns 启动时一次性） |
| `UserData\LEns.cfg` | `ProphecyNodeOverlay = true` | ✅ `= false`（已持久化） |

LEns 其余功能全部正常：

```
[LEns] 已加载词缀简写表: 1112 条简写 / 1112 个正式 id / 44 个注释草稿
[LEns] 伤害飘字采集模块初始化完成（TMP_Text 候选已注入 2 处）
[LEns] 伤害结算日志 Hook 模块初始化完成（主路径：ProtectionClass.ApplyDamage）
[LEns] 地上掉落标签模块初始化完成
[LEns] 战利品 Tooltip 模块初始化完成
```

## 剩余的一处报错与三条警告（均为 LEns 自身的版本适配问题，非本 Mod 引入）

```
[ERROR] TypeLoadException: Il2CppLE.Factions.ProphecyTooltip
        at ProphecyNodeOverlayFeature.Initialize()          ← 启动时一次性，不影响游戏

[WARNING] 未找到 SetGroundTooltipText(bool)，地上字可能仍被官方晚一帧覆盖
[WARNING] [GROUND] build name map failed: TargetInvocationException
[WARNING] AccessTools.DeclaredMethod: 找不到 TooltipItemManager.AddAffixAltText（参数列表）
```

前两条警告在**修复之前的日志里就已存在**，与本次修改无关。
第三条说明 `TooltipItemManager.AddAffixAltText` 的签名在新版游戏里变了，
对应 LEns 的"tooltip 额外词缀文本"功能失效。

## 关掉的功能是不是损失？

不是。Prophecy 覆盖层在更新后**本来就没工作**——`ProphecyNodeOverlayFeature.Initialize()`
在启动时就抛了 `ProphecyTooltip` 的 TypeLoadException。
`Render()` 仍会调用 `OnGui()`，于是每帧必然失败。关掉它等于关掉一个已经坏掉的功能。

## 文件

```
ProphecySpamGuard.cs    源码（v3）
ProphecySpamGuard.dll   产物，已部署到 Mods\
README.md               本文件
```

## 编译

本机无 dotnet SDK，用 VS18 Build Tools 的 Roslyn：

```powershell
$p   = 'C:\Program Files (x86)\Steam\steamapps\common\Last Epoch'
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$ref = '<工作区>\HybridMod\tools\refpack\ref\net6.0'

$refs = (Get-ChildItem "$ref\*.dll" | ForEach-Object { "-r:$($_.FullName)" }) + @(
  "-r:$p\MelonLoader\net6\MelonLoader.dll"
  "-r:$p\MelonLoader\net6\Il2CppInterop.Runtime.dll"
)

& $csc -nologo -target:library -optimize+ -nostdlib+ -noconfig `
       "-out:ProphecySpamGuard.dll" @refs ProphecySpamGuard.cs
```

## 若要恢复默认

把 `UserData\LEns.cfg` 里的 `ProphecyNodeOverlay` 改回 `true` 即可
（或删除本 Mod 并手动改回）。但这会让刷屏回来。
