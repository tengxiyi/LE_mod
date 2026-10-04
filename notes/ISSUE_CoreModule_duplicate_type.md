# 问题记录：Unity 6000.4 下 `UnityEngine.CoreModule` 无法加载 —— 已找到可用修复

状态：**已定位根因、已找到可用修复、已在工作区验证通过；尚未部署到 MelonLoader**
记录时间：2026-10-02
游戏版本：Steam build `25646802`，Unity **6000.4.8f1**，IL2CPP 元数据 **v39**

## 现象

游戏 2026-10-02 00:09 更新到 Unity 6000.4.8f1 后，MelonLoader 自动重新生成程序集
（成功，215 个文件），但加载 Mod 依赖链失败：

```
[ERROR] Loading Melon Dependency Failed: System.BadImageFormatException:
  Could not load file or assembly 'UnityEngine.CoreModule, Version=0.0.0.0, ...'
  ---> System.BadImageFormatException: Duplicate type with name '<>O' in assembly
       'UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'.
       at MelonLoader.InternalUtils.DependencyGraph`1.TryLoad(...) DependencyGraph.cs:line 143
```

随后 Mod 报缺失 `UnityEngine.CoreModule`，最终 `[ERROR] No Support Module Loaded!`。

## 影响范围（用自建 LoadProbe 逐程序集实测）

| 指标 | 结果 |
|---|---|
| `Il2CppAssemblies\` 程序集总数 | 213 |
| **加载失败** | **1 —— 仅 `UnityEngine.CoreModule.dll`** |
| 加载成功 | 212 |

> ⚠️ 早期"84 个程序集损坏"的说法**作废**。那是按"重名组数 > 0"统计，未经加载验证。
> 其余 83 个含重名的程序集**都能正常加载**。

## 关键更正：我最初对根因的判断是错的

早期我用自写的 ECMA-335 解析器得出"`NestedClass` 表 0 行、201 个嵌套类型塌缩成同名顶层、
必须改名"——**这个结论是错的，错在我的解析器**。

用 Mono.Cecil（成熟库）读取**同一个原始文件**，真实情况是：

| 指标 | 原始文件（Cecil 读取） |
|---|---|
| 顶层类型 | 1092 |
| 全部类型（含嵌套） | **3188**，其中**嵌套类型确实存在** |
| **同 FullName 重名** | **0** |
| **CLR 标识（命名空间+名+arity）重名** | **0** |
| 顶层类型使用 `Nested*` 可见性 | **0** |

也就是说：**元数据里的类型结构本来是正确的**，`<>O`、`<>c` 等都是正常的嵌套类型。
我的解析器读 `NestedClass` 表读错了（`NestedClass` 确实是 0 行，但那是**读取错误**，
不是文件问题），于是把一个不存在的"重名"当成了根因。

**教训**：用自写的二进制解析器下结论前，必须先与成熟库（Mono.Cecil）对账。

## 真正的缺陷与修复

缺陷本身仍然存在——否则 CLR 不会报错——但它不在"类型重名"上，而在
**Cpp2IL/Il2CppInterop 生成的这份特定二进制里某处让 CLR 的元数据校验失败**。
这与我实测到的上游报告一致：社区成员 V1ndicate1 在
[MelonLoader #1142](https://github.com/LavaGang/MelonLoader/issues/1142#issuecomment-4288803628)
指出是 "Il2CppInterop's unstripper" 在生成阶段写入了有问题的 `UnityEngine.CoreModule.dll`。

### 修复方法：用 Mono.Cecil 做一次读写往返

```csharp
var asm = AssemblyDefinition.ReadAssembly(input);
asm.Write(output);          // Cecil 重建元数据
```

**仅此而已**——不需要改名、不需要改可见性、不需要手写 PE 手术。
Cecil 重建出的元数据 CLR 就能接受。

## 验证结果（全部在工作区完成，游戏目录只读）

| 测试 | 结果 |
|---|---|
| 原始文件加载 | ❌ `Duplicate type with name '<>O'` |
| Cecil 往返后加载 | ✅ **LOADED OK** |
| 往返保真度（Cecil 读数对比） | ✅ 1092 / 3188 / 169 / 0 冲突 **逐项一致** |
| 运行时可见重名（CLR 实测） | ✅ **0** |
| 类型可用性 | ✅ 2630 个类型加载，含 677 个嵌套类型、373 个枚举 |
| **全部 213 个程序集整体加载** | ✅ **213/213 OK**（替换修复版 CoreModule） |

> 说明：保真度验证中有 558 个类型未加载，原因是我的探针环境缺少
> `Il2Cppmscorlib` 的跨程序集解析（这些类型的基类来自它），**与修复无关**；
> 在实际 MelonLoader 环境里依赖链是完整的。

## 上游确认

- [MelonLoader #1142](https://github.com/LavaGang/MelonLoader/issues/1142) —— 另一款游戏
  （Data Center, Unity 6000.4.2f1）出现**完全相同**的错误。closed (duplicate)。
- [MelonLoader #1148](https://github.com/LavaGang/MelonLoader/issues/1148) —— 报告者试过
  **0.7.2 与 nightly 0.7.3-ci.2497 都失败**。closed (Invalid)。
- [V1ndicate1/FixCoreModule](https://github.com/V1ndicate1/FixCoreModule) —— 社区修复工具
  （MIT，C#/.NET 8，用 Mono.Cecil 处理，自动检测 Steam 库，带 `.bak`）。

Cpp2IL 当前 `2022.1.0-pre-release.21` 已是 GitHub 最新版，之后无更新，
所以"升级工具链"暂时走不通。

## 工具

| 工具 | 作用 |
|---|---|
| `tools/FixCoreModuleCecil.cs` / `.dll` | **修复器**：Mono.Cecil 读写往返 |
| `tools/LoadProbe.cs` / `.dll` | 用 `AssemblyLoadContext.LoadFromAssemblyPath` 逐个实测加载 |
| `tools/VerifyFixed.cs` / `.dll` | 加载并确认类型可按名解析、无运行时重名 |
| `tools/CecilReport.cs` / `.dll` | 用 Mono.Cecil 报告类型结构（对账用） |
| `tools/diagnose_typedef_dupes.py` | ⚠️ 自写解析器，**已知会误读 `NestedClass`，结论不可信**，仅留作排错参考 |
| `tools/check_nesting.py`、`show_dupe_detail.py` | 同上，依赖上面的解析器 |
| `tools/loadprobe_baseline.txt` | 修复前基线（1 个失败） |

## 部署与注意事项

**部署方式**：把修复后的 `UnityEngine.CoreModule.dll` 覆盖到
`MelonLoader\Il2CppAssemblies\`（属可动范围），建议先备份原文件。

**⚠️ 会被覆盖**：该目录是 MelonLoader 的生成物，游戏更新（`GameAssembly.dll`
哈希变化）时 MelonLoader 会**清空并重新生成**，修复需要重新应用。
因此建议把修复做成"生成后自动应用"的步骤，而不是一次性手工操作。

**回归风险**：Cecil 往返重建了元数据，理论上 MethodDef RVA、自定义特性、
`MethodAddressToToken.db` 关联等都可能受影响。静态验证已全部通过，
但**最终必须在游戏内实测**才能确认 Il2CppInterop 运行时解析正常。
