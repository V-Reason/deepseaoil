---

> 🔴 **2026-10 更新：本文件第 1、4、6 节的字段清单已被「战斗与元素反应重构」取代。**
> 温湿度电三轴、`tile_effect` 效果档位表、`TileEffectValue` 多槽位结构体**已物理删除**。
> 现在各表的确切列与语义见：
> - `Docs/策划案_荒原复苏指南_v4.0.md` 第 8 节（配置契约附录，含全部列名速查）
> - `Docs/表格数据配置/技术文档_配表管线.md` 第 2 节（9 张表逐表消费者）
>
> **下文保留的是"划界原则"（Excel 放什么 / SO 放什么 / 资产怎么寻址）与已经被执行的部分**，
> 第四节「美术资源加载规约」仍然完全有效。

### 一、划分原则（第一性原则）

为了让策划不再面对无意义的物理常数，同时让程序在 Unity Editor 调手感时无需反复导表，确立以下**铁律**：

1. **Excel（策划驱动·核心玩法与规则网）**：
   - **只放**：核心结算数值（HP / 伤害 / 每波配给种子）、元素反应网（`element_rule` / `element_duo_reaction`）、地块生效节奏（存活秒数 / 移速倍率 / 每秒伤害）、波次节奏（三阶段时长）。
   - **严禁**：出现相机深度、像素尺寸、闪白频率、物理加速度、击退衰减曲线、资源物理路径。
   - **现状核对**：`tile_state` 的 `slow_rate`（移速倍率）是**消费者语义**（属性乘以倍率），不是物理加速度；
     `element_rule` 的 `impact_knockback` 用的是**格数**（冲量由执行者按格边长折算），
     所以"击退衰减率"这类手感曲线仍然在 SO（`EnemyTuning.knockbackDecay`）里。
2. **ScriptableObject（程序驱动·物理手感与视效体验）**：
   - **只放**：刚体运动学参数（加速度/转向衰减/最大外力）、投掷抛物线（弧高/时长/相机深度）、接触检测物理裕量、颜色调色板。
   - **收益**：Play Mode 下修改即时生效，调手感零编译、零导表成本。
3. **美术资产寻址（程序装配·`AssetModule.Load` 懒加载）**：
   - **禁止**在 Excel 里让策划手抄 `Resources\Icons\...` 路径（易错且无校验）。
   - **约定寻址**：美术按命名规范给图，程序按枚举统一通过 `AssetModule.Load<T>($"tiles/Tile_{state}")` 按需懒加载，进缓存池，切场景自动走 LRU 释放，绝不启动全量加载。
   - **唯一例外**：`seed.icon_key` 是**登记式 Key**（`Icons/Seed_Fire` 这种短 Key，不是 Windows 路径）。
     当前无读取点，接 HUD 图标时它会成为消费者 —— 这是刻意留的接口，不是漏删。

---

### 二、各表格字段裁剪与划界方案（一锤定音）

#### 1. `projectile.xlsx`（投掷资源表）

已完成：4 个抛物线字段已移出至 SO（`ThrowTuning.asset`）。
**本轮进一步收缩**：元素三数值与标签位已删除，球种只剩两种。

| 字段 | 类型 | 说明 |
| :-- | :-- | :-- |
| `id` | `BallType` | 球种枚举（**只剩 `Water` 纯水 / `Earth` 纯土**） |
| `name` | `string!` | 显示名 |
| `type` | `ElementType` | 大类标记（水 / 土）；只作语义标记，不参与落地判定 |

> 落地结果由 `element_rule` 查表决定，球本身不再携带任何"地形倾向"。

#### 2. `player.xlsx`（玩家配置表）
- **当前痛点**：策划案 3.0 已锁定为“3 颗心，归零即死”。表里填 `100` 血、`10` 伤害已脱节；且 `knockback_impulse`, `knockback_speed_limit`, `contact_radius` 全是刚体物理参数。
- **划界改动**：
  - **移出至 SO (`PlayerConfig.asset`)**：`knockback_impulse`（击退冲量）、`knockback_speed_limit`（受击限速）、`contact_radius`（身体接触判定半径）、`retry_delay`（死亡复活等待）。
  - **保留在 Excel**（建议仅留核心规则）：
    | 字段                    | 类型     | 说明                               |
    | :---------------------- | :------- | :--------------------------------- |
    | `id`                    | `int`    | 编号（固定为 1）                   |
    | `name`                  | `string` | 玩家标识                           |
    | `max_hp`                | `float`  | 锁定为 **3**（策划案 3.0：3 颗心） |
    | `invulnerable_duration` | `float`  | 受击无敌时长（秒，防连续暴毙）     |
    | `attack_interval`       | `float`  | 投掷间隔/CD（秒）                  |

