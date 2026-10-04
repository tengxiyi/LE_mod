# C# 宿主（HybridMod）

混合方案的托管侧：通过 P/Invoke 调用 Rust 核心 `lens_core.dll`。

## 为什么需要独立测试宿主

三层验证各自证明不同的事，缺一不可：

| 层 | 证明什么 | 不证明什么 |
|---|---|---|
| `cargo test`（40 个） | Rust 逻辑正确 | **托管声明与原生 ABI 是否一致** |
| **`CSharpHarness`（本文）** | 结构体布局、调用约定、字符串编组、DllImport 解析 | 游戏运行时下的行为 |
| **游戏内 `HybridMod`** | 在真实运行时里能跑通 | — |

`cargo test` 完全够不到 P/Invoke 边界：结构体大小不匹配、cdecl 与 stdcall 搞错、
`bool` 编组宽度不对——这些在单元测试里全部"通过"，然后在游戏里静默地把内存写坏。

**测试宿主用同一份 `RustBridge.cs`**，所以它验证的就是真正会部署的代码，不会漂移。

## 文件

```
RustBridge.cs      P/Invoke 声明 + 原生库解析 + 托管包装（无 MelonLoader 依赖，可复用）
HybridMod.cs       MelonMod 入口，游戏内自检 + 与 LEns 实况对比
Harness.cs         独立控制台测试宿主
NullableShims.cs   csc -nostdlib+ 所需的可空性特性 polyfill
build.ps1          一键构建（可选 -Deploy）
```

## 构建 / 测试 / 部署

```powershell
cd <工作区>\HybridMod\CSharpMod

# 构建全部 + 跑 P/Invoke 边界测试
powershell -File build.ps1

# 构建并部署到 Mods\
powershell -File build.ps1 -Deploy

# 只构建
powershell -File build.ps1 -SkipHarness
```

本机**没有 dotnet SDK**，脚本直接调用 VS18 Build Tools 的 Roslyn `csc`。
详见 `build.ps1` 内注释。

## 已验证（harness 实测输出）

```
loaded       : ...\lens_core.dll
  OK   ABI: struct sizes (24/56) and cdecl calling convention
version      : lens_core/0.1.0
  OK   record count == 1112
  OK   table.Length == 1112
  OK   table.Skipped == 50
  OK   NameForId(1) is CJK, i.e. UTF-8 intact (got 提甲)
  OK   TryGetId("提甲") == 1
  OK   unknown abbreviation rejected
  OK   unknown id returns null
  OK   timeline length == 10
  snapshot   : dps=220.0 total=1000 hits=10 critRate=0.0% window=5.0s combat=4.50s
  OK   current dps == 220
  OK   crit rate == 0.5
  OK   incoming is not a hit (got 0)
  OK   max incoming == 777
  OK   null buffer -> LENS_ERR_NULL
  OK   invalid UTF-8 -> LENS_ERR_UTF8
  OK   no error path threw or crashed
ALL CHECKS PASSED
```

几个关键点：

- **`NameForId(1) = 提甲` 且能反查回 1** —— 证明 CJK 的 UTF-8 在**两个方向**上都完好
- **`crit rate == 0.5`** —— 托管 `bool` 正确映射到 Rust `u8`（编组宽度是最容易错的地方之一）
- **`dps=220.0`** —— 与 Rust 单元测试**完全一致**，说明三层算的是同一个数

## 设计要点

### 原生库解析

