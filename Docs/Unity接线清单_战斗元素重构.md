# Unity 接线清单 · 战斗与元素反应重构

> **背景**：本轮把「温湿度伪拟真」整条链换成了「离散二元查表 + 网格连锁泛化」。
> 逻辑层、Data 层、配表、导表、编译门、测试**已经全部落地并全绿**；
> 剩下的只有**Unity 编辑器内的资源与场景接线**——那些没有编辑器就做不了、也没法自动校验。
>
> 每条都写了「**不接的后果**」。缺资源的地方代码一律降级为「逻辑生效 + 告警」，不崩。

---

## 0. 先跑这两条（不需要 Unity）

```powershell
pwsh Tools/compile-gate.ps1 -Topology     # 期望：错误 0 条 / 警告 0 条
pwsh Tools/comment-lint.ps1               # 期望：LINT OK，退出码 0
```

---

## 1. 补 4 个地块贴图资源 🔴 必做

状态 → 贴图走**约定式懒加载**：`Assets/Resources/tiles/Tile_<枚举名>.asset`。
本轮新增了 4 个枚举成员，资源不存在：

| 资源路径 | 对应枚举 | 用在哪 |
| --- | --- | --- |
| `Assets/Resources/tiles/Tile_ConductZone.asset` | `ConductZone` | 水砸漏电桩的产物 |
| `Assets/Resources/tiles/Tile_FlameField.asset` | `FlameField` | 燎原爆发的火海 |
| `Assets/Resources/tiles/Tile_ChargedThorn.asset` | `ChargedThorn` | 电 × 植物 |
| `Assets/Resources/tiles/Tile_FrostSpike.asset` | `FrostSpike` | 冰 × 植物 |

**做法**：复制一个现有 `Tile_*.asset`，改名为上表的名字即可（Tile 资产内部引用的是 Sprite，
贴图先复用现有的也能先跑起来）。

**不接的后果**：`TilemapAdapter.TileFor` 找不到资源 → 只打一条 Warning，
**逻辑照常生效**（敌人照常减速掉血），但玩家**看不见**那块地。
这类"看不见但真在扣血"的错最难自查，所以建议先接上。

> 已在 `__enums__.xlsx` 里的旧枚举 `Smoothie / FrozenEarth / Vine / Glass / Burn / Ashes`
> 已删除，但对应的 `Tile_*.asset` 还在。**不要删它们**——`TilemapAdapter` 的
> `InitialSetup` 笔刷层按资产名反解状态，删了会让老场景里画过的地块变成空地。

---

## 2. 场景里摆三泉 🔴 必做

`Assets/Resources/prebs/Fountain.prefab` 现在是**三用途**组件，用 `kind` 字段区分。

在 `Assets/Scenes/Sandboxes/Slice_CombatSandbox.unity` 里复制 **3 份**，分别设：

| 对象名建议 | `kind` | 位置建议 | 行为 |
| --- | --- | --- | --- |
| `WaterWell` | `Water` | 北侧 | 玩家进九宫格 → 补满水弹药(5)，并持续吐水滴掉落 |
| `EarthMound` | `Earth` | 西南 | 玩家进九宫格 → 补满土弹药(5)，并持续吐土块掉落 |
| `LifeTotem` | `Life` | 东南 | 不吐掉落物；玩家在九宫格内**完全静止 3 秒** → 回 1 颗心 |

然后把三个都拖进 `CombatRoot` 的 **`fountains`** 数组（可拖多个，留空也可以）。

**关于 Collider**：`Fountain.Awake` 会自愈——没有 `Collider2D` 就自动补一个
`CircleCollider2D (isTrigger, radius = 1.5)`，正好覆盖以泉眼为中心的九宫格。
想手动控制半径就在 prefab 上自己挂一个，代码不会覆盖作者的配置。

**不接的后果**：
- `fountains` 数组留空 → 玩家只能靠掉落物补给，**没有回血站**（装配日志里会显式提示
  「未接线生命神泉：本局没有回血站」）。
- 接了多个 `kind = Life` → 只认第一个，其余会打一条 Warning。

---

## 3. 刷一格火池做现场验收 ✅ 强烈建议

在 `TilemapAdapter` 的 **`InitialSetup` 层**（就是那个"读一次就自动隐藏"的笔刷层）
刷 1～2 格 `Tile_BasicFire`。

进 Play 之后朝火池扔**纯水**，应当立刻看到：

| 预期现象 | 对应断言 |
| --- | --- |
| 火池变成**蒸汽** | `element_rule` #4 命中 |
| 站在上面的敌人**当帧就掉 2 血** | 首跳伤害当帧结算（本轮修的静默 Bug） |
| 敌人被**推开 3 格** | `impact_knockback = 3` |
| Console 打出 `[Reaction] BasicFire + Water → Steam（规则 #4…）` | `ReactionResolver.TraceEnabled` 追踪 |