#### 3. `enemy.xlsx`（敌人配置表）
- **落地状态（2026-10-09，已完成）**：`radius` / `acceleration` / `knockback_decay` / `stop_distance` / `chase_range` 已移出表、进 `EnemyTuning`（`Assets/Resources/tuning/EnemyTuning.asset`，`ConfigModule.BindAssets` 取不到即抛 `ConfigLoadException`）；表里只剩 **`id` / `name` / `max_speed` / `hp`**。`flash_hz` 更早已归 `VisualPalette`，`stun_seconds` 已删列。
- **尚未落地**：`contact_damage` 列还没加 —— 接触伤害目前仍由 `player.contact_damage` ＋ `player.contact_radius` 在 `CombatRoot.UpdatePlayerContact` 里结算；要按怪种区分伤害时才搬（见 `Docs/待办.md`）。
- **当前痛点**：`flash_hz`（受击闪烁频率）是纯表现；`acceleration`、`knockback_decay` 是运动学控制律；`stun_seconds` 代码已废弃。
- **划界改动**：
  - **移出至 SO (`EnemyTuning.asset` & `VisualPalette.asset`)**：
    - 移入 `VisualPalette`：`flash_hz`（闪烁频率）。
    - 移入 `EnemyTuning`：`radius`（碰撞半径）、`acceleration`（加速度）、`knockback_decay`（受击滑停衰减）、`stop_distance`（停止逼近距离）、`chase_range`（脱战距离）。
  - **保留在 Excel**（支持普通怪与精英怪两行）：
    | 字段             | 类型     | 说明                                           |
    | :--------------- | :------- | :--------------------------------------------- |
    | `id`             | `int`    | 1=普通怪，2=精英怪                             |
    | `name`           | `string` | 普通怪 / 精英怪                                |
    | `hp`             | `int`    | 耐久（策划案 3.0：普通怪 **3**，精英怪 **6**） |
    | `max_speed`      | `float`  | 追击极速（策划调追逐压迫感）                   |
    | `contact_damage` | `float`  | 接触伤害（固定为 1）                           |

#### 4. `tile_state.xlsx`（地块状态表）

已完成：`icon` / `willSpread` 已剔除（贴图走 `tiles/Tile_{Id}` 约定）。
**本轮重写**：地形元素四件套与效果清单全部删除，改为「地面残留」四项 + 导通标记。

| 字段 | 类型 | 说明 |
| :-- | :-- | :-- |
| `id` | `TileStateType` | 地块状态枚举（16 项） |
| `name` | `string!` | 显示名（HUD / 诊断面板读数） |
| `duration` | `float` | 存活秒数（-1 为永久） |
| `slow_rate` | `float` | 踩在上面的移速倍率（1 = 不减速） |
| `dot_damage` | `int` | 每秒伤害（0 = 无伤害） |
| `is_obstacle` | `bool` | 物理阻挡墙体 |
| `is_conductor` | `bool` | **网格连锁导通体**（水·土·泥浆·稀泥为真） |

> **权责边界**：这张表只管"留在地上之后"。**落地那一下的冲击归 `element_rule`**，
> 两张表不重叠、不互斥 —— 不再有"规则表有效果就顶掉地块表效果"的补丁逻辑。

#### 5. `wave.xlsx`（波次配置表）

已完成：`spawn_radius` **没有**移出（刷怪环半径留在表里，它决定"怪从多远压过来"，属玩法节奏）。
**本轮重写为三阶段时钟 + 战备配给**：

| 字段 | 类型 | 说明 |
| :-- | :-- | :-- |
| `id` | `int` | 波次编号（1, 2, 3...） |
| `name` | `string!` | 显示名（HUD 预警文案） |
| `prep_time` | `float` | 备战时长（默认 10s）：发配给、让玩家铺地形 |
| `battle_time` | `float` | 战斗时长（默认 30s）：按 `spawn_interval` 出怪 |
| `settle_time` | `float` | 结算时长（默认 8s）：**必须等残余敌人清空**才推进 |
| `enemies_per_wave` | `int!` | 本波怪量 |
| `spawn_interval` | `float!` | 刷怪间隔（秒），下限 0.05 |
| `spawn_radius` | `float!` | 出生环半径 |
| `grant_seed` | `SeedType` | **本波备战期配给的战备种子**（可留空 = 不发） |

> 跑完全表后**循环回第一行**并继续递增波次号（无尽模式），没有"通关"终态。

#### 6. `element_rule.xlsx`（一级反应）与 `element_duo_reaction.xlsx`（二级反应）

**判定：100% 留在 Excel。这是策划的核心资产。** 本轮**重写了表结构**（不是"保持原状"）：

`element_rule.xlsx` — 球砸地面：

| 字段 | 类型 | 说明 |
| :-- | :-- | :-- |
| `id` | `int` | 主键 |
| `source_tile` | `TileStateType` | 作用的地块原状态 |
| `ball_type` | `BallType` | 投掷的球种 |
| `result_tile` | `TileStateType` | 生成的新地貌（等于原格 = 地形不变、只结算冲击） |
| `impact_damage` | `int` | **落地瞬间伤害，当帧结算** |
| `impact_knockback` | `float` | 落地瞬间击退（格） |
| `impact_stun` | `float` | 落地瞬间麻痹（秒） |
| `trigger_chain` | `bool` | 是否触发网格连锁泛洪 |

