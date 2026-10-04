# lens_core —— LEns 纯计算部分的 Rust 移植

把 LEns 里**与游戏无关的纯计算**移植成 Rust，用 `cargo test` 验证正确性。
这部分**不受游戏版本更新影响**，是混合方案（C# 宿主 + Rust 核心）里真正能长期稳定的部分。

## 状态

| 项目 | 状态 |
|---|---|
| `cargo test` | ✅ **40 passed / 0 failed** |
| release 构建 | ✅ `lens_core.dll` 129.5 KB |
| 导出符号 | ✅ 21 个 `lens_*` C ABI 函数 |
| 部署 | ❌ 未部署（纯计算库，等 C# 宿主接上后再一起部署） |

## 一、词缀表解析（`LensAffixTable`）

解析 `Mods\AffixAbbrev.tsv`，建立 `affixId ↔ 缩写` 双向查找。

**真实数据验证**（不是我自己编的样本）：测试用 `include_str!` 方式在编译期嵌入真实的
`AffixAbbrev.tsv`，断言：

```
数据行    = 1112   （精确断言）
跳过行    = 50     （49 条注释/空行 + split('\n') 产生的尾部空串）
条目 1 的缩写是非 ASCII（中文）→ 证明 UTF-8 跨边界未损坏
```

> 这里踩了一个坑值得记录：我最初断言"跳过 49 行"，测试失败报 50。
> 原因是 **PowerShell 的 `Get-Content` 不计末尾空行**，而 `split('\n')` 会多产生一个空串。
> **解析器是对的，我的测量方式错了**——已修正断言并在代码里注明。

导出接口：

| 函数 | 作用 |
|---|---|
| `lens_affix_table_new(ptr, len)` | 解析并返回不透明句柄；失败返回 null |
| `lens_affix_table_free(handle)` | 释放；传 null 是空操作 |
| `lens_affix_table_len / _skipped` | 数据行数 / 跳过行数 |
| `lens_affix_table_name_at(idx)` | 按序号取缩写（返回 NUL 结尾 C 字符串） |
| `lens_affix_table_name_for_id(id)` | 按 affixId 取缩写 |
| `lens_affix_table_id_for_name(ptr,len,out)` | 按缩写取 affixId |
| `lens_core_parse_affix_tsv(ptr,len)` | 只计数，不建表 |

## 二、DPS 计算（`DpsCalculator`）

这是本次的重点：**忠实移植 LEns 的 `DpsStatsCalculator`**，规则逐条从
`Mods\LEns.dll` 的 IL 读出（用 `tools/InspectMethod`），不是我自己发明的聚合方式。

### 原始算法（IL 依据）

```
AddSample(sample)
    if !(sample.Amount > 0) return          // cgt.un 对 0，NaN 也被拒
    if sample.IsIncoming {                  // 承伤单独记录，【不进】DPS 时间线
        _maxIncomingDamage = max(_maxIncomingDamage, sample.Amount)
        return
    }
    t = sample.Time                          // 样本自带时间戳（不是 Now）
    _timeline.Enqueue(DamagePoint(t, sample.Amount))
    if (_firstDamageTime < 0) _firstDamageTime = t
    _lastDamageTime = t
    _totalDamage += sample.Amount
    _hitCount++
    if sample.IsCrit _critCount++

TrimExpiredPoints()                          // while，不是 if
    while Count > 0 && (Now - Peek().Time) >= window: Dequeue()

TriangularWeightedSum()                      // 三角权重：越新权重越高
    if Count == 0 || !(window > 0) return 0.0
    k = 1.0 / window
    for p in _timeline:
        age = Now - p.Time
        if age < 0 || !(age < window) continue    // 两个守卫都关键
        sum += p.Amount * (1.0 - age * k)
    return sum

CurrentDps => 2.0 * TriangularWeightedSum() / window

GetSnapshot()
    CritRate = _hitCount > 0 ? _critCount / _hitCount : 0.0
    CombatDurationSeconds = _firstDamageTime < 0 ? 0.0 : Max(0, _lastDamageTime - _firstDamageTime)
```

**我自己绝对猜不到的细节**：`CurrentDps` 里那个 `2.0` 因子，以及权重
`1 - age/window`（三角而非矩形）。这两点决定了数值是否与原版一致。

### 一处**刻意的**偏差（已文档化）

