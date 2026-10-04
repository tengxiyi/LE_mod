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

## 版本历史

### v0.6.0(当前)
- **物品信息界面**:词缀行前置彩色 [T品级] 与品级字母+roll 百分比
  (如 A95% [T7] 增加近战伤害 45%;品级取自游戏 ItemAffix.DisplayTier,
  roll 取自 getRollFloat(),隐式等无 tier 的行自动跳过)
- **物品信息界面**:第一行下注入 匹配过滤器规则: #N(金色)——
  ItemFilterManager.Instance.Filter.rules 自上而下、跳过禁用规则、
  调用游戏自己的 Rule.Match,首条命中即编辑器编号
  (接手 LEns 失效的 tooltip 模块,其旧 11 参数挂钩在当前版本失效)
- **游戏设置页(游戏性)底部**:注入"视距"滑条(0.5x~3.0x)——克隆一行
  现成的 NumericSettingUI 滑条行、断开原绑定、接入
  ZoneVisualsManager.farViewDistance,倍率持久化到
  MelonLoader\HybridModSettings.txt(原作者新版 LEns 同款功能)
- **启动器 启动游戏.bat**:比对 GameAssembly 标记检测游戏更新 →
  引导一次启动/关闭 → 自动重建元数据并验证 → 自动重启
  (详见 游戏更新指南.txt)
- 游戏本体 10/2、10/3 两次更新;每次更新后程序集重新生成,均重跑了
  全量规范化(第二次起由启动器自动化)

### v0.5.10(2026-10-03)
- 游戏 10/3 更新 → 程序集重新生成 → 全量规范化 213 个互操作程序集
  (第二次执行此流程;工具 tools/NormalizeAssemblies.cs)

### v0.5.9(2026-10-03)
- 0x80131506 致命错误根治:Cpp2IL 生成的互操作程序集元数据有缺陷,
  延迟类型加载在特定时序下 fail-fast;对全部 213 个程序集做 Cecil
  元数据重建(LoadProbe 213/213;同 CoreModule 疗法,需随每次游戏更新重跑)

### v0.5.8(2026-10-02)
- 地面标签:词缀色块改回名字同行跟随(85%→80%),标签恢复单行高度,
  多物品堆叠遮挡从根源消失;高度同步机制(含 0x80131506 元凶的
  set_text 补丁与 GetTypes 全量扫描)整体退役

### v0.5.7 / v0.5.6b(2026-10-02)
- set_text 高度补丁改为零补丁周期扫描;首次批量规范化互操作程序集
  (0x80131506 当时的结论,后续 v0.5.8/v0.5.9 演进为最终方案)

### v0.5.6(2026-10-02)
- 多物品堆叠遮挡修复尝试:TMP set_text 高度同步(后被同行布局取代)

### v0.5.5(2026-10-02)
- 地面标签色块追加品级数字(如"爆伤 T7")

### v0.5.4(2026-10-02)
- F6/F7 按键检测改为每帧执行(修复被 1.5s 轮询门挡住的问题)

### v0.5.3(2026-10-02)
- 键位退役前缀独立化(每个退役键只检查自己的旧键,修复 F6/F7 被自身
  前缀拦截);移除 prophecy 兼容补丁(其失败尝试与 0x80131506 相关)

### v0.5.2(2026-10-02)
- 信息面板行高修复(19→22px + TextClipping.Overflow);构建引用补充
  (TextRenderingModule/Il2CppLE/UnityEngine.UI/Unity.TextMeshPro)

### v0.5.1(2026-10-02)
- 启动日志清理;键位接管延迟化;消除首次接管误报;吞掉 LEns
  ProphecyTooltip 异常的尝试(该方案在 v0.5.3 移除)

### v0.5.0(2026-10-02)
- 按键重排 F5/F6/F7 + LEns 面板整合(详见启动器说明)

## 尚未完成

- **游戏内对账**：Rust 侧已消费真实伤害（419 条真实日志回放下 Rust 与 LEns 的
  C# 转写逐检查点一致），待下次进游戏把"实测"与 LEns HUD 并排核对。
- **LEns(实况) 读数长期为 0.0**：已加入一次性诊断字段
  `[hits= total= tl= w=]`——若 hits 保持 0 说明取到的计算器实例没有被喂样
  （对象图或实例选择问题）；若 hits>0 而 dps=0 则是窗口时间基准问题。
- **反射路径可能因 LEns 改版而失效**：失效时只是少一行对比信息，不影响 Rust 侧。
