# 铁律：只动三样东西

> 本文件是本工作区的最高约束。任何后续任务、脚本、子代理都必须遵守。

## 规则（用户原话，最终版）

> "MelonLoader文件夹、version.dll、Mods文件夹。这些是我们完全可以动的部分。
> 其他的完全不允许动。"

**可动范围 = 精确三项，不多不少：**

1. `C:\Program Files (x86)\Steam\steamapps\common\Last Epoch\MelonLoader\`（整个文件夹）
2. `C:\Program Files (x86)\Steam\steamapps\common\Last Epoch\version.dll`
3. `C:\Program Files (x86)\Steam\steamapps\common\Last Epoch\Mods\`（整个文件夹）

**游戏安装目录内的其他任何内容，一律不允许动。**

## 边界明细

### ✅ 可动

| 路径 | 归属 | 备注 |
|---|---|---|
| `MelonLoader\` | Mod 载体 | `net6\`、`Dependencies\`、`Il2CppAssemblies\`、`Logs\`、`Documentation\` 等全部 |
| `version.dll` | MelonLoader 引导代理 | 由 `MelonLoader.x64.zip` 释放（11057664 字节） |
| `Mods\` | Mod 存放处 | 含 `LEns.dll`、`AffixAbbrev.tsv` 与自研 Mod |

### ❌ 禁止改动

| 路径 | 归属 |
|---|---|
| `GameAssembly.dll`、`Last Epoch.exe`、`UnityPlayer.dll`、`UnityCrashHandler64.exe` | 游戏本体 |
| `baselib.dll`、`dstorage.dll`、`dstoragecore.dll`、`D3D12\`、`Licenses\` | 游戏本体 |
| `Last Epoch_Data\`（含 `global-metadata.dat`、`build_hash.txt`） | 游戏数据 |
| `Plugins\`、`UserLibs\`、`UserData\` | 同属 Mod 生态，但**未授权** → 不动（可读） |
| 游戏目录内新建任何目录 | —— |

> 注：`Plugins\`、`UserLibs\`、`UserData\` 虽然也是 Mod 相关目录，但用户只列了三项，
> 因此它们**不在**我们的可动范围内。只读访问可以。

### 判断口诀

> 写文件前自问三句：
> 1. 路径是否以 `…\Last Epoch\MelonLoader\` 开头？
> 2. 是否恰好等于 `…\Last Epoch\version.dll`？
> 3. 是否以 `…\Last Epoch\Mods\` 开头？
>
> **三者皆否 → 不许写。**

### 工作区（游戏目录之外）

所有分析产物、笔记、源码、构建输出一律放：

```
C:\Users\tomas\Documents\LastEpoch_Mod_Workspace\
```

曾误在游戏目录内创建 `_dsh_backup_20261001_235451\` 与 `contrib\`，两者均已迁出/删除。

## ⚠️ `MelonLoader\Il2CppAssemblies\` 的特殊性

该目录是 Cpp2IL / Il2CppInterop 的**运行时生成物**。MelonLoader 在检测到
`GameAssembly.dll` 哈希变化时会**清空并按 `cpp2il_out` 重新生成**整个目录。

因此在此目录内做的修补：
- ✅ 属于可动范围，允许做
- ⚠️ 但**会被下次游戏更新覆盖** → 必须配套"重生成后自动重新应用"的机制

## CoreModule 问题：属于我们的责任范围

`UnityEngine.CoreModule.dll` 加载即抛：

```
BadImageFormatException: Duplicate type with name '<>O'
```

**根因**（已实证，非推测）：

- Unity 6000.4.8 使用 IL2CPP 元数据 **v39**
- Cpp2IL `2022.1.0-pre-release.21`（**已是 GitHub 上的最新版**，之后无更新）
  在该元数据下**不生成 `NestedClass` 表**（实测行数 = 0）
- 于是 `<>O`、`<>c`、`BindingsMarshaller` 等**嵌套**类型塌缩为同名的**顶层**类型
- 它们的可见性标志仍是 `NestedPublic` / `NestedPrivate`，但没有任何 `NestedClass`
  记录把它们挂到父类型上 → CLR 判定元数据非法

**实测范围（用自建 LoadProbe 逐程序集加载验证）：**

| 指标 | 结果 |
|---|---|
| `Il2CppAssemblies\` 程序集总数 | 213 |
| **加载失败** | **1**（仅 `UnityEngine.CoreModule.dll`） |
| 加载成功 | 212 |
| CoreModule 中冲突 TypeDef 行 | 201 行 / 68 组 |
| 其中被 TypeRef 引用 | **0** |
| 其中被 ExportedType 引用 | **0** |
| 冲突类型所在命名空间 | 全部为全局命名空间 `''` |

> 早前"84 个程序集损坏"的说法**已作废**——那是按"重名组数 > 0"统计得出的，
> 未经实际加载验证。其余 83 个含重名的程序集均**能正常加载**（其重名是合法嵌套类型）。
> 正确口径以 LoadProbe 的实测结果为准。

**修复方案**：对这些无人引用的重复顶层类型做元数据重命名。
难点在于 `#Strings` 堆**零剩余空间**（紧随其后就是 `#US`，且无空隙），
所以新名字**不得长于原名字**，需要巧妙的命名方案。

工具：`tools/fix_coremodule.py`（在游戏目录外先验证，再部署）。
现象与调查过程详见 `notes/ISSUE_CoreModule_duplicate_type.md`。

## 工作区结构

```
LastEpoch_Mod_Workspace\
├── PRINCIPLES.md              <- 本文件
├── notes\
│   └── ISSUE_CoreModule_duplicate_type.md
├── LEnsAnalysis\              <- LEns.dll 反编译结构清单
│   ├── extract.py             <- 自写 ECMA-335 解析器（无需 dotnet SDK）
│   ├── make_report.py
│   ├── LEns_inventory.json
│   ├── LEns_inventory.tsv     <- 2161 行成员表
│   └── LEns_inventory.md      <- 8 章节分析报告
├── tools\
│   ├── diagnose_typedef_dupes.py   <- TypeDef 重名诊断
│   ├── check_nesting.py            <- 判定重名是合法嵌套还是非法顶层
│   ├── show_dupe_detail.py         <- 单类型明细（arity/可见性/嵌套）
│   ├── LoadProbe.cs / .dll         <- 实证：逐个程序集尝试 CLR 加载
│   ├── fix_coremodule.py           <- 重复类型名修复器
│   └── loadprobe_baseline.txt      <- 修复前基线清单
└── HybridMod\                 <- 自研混合 Mod（C# 宿主 + Rust 核心）
    ├── RustCore\              <- 已可编译，产出 lens_core.dll
    └── CSharpMod\             <- 待完成
```

## 部署前自检

1. 确认待写路径命中"判断口诀"三问之一。
2. 一切改动先在**工作区副本**上验证通过，再考虑部署。
3. 部署到 `Mods\` 或 `MelonLoader\` 前，先向用户说明改了什么。
