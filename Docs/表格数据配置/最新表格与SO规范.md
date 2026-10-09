---

### 一、划分原则（第一性原则）

为了让策划不再面对无意义的物理常数，同时让程序在 Unity Editor 调手感时无需反复导表，确立以下**铁律**：

1. **Excel（策划驱动·核心玩法与规则网）**：
   - **只放**：关卡结算数值（HP/伤害）、元素反应网（温/湿/电/Tag）、地块生效节奏（持续时间/触发间隔）、波次节奏。
   - **严禁**：出现相机深度、像素尺寸、闪白频率、物理加速度、击退衰减曲线、资源物理路径。
2. **ScriptableObject（程序驱动·物理手感与视效体验）**：
   - **只放**：刚体运动学参数（加速度/转向衰减/最大外力）、投掷抛物线（弧高/时长/相机深度）、接触检测物理裕量、颜色调色板。
   - **收益**：Play Mode 下修改即时生效，调手感零编译、零导表成本。
3. **美术资产寻址（程序装配·`AssetModule.Load` 懒加载）**：
   - **禁止**在 Excel 里让策划手抄 `Resources\Icons\...` 路径（易错且无校验）。
   - **约定寻址**：美术按命名规范给图，程序按枚举统一通过 `AssetModule.Load<T>($"tiles/Tile_{state}")` 按需懒加载，进缓存池，切场景自动走 LRU 释放，绝不启动全量加载。

---

### 二、各表格字段裁剪与划界方案（一锤定音）

#### 1. `projectile.xlsx`（投掷资源表）
- **当前痛点**：`flight_duration`, `max_height`, `max_throw_distance`, `min_throw_distance` 4 个字段在 11 行里全填了 `0.6, 2, 5, 0.4`。策划根本不调这个，这纯粹是抛物线手感。
- **划界改动**：
  - **移出至 SO (`ThrowTuning.asset`)**：`flight_duration`（基准飞行时长）、`max_height`（弧高）、`max_throw_distance`（射程）、`min_throw_distance`（最小起掷距）。
  - **保留在 Excel**：
    | 字段                          | 类型          | 说明                                                  |
    | :---------------------------- | :------------ | :---------------------------------------------------- |
    | `id`                          | `BallType`    | 球种枚举（纯水/热水/寒水/干土/湿土/沙/粘土/各类种子） |
    | `name`                        | `string`      | 显示名称                                              |
    | `type`                        | `ElementType` | 大类（水 / 土 / 种子）                                |
    | `temp` / `wet` / `conductive` | `int`         | 元素三数值（-6~6 / 0~6 / 0~2）                        |
    | `tags`                        | `ElementTag`  | 标签位（含土/含沙/含植物等）                          |

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
- **当前痛点**：`icon` 填的是 Windows 相对文件路径，策划难记且运行期没用；`willSpread` 标记代码明确“本轮只读不做”。
- **划界改动**：
  - **彻底剔除字段**：
    - 删掉 `icon`：贴图走程序约定动态加载 `AssetModule.Load<TileBase>($"tiles/Tile_{Id}")`。
    - 删掉 `willSpread`：本轮不做蔓延，避免策划误配。
  - **保留在 Excel**：
    | 字段                             | 类型            | 说明                         |
    | :------------------------------- | :-------------- | :--------------------------- |
    | `id`                             | `TileStateType` | 地块状态枚举                 |
    | `name`                           | `string`        | 显示名                       |
    | `duration`                       | `float`         | 持续秒数（-1 为永久）        |
    | `canReact`                       | `bool`          | 是否参与后续反应             |
    | `temp` / `wet` / `cond` / `tags` | 数值/枚举       | 该地块所拥有的地形元素四件套 |
    | `effects` / `effectValuePos`     | 列表            | 挂载的效果列表及档位         |
    | `tip`                            | `string`        | 仅作策划备注文档用           |

#### 5. `wave.xlsx`（波次配置表）
- **当前痛点**：策划案 3.0 规定“备战 10s / 战斗 30s / 结算 8s”；表里 `spawn_radius` 是刷怪外环半径（屏幕正交尺寸相关，属相机与视口参数）。
- **划界改动**：
  - **移出至 SO (`WaveTuning.asset`)**：`spawn_radius`（视口边缘生成距离）。
  - **保留在 Excel**：
    | 字段               | 类型    | 说明                   |
    | :----------------- | :------ | :--------------------- |
    | `id`               | `int`   | 波次编号（1, 2, 3...） |
    | `prep_time`        | `float` | 备战时长（默认 10s）   |
    | `battle_time`      | `float` | 战斗时长（默认 30s）   |
    | `settle_time`      | `float` | 结算时长（默认 8s）    |
    | `enemies_per_wave` | `int`   | 本波怪量               |
    | `spawn_interval`   | `float` | 刷怪间隔（秒）         |

#### 6. `element_rule.xlsx`（元素反应规则表）与 `tile_effect.xlsx`（效果参数表）
- **判定**：**100% 留在 Excel**。这是策划的核心资产，表头结构无需做大破坏，保持现有逻辑。

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