MelonLoader 从内存加载 Mod，所以 `Mods\` **不在**原生库搜索路径上。
`RustBridge.Initialize` 用 `NativeLibrary.Load(完整路径)` +
`SetDllImportResolver` 显式解析，这样：

- 裸名 `[DllImport("lens_core")]` 也能解析
- **位数不匹配会被明确报告**（`BadImageFormatException` 会被翻译成可读信息，
  提示进程是 x64/x86），而不是抛一个看不懂的 `DllNotFoundException`

### ABI 自检

`ValidateAbi()` 显式校验，而不是假设：

- `LensDamageSample` 必须是 24 字节
- `LensDpsSnapshot` 必须是 56 字节
- `lens_core_add(2000, 37) == 2037` —— 兼作**调用约定探针**；
  若 cdecl/stdcall 搞错，返回值会是垃圾

结构体大小不匹配是 FFI 静默损坏内存的经典方式，所以宁可在启动时报错停用。

### 游戏内的行为

`HybridMod` 在 `OnInitializeMelon` 里：

1. 加载原生核心（失败则明确报错并**安全停用**，不会让游戏崩）
2. ABI 自检
3. 用 Rust 解析真实 `AffixAbbrev.tsv`，打印条数 + CJK 往返结果
4. 跑一个**结果精确已知**的合成场景自检（10 次 100 伤害 / 间隔 0.5s / 窗口 5s → dps=220.0）
5. 之后每 10 秒打一行对比：`Rust(合成) ... ‖ LEns(实况) ...`

### 与 LEns 的对比为什么用反射

LEns 的计算器只能从它自己的对象图里取到，而那条路径上的字段都是**私有实现细节**，
LEns 更新就可能改名。所以：

- 反射读取，**只读**、绝不写入，且失败不影响任何功能
- Rust 侧喂的是**结果已知的合成时间线**，因此 Rust 的正确性与 LEns 是否存在无关
- 两者并排打印，供人在游戏里对照

真正的做法应该是用 hook 喂真实伤害样本并彻底去掉反射——这是下一步。

## 游戏内验证：下次启动后请确认

日志里应出现：

```
[HybridMod] === HybridMod: C# host + Rust core ===
[HybridMod] 原生核心已加载 : ...\Mods\lens_core.dll
[HybridMod] 原生核心版本   : lens_core/0.1.0
[HybridMod] ABI 校验通过   : 结构体 24/56 字节，cdecl 调用约定正确
[HybridMod] 词缀表(Rust)   : 1112 条数据 / 50 行跳过，id=1 -> "提甲"，反向查回 = 成功
[HybridMod] 自检通过       : 合成 10 次伤害 -> dps=220.0 ...（期望 dps=220.0）
[HybridMod] 初始化完成。每 10 秒会在日志里打一行对比结果。
[HybridMod] [对比] Rust(合成) ... dps | ... ‖ LEns(实况) ... dps
```

**如果 `原生核心加载失败`**：确认 `lens_core.dll` 与 `HybridMod.dll` 一起在 `Mods\` 下。

## v0.5.10：tooltip 词缀行增加品级显示（2026-10-04）

[T7] 标记扩展为 "品级字母+roll百分比 + [T品级]"（如 `A95% [T7] 增加近战伤害 45%`）。
品级字母阈值与 LEns 一致（F<50 / C<70 / B<80 / A<95 / S≥95），roll 取自游戏
`ItemAffix.getRollFloat()`（0..1 占 tier 上限比例）。隐式等无 roll 的行只显示
tier 标记。品级配色：S 金 / A 蓝 / B 绿 / C 白 / F 灰。

**未完成**:tooltip 中"匹配战利品过滤器规则号"部分——游戏 `ItemFilter.Match`
重载带 12 个 ref 参数,反射调用复杂,留待下一轮。



新增 `TooltipTier`：Harmony 后缀挂 `TooltipItemManager.AddAffixAltText`
（新版本签名为 7 参数，已对新游戏转储核验；LEns 旧 11 参数挂钩因此失效），
对返回的词缀行前置 `[T品级]` 彩色标记（颜色与地面标签色块同一套 Tier1To7
色表）。品级取自游戏自己的 `ItemAffix.DisplayTier`（密封/卓越的显示规则
归游戏），隐式等无 tier 的行自动跳过。反射读取，无新编译引用。

## v0.5.9：物品信息界面显示词缀 tier（2026-10-04）
## v0.5.8：词缀改回名字同行跟随，彻底解决堆叠遮挡（2026-10-02）

物品名称下方一行的布局使标签变为两行高，多物品堆叠时词缀行会压到相邻标签。
改为：**词缀色块跟随在名字后面同一行**（`名字 [爆伤 T4] [生命 T5]`），字号
85% → **80%**。单行标签恢复游戏原生堆叠间距，遮挡从根源消失；
高度同步机制（含曾致致命错误的 set_text 补丁）整体退役删除。



0x80131506 致命错误的真正元凶确认:高度同步补丁**安装时**的
`a.GetTypes()` 全程序集类型扫描（含 Il2CppLE 的 13898 个类型）在我们的初始化
阶段强制加载了海量游戏类型，与游戏自身初始化竞争破坏了运行时状态——
v0.5.6 在旧游戏本体上也 3/3 必崩（与游戏更新无关），v0.5.5 无此扫描则稳定。

修复：完全移除 set_text 补丁与 GetTypes 扫描，改为零补丁方案——每秒
`FindObjectsOfType(FloatingTextLineUI)` 找出地面标签行，仅对文本含重排版
标记的行调用游戏自己的 `UpdateHeight()`（从 OnUpdate 的稳定点调用，
绝不在 setter 内部进原生 UI 代码）。遮挡修复效果保留。

## v0.5.7：彻底移除 set_text 高度补丁，改为零补丁周期扫描（2026-10-02）
## v0.5.6b：批量规范化互操作程序集，根治 0x80131506 致命错误（2026-10-02）

两次"Fatal error. Internal CLR error. (0x80131506)"都发生在 LEns 的
`GroundItemLabelHook.ResolveGroundGuardTmpTypes` → `Assembly.GetType("TMPro.*")`——
触发点固定但是否发作随机。根因：Cpp2IL 生成的互操作程序集元数据有缺陷
（与 CoreModule 的"Duplicate type '<>O'"同源），延迟的 CLR 类型加载在特定时序下
（与 Il2CppInterop 后台 icall 注册竞争）直接 fail-fast 而非抛可捕获异常。

修复：用 Mono.Cecil 对 **全部 213 个**互操作程序集做读/写往返，重建全部元数据堆
（与 CoreModule 修复同一疗法，该修复已稳定运行一整天）。LoadProbe 验证 213/213
加载成功。原始文件备份于 `out\interop_backup_20261002\`，规范化输出在
`out\interop_normalized\`，游戏目录与分发包均已替换。工具：`tools/NormalizeAssemblies.cs`。

**注意：游戏更新触发 MelonLoader 重新生成后，需要重跑本规范化**（生成物会再次
带同样缺陷）。



游戏的地面标签行（`FloatingTextLineUI`）在垂直布局里按各自声明的高度排位。
LEns 把两行文本直接强写进标签 TMP，绕过了游戏的 `UpdateHeight()`，布局仍按
单行高度分配槽位——重排版多出来的词缀行就压到了相邻标签上。

修复：给 `TMP_Text.set_text` 挂高度同步后缀——文本含重排版标记（`\n<size=`）
时，通过 `GetComponentInParent(Il2CppType.Of<FloatingTextLineUI>())` 找到所属
标签行并调用游戏自己的 `UpdateHeight()` 重新测量，布局随之给两行标签分配
更大的槽位。原生方法经互操作直接调用，无需游戏更新配合。

## v0.5.6：修复多物品堆叠时标签互相遮挡（2026-10-02）


每个词缀色块显示为"词缀 T品级"（如"爆伤 T7"）——品级数字从 LEns 自己的
品级颜色表反推（颜色↔品级一一对应），与色块颜色永远一致。色块背景/文字色/
加粗规则（GroundLabelRules.txt）不受影响，规则匹配仍按原始词缀文字进行。

## v0.5.5：词缀色块追加品级数字（2026-10-02）
## v0.5.4：修复 F6/F7 按键检测被轮询门挡住（2026-10-02）

v0.5.3 修好了前缀拦截，但 F6/F7 的按键检测仍放在 1.5 秒一次的轮询门后面——
`Input.GetKeyDown` 只在按下那一帧为 true，每 1.5 秒查一次几乎必然错过
（日志证据：用户按 F6 后我们的处理器零输出，而 F5 因 DpsOverlay 每帧检测
所以秒生效）。修复：F6/F7 检测改为每帧执行，状态回收仍走 1.5 秒轮询。



1. **F6/F7 无效的根因**：键位退役的 Harmony 前缀 `SkipOriginal` 检查了全部四个旧键，
   而我们自己的 F6 处理器恰好在 F6 按下时调用 `ToggleGroundLabelEnhancement` ——
   被自己的前缀拦截（日志表现为"Hook 未就绪"），F7 同理。修复：每个被退役方法
   挂**只检查自己旧键**的独立前缀，我们的直接调用（在另一个键按下时发生）永远放行。
2. **致命错误 0x80131506 的根因**：v0.5.1 的"兼容 finalizer"对
   `ProphecyNodeOverlayFeature.Initialize` 打补丁——该方法 IL 引用缺失类型，
   Harmony 编译 detour 必然失败（IL Compile Error），且失败尝试与紧随其后的
   `Assembly.GetType` Fatal CLR Error 强相关。修复：彻底移除该补丁，回到 v0.5.0
   的稳定状态（异常由 MelonLoader 记录，无实际影响）。教训：对引用了缺失类型的
   方法做 Harmony 补丁本身就不可行。

## v0.5.3：修复 F6/F7 失效与启动致命错误（2026-10-02）


信息面板每行 19px 装不下 13px 中文字形（上下各被削掉一截）。行高提到 22px，
label 显式 `TextAnchor.MiddleLeft` + `TextClipping.Overflow`（禁止裁剪），内边距
调整为 8/10。构建引用新增 `UnityEngine.TextRenderingModule`（TextAnchor 所在模块）。

## v0.5.1：启动日志清理（2026-10-02）


1. **键位接管延迟化**：HybridMod 比 LEns 先初始化，而 LEns 的 Bootstrap 在它自己的
   OnInitializeMelon 里才创建——v0.5.0 在我们的 Initialize 里找不到它，导致按键退役
   补丁没装上、只在 1.5 秒后被状态回收兜底。现在改为"等 LEns 就绪后接管"：首次
   OnUpdate 轮询（0.5s 间隔）完成补丁安装+初始收起+打印键位表，然后转入 1.5s 常规轮询。
2. **消除误报**："检测到旧键操作，已自动恢复 2 项"实为首次接管的初始状态修正，不再
   误报为按键操作；后续真实漂移才报"状态漂移"。
3. **吞掉 LEns 的 ProphecyTooltip 异常**：对 `ProphecyNodeOverlayFeature.Initialize`
   挂 Harmony finalizer，TypeLoadException 被记录后吞掉——ModBootstrap.Initialize 得以
   继续执行完（其 ApplyPersistedFeatureToggles 首次真正运行），日志里的
   `[ERROR] [LEns] System.TypeLoadException` 消失。该功能在 LEns.cfg 本就关闭
   （ProphecySpamGuard 维持 false），吞掉无副作用。

剩余三条 LEns 自身的 WARNING（SetGroundTooltipText 未找到 / GROUND name map
TargetInvocationException / AddAffixAltText 未找到）是 LEns 在本游戏版本上的内部功能
降级提示，均不影响地上标签增强与我们的重排版，不动它（深修需要改 LEns 内部逻辑，
收益/风险不成比例）。

## v0.5.1：启动日志清理（2026-10-02）


三个保留功能的最终键位（用户指定关联到 F5/6/7）：

| 键 | 功能 | 实现方式 |
|---|---|---|
| **F5** | 统一面板(DPS+收益) 显示/隐藏 | HybridMod 自身 |
| **F6** | 地上显示增强 开/关 | 直调 LEns `HookCollector.ToggleGroundLabelEnhancement()` |
| **F7** | 伤害详细日志 开/关 | 直调 LEns `HookCollector.ToggleDamageApplyLog()` |
| `[` `]` | 词缀字号 50%..120% | HybridMod |

旧绑定退役方式：LEns 的按键在它自己的 Update 里轮询、无法从外部注销，因此对四个旧
处理器的落点方法打"按旧键则跳过"的 Harmony 前缀——`ToggleHud`(F5)、`SetHudEnabled`(F6)、
`ToggleGroundLabelEnhancement`(F7)、`ToggleDamageApplyLog`(F10)。直接调用（我们的新绑定）
不会与旧键同帧，永远放行。另外每 1.5 秒校验一次整合状态（LEns DPS/ECO 面板收起、飘字
采集开启），漂移即恢复。

面板底部新增三个键的实时开关状态行（绿=开/灰=关，rich text）；F6/F7 状态取自
`GroundItemLabelHook.IsGroundLootEnhancementEnabled`（静态）与
`HookCollector.IsDamageApplyLogEnabled`，切换后即时更新。

## v0.5.0：按键重排 F5/F6/F7（2026-10-02）
## v0.4.1：标签高亮规则 + 面板整合（2026-10-02）


1. **地上标签自定义高亮规则**：规则文件 `Mods\GroundLabelRules.txt`（首次运行自动生成
   带注释的模板），改完保存约 1 秒内热加载。条件：`text:关键词` / `tier>=N`（词缀
   色块级）、`lp>=N` / `ww` / `name:关键词`（整签级）；样式：`bg=#RRGGBB[AA]` /
   `fg=#RRGGBB` / `bold`。首条命中规则生效；LP/WW 用 Harmony 前缀从物品模型精确
   读取（不用猜文本）。解析器为纯代码，harness 全覆盖。