> 想看 `[Reaction]` 追踪：`GridReactionHarness` 切片默认打开它；
> `CombatDummyHarness` 也打开了。主战斗场景默认关闭（零分配）。

---

## 4. 按下 E 键需要先有种子 ⚠️ 注意

播种的三条前置条件（缺一条都**静默不播**，这是刻意设计——不做"播不了还扣种子"）：

1. 手上有种子（`PlayerStats.Seed != None`）
2. 瞄准格**有地板**且当前是**空地**
3. 种子在 `seed.xlsx` 里有行

**怎么拿到第一颗种子**：进 Play 后等 **10 秒备战期**结束前，`WaveLogic` 会在进入 Prep 时
挂上配给，`CombatRoot` 同帧取走并发给玩家。HUD 的种子栏会从「种子：空（等下一波配给）」
变成「种子：火种子」。

**如果 HUD 里没有 `txtSeed` / `txtPhase` 节点**：不报错，只是不显示这两行——
`HudPanel` 对缺件是容错的。参照 `Assets/Resources/ui/Panel/HudPanel.prefab`
里已有的 `txtHp / txtWater / txtWave` 加两个 `TMP_Text`，名字分别叫 `txtSeed`、`txtPhase`。

---

## 5. 输入动作表已改，InputSys.cs 不用重新生成 ℹ️

`Assets/Scripts/Presentation/Input/InputSys.inputactions` 的 `Player` 地图里新增了 4 个动作：

| 动作 | 类型 | 绑定 |
| --- | --- | --- |
| `Attack` | Button | `<Mouse>/leftButton` |
| `AltAttack` | Button | `<Mouse>/rightButton` |
| `Plant` | Button | `<Keyboard>/e` |
| `Aim` | PassThrough (Vector2) | `<Mouse>/position` |

`InputProvider` 用 `InputActionAsset.FindAction("Player/Attack")` 取用它们。
**故意不走生成的属性**：这样改动作表不必重新生成 95KB 的 `InputSys.cs`。

**在 Unity 里要做的**：打开这个资产确认它是干净的（改动只增不删，理论上直接生效）。
如果 Unity 提示"资产有外部改动，是否重新导入"→ 选**重新导入**。
**不要**点 "Generate C# Class"（那会重写 `InputSys.cs`，而当前代码不依赖它）。

**不接的后果**：`InputProvider.Bind()` 会为找不到的动作各打一条 `LogError`，
明确告诉你缺哪个；对应输入永久失效（不会静默）。

---

## 6. 验收清单（全部勾上才算交付）

- [ ] `pwsh Tools/compile-gate.ps1 -Topology` → 0 error / 0 warning
- [ ] `pwsh Tools/comment-lint.ps1` → LINT OK
- [ ] 4 个新 Tile 资源就位（第 1 节）
- [ ] 三泉已摆并拖进 `CombatRoot.fountains`（第 2 节）
- [ ] `InitialSetup` 层刷了至少 1 格火池，水砸火池能当场看到 2 伤 + 击退（第 3 节）
- [ ] Unity ▸ **Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All** 全绿
      （本会话跑不了 Unity，这是唯一需要你代跑的验收项）
- [ ] HUD 能看到「水 5/5 · 土 5/5 · 种子：火种子」

### 编辑模式下测试覆盖了什么

| 测试文件 | 守什么 |
| --- | --- |
| `ReactionResolverTests.cs` | 表命中优先于兜底；未命中零冲击；重复键被报出 |
| `网格连锁Tests.cs` | 连锁导电全网同帧；环状水网被 32 步截断；火海逐圈蔓延并烧穿；二级反应激发 |
| `地块效果Tests.cs` | 减速咬住每个物理拍；DoT 按秒；**首跳伤害当帧**；连锁标记真的泛洪 |
| `状态工厂Tests.cs` | 表里每个地貌都能造出状态；反应主键不重复；结果地貌都有实现 |
| `Data层Tests.cs` | 配置链路跑通（含新增的 seed / 二级反应表） |
| `LubanWorkflowTests.cs` | 导表参数串；**输入动作表含全部战斗动作与绑定** |

### 人在编辑器里仍要目视确认的（自动化覆盖不到）

1. 三泉的三角动线是否真的形成拉扯（跑位是否顺手）
2. 泥浆 ×0.5 减速的手感是否够明显（不够就调 `tile_state.slow_rate`）
3. 生命神泉 3 秒静止窗口够不够用（不够就调 `player.life_heal_interval`）
4. 连锁导电的视觉反馈是否"够响"（水网受击的那一帧要有明确表现）
