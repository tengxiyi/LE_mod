# LE_mod — Last Epoch Mod 整合（HybridMod + LEns 增强）

基于 MelonLoader 0.7.3 的 Last Epoch(IL2CPP/Unity 6000.4.8f1) Mod 整合:
C# 宿主 + Rust 核心 + 第三方 LEns 增强。

## 功能

- **信息面板**(F5):Rust 核心实时 DPS、金币/青睐收益、开关与按键整合(F5/F6/F7)
- **地上标签重排版**:词缀以品级色块跟随物品名字(含品级数字如"爆伤 T7"),
  规则文件 `GroundLabelRules.txt` 支持自定义高亮(颜色/加粗/品级条件),热加载
- **物品信息界面**:词缀行显示 `[T品级]` + 品级字母与 roll 百分比(如 `A95%`)
- **伤害采集**:TMP 文本钩子 + 配对算法,喂给 Rust 核心实时计算

## 仓库结构

```
HybridMod/
├── CSharpMod/            C# 宿主(补丁、采集、面板、规则)
│   ├── HybridMod.cs      MelonMod 入口
│   ├── DamageNumberCapture.cs   TMP set_text/set_color 采集
│   ├── TooltipTier.cs    物品 tooltip 词缀 tier 显示
│   ├── GroundLabelRestyle.cs    地上标签重排版 + 高亮规则
│   ├── GroundLabelTransform.cs  标签重排版纯逻辑(有测试)
│   ├── GroundLabelRules.cs      高亮规则解析(纯逻辑)
│   ├── DpsOverlay.cs     信息面板(IMGUI)
│   ├── LensPanelPolicy.cs 键位重排 + LEns 面板整合
│   ├── RustBridge.cs     Rust 核心 P/Invoke 声明
│   ├── DamageTextParser.cs / DamageNumberFlags.cs  采集纯逻辑
│   ├── DpsReference.cs   LEns DPS 计算器的 C# 转写(差分测试用)
│   ├── build.ps1         一键构建(csc + cargo + harness)
│   └── README.md         详细文档(架构/决策/版本史)
├── RustCore/             Rust 计算核心(DPS/词缀表,FFI)
├── ProphecySpamGuard/    LEns 预言覆盖层防护
└── tools/refpack/        .NET 参考程序集(不入库,见下)
tools/                    逆向/构建辅助工具(Cecil 系列)
notes/                    调查笔记
游戏更新后-重打补丁.ps1    游戏更新后的一键修复脚本
PRINCIPLES.md             本项目的约束与原则
```

## 构建

需要:Rust(msvc)、.NET 6、VS Build Tools 的 Roslyn(csc)、
以及 `HybridMod/tools/refpack/ref/net6.0`(参考程序集,不入库;
从任意 .NET SDK 安装处复制,或用 `dotnet` 自带)。

```powershell
powershell -File HybridMod/CSharpMod/build.ps1            # 构建 + harness 测试
powershell -File HybridMod/CSharpMod/build.ps1 -Deploy    # 构建并部署到游戏 Mods\
```

## 游戏更新后

游戏更新会使 MelonLoader 重新生成互操作程序集(带有 Cpp2IL 元数据缺陷),
需要重新修复:

```powershell
powershell -File 游戏更新后-重打补丁.ps1
```

详见 `游戏更新指南.txt`(分发包内)与 `notes/`。

## 测试

- Rust:`cargo test`(40 个)
- C# P/Invoke 与逻辑:`build.ps1` 自带的 harness(含 419 条真实伤害日志回放、
  Rust 与 LEns 计算器的逐检查点差分)