2. **面板整合**：F8 面板 = DPS 块（Rust）+ ECO 块（反射读 LEns 的
   EconomyHudFeature.Snapshot.Rows：Abbrev/PerMinute1m/PerMinute5m），位置下移到
   y=192（用户要求约 180px，避开游戏左上角的图标）。启动时一次性隐藏 LEns 自带的
   DPS 面板（F6）与 ECO 面板（F5）——两者的信息已并入本面板，按 F5/F6 随时恢复。
   隐藏 DPS 面板不影响 LEns 的飘字采集（SetTextHookEnabled 独立），对比读取照常。

按键总表：F8 本面板开关；F9 标签重排版开关；`[` `]` 词缀字号；F5/F6 恢复 LEns
自带面板；F7 LEns 地上增强；F10 LEns 伤害日志。

## v0.4.0：DPS 面板 + 地上标签重排版（2026-10-02）

1. **DPS 面板（F8 显示/隐藏）**：Rust 核心的快照直接画在屏幕左上角（IMGUI，
   与 LEns 的 HUD 同一条经过验证的渲染路径）：当前 DPS / 总伤害 / 命中 / 暴击 /
   战斗时长 / 最大被击。文本 5Hz 刷新缓存，OnGUI 只画字符串。
2. **地上标签重排版（F9 开关，`[` `]` 调字号 50%..120%）**：词缀不再以
   `<sup>/<sub>` 小字挤在名字两侧，而是移到名字下方一整行，字号默认 85%
   （比名字略小一丁点），每个词缀带**自己品级颜色的背景色块**
   （TMP `<mark>`，品级色 + 25% alpha，文字保持品级色）。
   实现方式：Harmony 后缀改写 LEns 的 `GroundAffixLabelFormatter.Format`
   返回值——不改 LEns.dll 文件（铁律），只改它的输出字符串；
   形状不符合预期时原样放行，绝不半改不改。
   重排版规则在 `GroundLabelTransform.cs`（纯字符串，harness 覆盖）。