> 🔴 **业务唯一键是 `(source_tile, ball_type)`**：`ReactionResolver` 按它建哈希表做 O(1) 查询，
> 重复配置会被当场 `LogError` 报出（后者不算数）。`priority` 列**已删除** ——
> 它曾经叫"优先级"而实际匹配顺序由表内行序决定，是纯误导。
> `impact_stun` **不要加 `!`**：它有 8 行天然是 0，加 `!` 会导表失败。

`element_duo_reaction.xlsx` — 地面 × 地面（新表）：

| 字段 | 类型 | 说明 |
| :-- | :-- | :-- |
| `id` | `int` | 主键 |
| `elem_a` / `elem_b` | `TileStateType` | 两个元素发生器地貌（**无序对**） |
| `result_tile` | `TileStateType` | 激发出的地貌 |
| `impact_damage` | `int` | 瞬发伤害 |
| `impact_knockback` | `float` | 瞬发击退（格） |
| `result_duration` | `float` | 结果存续秒数 |
| `effect_radius` | `float` | 波及半径（格，欧氏距离） |
| `trigger_chain` | `bool` | 是否沿网格继续泛洪 |

> `(elem_a, elem_b)` 与 `(elem_b, elem_a)` 是同一条：`DuoReactionCatalog` 建**双向字典**，
> 重复配置同样当场报错。

**已退役**：`tile_effect.xlsx`（效果多档参数表）。效果不再是"效果号 + 档位"，
而是直接写在 `element_rule`（落地）与 `tile_state`（残留）两张表里。

---

### 三、ScriptableObject 承接体系（程序调参盘）

在 `Assets/Scripts/Data/Settings/` 下，程序持有以下 SO 文件（均在 `Resources/tuning/`）：

1. **`PlayerConfig.cs`**：
   - 包含：移动速度 `moveSpeed`、8向吸附 `snapToEightDirections`、冲刺参数 `dash*`、受击物理反馈 `knockbackImpulse`、`knockbackSpeedLimit`、接触检测 `contactRadius`。
2. **`ThrowTuning.cs`**：
   - 包含：抛物线基准时长 `flightDuration`、弧高 `arcHeight`、投掷最大/小距离、瞄准环压扁比例与透视。
3. **`EnemyTuning.cs`**（建议将敌人运动参数收口于此）：
   - 包含：碰撞半径 `radius`、加速度 `moveAcceleration`、受击滑停衰减 `turnDecayRate`、停止逼近距离 `stopDistance`、脱战距离 `chaseRange`。
4. **`VisualPalette.cs`**：
   - 包含：受击闪烁频率 `flashHz`、球种颜色、敌人 4 态颜色、高亮提示色。

---

### 四、美术资源加载规约（零常驻，动态按需）

针对“美术只管出图，程序做配置，杜绝全量进内存”的要求，制定以下统一调用通道：

```csharp
// 规则：所有 Tile 资源统一放于 Assets/Resources/tiles/ 目录下，命名为 Tile_<StateEnum>.asset
public TileBase ResolveTile(TileStateType state)
{
    string key = $"tiles/Tile_{state}";
    
    // AssetModule 内部维护了 CacheStore 与 RefCounter：
    // 首次调用从 Resources 加载并缓存；如果资源不存在，返回 null，绝不抛异常
    TileBase tile = AssetModule.Load<TileBase>(key);
    
    if (tile == null)
    {
        // 兜底提示：告知程序或美术缺图，但不打断游戏运行
        Debug.LogWarning($"[Grid] 未找到地块资源：{key}，将使用临时占位处理。");
    }
    return tile;
}
```
- **内存安全**：
  - 只有在战场中**真正出现**的地块状态，才会触发 `AssetModule.Load` 加载其 Sprite/Tile。
  - 关卡结算/切换场景时，`SceneService` 会触发 `AssetModule.OnSceneSwitch()`，未被引用的 Tile 自动进入冷却并按 LRU 释放，内存峰值恒定。

---

### 五、落地执行动作与排期

既然要“一锤定音”，这一步的修改将分两动完成：

1. **第 1 动（表格轻量瘦身与 Luban 生成）**：
   - 在 Excel 中剔除 `projectile` 的 4 列物理参数、`tile_state` 的 `icon`/`willSpread` 列、`enemy` 的 `flash_hz`/`stun_seconds` 列。
   - 跑一次 Luban 导表，使数据定义真正干净。
2. **第 2 动（代码 Spec 适配）**：
   - 修改 `ProjectileSpec.cs`、`PlayerSpec.cs`、`EnemySpec.cs`，将其移出的字段重定向至对应的 Tuning SO。
   - 改造 `TilemapAdapter.cs`，接入 `AssetModule.Load<TileBase>` 按需加载。