原版 `Now => Time.realtimeSinceStartup - _sessionEpoch`，读的是 Unity 时钟。
纯计算核心不应依赖游戏引擎，所以改为**由宿主传入 `now`**：

| 原版 | 本移植 |
|---|---|
| `new DpsStatsCalculator(w)` | `DpsCalculator::new(w, epoch)` |
| `Reset()` 内部读游戏时钟 | `reset(now)` |
| `Now => realtimeSinceStartup - _sessionEpoch` | `now` 由调用方给 |

**这里我犯过一个真实 bug，被测试抓出来了**：我一开始让 `now()` 返回
`now - session_epoch`（转成相对时间），但样本时间却按绝对值传入，导致 `age` 计算
**双重减去了 epoch**，结果为负数被 `age < 0` 守卫全部丢弃，DPS 恒为 0。

调试输出是决定性的：`DBG len=1 epoch=100 now(103)=3 tws(103)=0`。

修正：统一用**同一时钟**（绝对时间），`age = now - sample.time`，不做二次转换。
现在有一条专门的回归测试 `age_is_the_plain_difference_between_two_times_on_one_clock` 锁住它。

### 与 C# 的一致性细节（都有测试锁定）

- `NaN` 与负数被拒（`!(Amount > 0)` 的语义）
- **`+∞` 会被接受**——因为 `inf > 0` 为真，与原版一致。我一开始写测试时"想当然"地
  认为应该拒绝无穷大，是我错了，已按原版行为修正并注明
- `age == window` 的点**不计入**（`!(age < window)`）
- 比 `now` 更新的点（`age < 0`）**不计入**
- `trim` 是 **while 循环**，一次要清干净（有测试证明单个 `if` 会漏掉 9 个）
- 裁剪**不影响**累计总量 `total_damage`（总量是会话累计，与窗口无关）
- `Reset()` **保留** `window_seconds`，只清计数器并重置 epoch

导出接口：

| 函数 | 作用 |
|---|---|
| `lens_dps_new(window, epoch)` | 创建计算器 |
| `lens_dps_add_sample(handle, sample)` | 喂入一个伤害事件 |
| `lens_dps_trim(handle, now)` | 丢弃过期点 |
| `lens_dps_snapshot(handle, now, out)` | 读快照（失败时也保证 out 已清零） |
| `lens_dps_reset(handle, now)` | 清空并重置 epoch |
| `lens_dps_timeline_len(handle)` | 当前时间线点数 |
| `lens_dps_free(handle)` | 释放 |

## 三、跨边界约定

- 所有入口都是 `#[no_mangle] pub extern "C"`，只用 C 兼容类型
- 每个函数都有防护：空指针、越界长度、非法 UTF-8、越界索引都返回**文档化的哨兵值**，
  绝不崩溃
- **panic 绝不跨越 FFI 边界**（`catch_unwind`，`panic-guard` feature 默认开启）——
  否则会直接带崩游戏进程
- 结构体布局用测试锁死，防止与 C# 侧声明不一致：
  - `LensDamageSample` = 24 字节，对齐 8
  - `LensDpsSnapshot` = 56 字节，对齐 8
- 返回字符串的缓冲区**显式 NUL 结尾**——Rust `String` 的缓冲区不以 NUL 结尾，
  直接返回 `as_ptr()` 会让 marshaller 走进堆后面的垃圾字节

## 编译与测试

```powershell
cd <工作区>\HybridMod\RustCore

# 测试（带真实词缀表验证）
$env:LENS_REAL_TSV = 'C:\Program Files (x86)\Steam\steamapps\common\Last Epoch\Mods\AffixAbbrev.tsv'
cargo test --lib

# release 构建
cargo build --release      # -> target\release\lens_core.dll
```

> 未设置 `LENS_REAL_TSV` 时，真实数据那条测试会**跳过**而不是失败，
> 保证没有游戏文件的环境也能构建。

## 尚未完成

- **C# 宿主尚未接上**：`lens_affix_table_new` / `lens_dps_*` 还没有被任何 C# 代码调用。
  需要写 `[DllImport("lens_core")]` 声明与调用点。
- **未部署**：`lens_core.dll` 需与 C# 宿主 Mod 一起放进 `Mods\`。
- **端到端未验证**：测试证明的是"Rust 侧逻辑正确"，不是"游戏里数值与原版一致"。
  真正对账需要在游戏里同时跑原版 LEns 与本实现，比对同一场战斗的 DPS 输出。