注意：F9 关闭后，已被 LEns 强制显示的标签会在其 5 秒 keep-alive 过期后恢复
原布局。若游戏内 TMP 标签不渲染 `<mark>` 背景（理论上 Unity 6 支持），回退
方案是去掉 mark 只保留放大后的品级色文字——看到效果后决定。

## v0.3.0：按 LEns 的 IL 精确复刻采集管线（2026-10-02）

游戏内实测发现旧采集的两个根本问题，均在 LEns 的 IL（`DamageTextHook::*`，用
tools/DumpIl 逐方法核对）里找到答案并 1:1 复刻：

1. **颜色守卫从不生效（日志里"颜色不符"恒为 0）**：游戏先写 text 后写 color，
   在 text 时读 `color` 属性拿到的是上一个标签的旧颜色。LEns 的做法是从
   **set_color 的第一个参数**取颜色（Harmony 位置注入 `__0`），对本游戏的
   IL2CPP interop setter 有效（其 PostfixReadColor 正在游戏内运行）。
2. **没有来源守卫**：LEns 只接受 GameObject 名字**恰好等于
   `"Damage Number(Clone)"`** 的标签（PassesSourceGuards），数字/颜色都只是
   之后的分类与配对。旧的"纯数字标签"门槛挡不住 mana/经验等数字 UI。

