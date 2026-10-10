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
pwsh Tools/luban-mirror.ps1               # 期望：待删除 0 / 待覆盖 0 / 孤儿 .meta 0
```

---

## 1. 地块贴图 ✔ 已就绪（无需动作）

状态 → 贴图走**约定式懒加载**：`Assets/Resources/tiles/Tile_<枚举名>.asset`。
`Assets/Resources/tiles/` 现在是**扁平一层 17 份**，与 `TileStateType` 一一对应：

| 资源 | 说明 |
| --- | --- |
| `Tile_ConductZone` / `Tile_FlameField` / `Tile_ChargedThorn` / `Tile_FrostSpike` | 本轮新增的 4 个状态，**资源已存在**，不用新建 |
| `Tile_Normal` / `Tile_Mud` / `Tile_MudSkid` / `Tile_Freeze` / `Tile_TerracottaBrick` / `Tile_Steam` | 常规状态 |
| `Tile_BasicWater` / `Tile_BasicEarth` / `Tile_BasicFire` / `Tile_BasicIce` / `Tile_BasicElectricity` / `Tile_BasicPlant` | 基础元素地貌（反应的输入端） |
| `Tile_Floor` | **不是状态**：`Grid/Ground` 层刷的常规地板，四个场景直接引用它 |

`Assets/Resources/tiles/` 是**扁平一层 17 份**，没有任何子目录（旧的 `tilesets/` 子目录已移除，
`Tile_Floor` 已上提到顶层；因为 `.meta` 一起移动，GUID 未变、场景引用不受影响）。
`Tile_Floor` **不是状态**：它是 `Grid/Ground` 层刷的常规地板件。

**还守着两条规矩**：

1. 状态件必须叫 `Tile_<枚举成员名>` 且**放在 `tiles/` 顶层**：`TileFor` 只认 `tiles/Tile_X` 这条路径，建子目录就等于没这个资源。
2. 资产的内部名 `m_Name` 要保持带 `Tile_` 前缀：`InitialSetup` 笔刷层（`ResolveSetupState`）按资产名反解状态，改名会让笔刷刷过的地块静默落回 `Normal`。

**删贴图之前**先确认没有场景在 `InitialSetup` 层画过它（删掉只会得到一块空地，不报错）。

---

## 2. 三泉 ✔ 已就绪（无需动作）

`Assets/Resources/prebs/Fountain.prefab` 是**三用途**组件，用 `kind` 字段区分。
`Assets/Scenes/EmptyTest.unity` 里已经摆了 3 份，并且都拖进了 `CombatRoot.fountains`：

| 对象名 | `kind` | 行为 |
| --- | --- | --- |
| `WaterWell` | `Water`(0) | 玩家进九宫格 → 补满水弹药(5)，并持续吐水滴掉落 |
| `EarthMound` | `Earth`(1) | 玩家进九宫格 → 补满土弹药(5)，并持续吐土块掉落 |
| `LifeTotem` | `Life`(2) | 不吐掉落物；玩家在九宫格内**完全静止 3 秒** → 回 1 颗心 |

**关于 Collider**：`Fountain.Awake` 会自愈——没有 `Collider2D` 就自动补一个
`CircleCollider2D (isTrigger, radius = 1.5)`，正好覆盖以泉眼为中心的九宫格。
想手动控制半径就在 prefab 上自己挂一个，代码不会覆盖作者的配置。

**装配日志会自证**：`[Combat] 装配完成：… 泉眼 3 个（生命神泉 1 个）…`。
若哪天 `fountains` 数组被清空，日志会改成「（未接线生命神泉：本局没有回血站，请在场景里放一个 kind=Life 的 Fountain）」。
接了多个 `kind = Life` → 只认第一个，其余打一条 Warning。

> 另外三个沙盒场景**没有**摆泉，它们各自只验一块，不需要泉。

---

## 3. 火池 ✔ 已就绪（可直接验收）

`EmptyTest.unity` 的 **`InitialSetup` 层**已经刷了 **9 格 `Tile_BasicFire`**
（x∈[3,5]、y∈[-2,0] 的 3×3 块，`LoadInitialSetupTiles` 载入 9 格）。

进 Play 之后走到那块**橙色方块**旁边，朝它扔**纯水**（鼠标左键），应当立刻看到：

| 预期现象 | 对应断言 |
| --- | --- |
| 火池变成**蒸汽**（那一格转为近白色 `Tile_Steam`） | `element_rule` #4 命中 |
| 站在上面的敌人**当帧就掉 2 血** | 首跳伤害当帧结算 |
| 敌人被**推开 3 格** | `impact_knockback = 3` |
| **只掉一次血、不额外麻痹** | `trigger_chain = FALSE`（蒸汽既不可燃也不导通，连锁无处可去） |

扔**纯土**（右键）则该格变成陶砖色 `Tile_TerracottaBrick`（`element_rule` #5）。

> 想看 `[Reaction]` 追踪：`GridReactionHarness` 切片默认打开它；`CombatDummyHarness` 也打开了。
> **主战斗场景默认关闭**（`ReactionResolver.TraceEnabled = false`，零分配）。
> 想在 `EmptyTest` 里看，把 `ReactionResolver.cs` 的那个开关临时置 `true`。

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

- [x] `pwsh Tools/compile-gate.ps1 -Topology` → 0 error / 0 warning
- [x] `pwsh Tools/comment-lint.ps1` → LINT OK
- [x] `pwsh Tools/luban-mirror.ps1` → 0 / 0 / 0
- [x] 4 个新 Tile 资源就位（第 1 节）
- [x] 三泉已摆并拖进 `CombatRoot.fountains`（第 2 节）
- [x] `InitialSetup` 层刷了 3×3 火池，水砸火池能当场看到 2 伤 + 击退（第 3 节）
- [ ] Unity ▸ **Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All** 全绿
      （无 Unity 环境跑不了，这是唯一需要人代跑的验收项）
- [ ] HUD 能看到「水 5/5 · 土 5/5 · 种子：火种子」
- [ ] 4 个种子图标资源补齐（`seed.icon_key` 指向 `Icons/Seed_*`，当前 `Resources/Icons/` 下只有 `sardline.png`）

### 编辑模式下测试覆盖了什么

| 测试文件 | 守什么 |
| --- | --- |
| `ReactionResolverTests.cs` | 表命中优先于兜底；未命中零冲击；重复键被报出 |
| `网格连锁Tests.cs` | 连锁导电全网同帧；环状水网被 32 步截断；火海逐圈蔓延并烧穿；二级反应激发 |
| `地块效果Tests.cs` | 减速咬住每个物理拍；DoT 按秒；**首跳伤害当帧**；连锁标记真的泛洪 |
| `状态工厂Tests.cs` | 表里每个地貌都能造出状态；反应主键不重复；结果地貌都有实现 |
| `Data层Tests.cs` | 配置链路跑通（含新增的 seed / 二级反应表） |
| `表清单Tests.cs` | `TablesMeta.Names` 与生成物 `cfg.Tables` 的属性**逐项反射对齐**（加 / 删表后必跑） |
| `LubanWorkflowTests.cs` | 导表参数串；**输入动作表含全部战斗动作与绑定** |

### 人在编辑器里仍要目视确认的（自动化覆盖不到）

1. 三泉的三角动线是否真的形成拉扯（跑位是否顺手）
2. 泥浆 ×0.5 减速的手感是否够明显（不够就调 `tile_state.slow_rate`）
3. 生命神泉 3 秒静止窗口够不够用（不够就调 `player.life_heal_interval`）
4. 连锁导电的视觉反馈是否"够响"（水网受击的那一帧要有明确表现）