其余逐项对齐：锚定数字正则 `^\D*(\d[\d,\.]*)([kKmMbB]?)\D*$`（故意宽松，靠名字
守卫兜底）、k/m/b 后缀、2..1e8 范围、同帧同值帧守卫（Time.frameCount）、220ms
配对窗口、RGBA 字符串分类（红=受伤/黄=暴击/其余=普攻）、4096 帧守卫上限、
场景切换重置（OnSceneWasLoaded → Rust Reset，与 LEns 对齐）。

**新增诊断计数**：`set_text N 次 → 解析失败/范围外/同帧重复/暂存`、
`set_color N 次 → 无暂存/超时/颜色无法解析/配对成功`。下次运行若 set_color
计数仍为 0，即证明颜色钩子没打上（可能性已被 LEns 排除，但计数会给出定论）。

## 尚未完成

- **游戏内对账**：Rust 侧已消费真实伤害（419 条真实日志回放下 Rust 与 LEns 的
  C# 转写逐检查点一致），待下次进游戏把"实测"与 LEns HUD 并排核对。
- **LEns(实况) 读数长期为 0.0**：已加入一次性诊断字段
  `[hits= total= tl= w=]`——若 hits 保持 0 说明取到的计算器实例没有被喂样
  （对象图或实例选择问题）；若 hits>0 而 dps=0 则是窗口时间基准问题。
- **反射路径可能因 LEns 改版而失效**：失效时只是少一行对比信息，不影响 Rust 侧。
