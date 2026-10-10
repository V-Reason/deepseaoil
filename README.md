# 深海鱼油？嗯！

Unity 2022.3.62f3c1 + URP 14.0.12。四层架构（Foundation / Data / Logic / Presentation），配表走 Luban。
俯视角 2D：投掷（水球 / 土球）→ 落地改格子 → 格子对踩上去的敌人结算伤害，外加水球掉落物与波次敌人。

## 先读哪份

| 想知道什么 | 看哪份 |
| :-- | :-- |
| **文档地图（先看这个）** | [`Docs/README.md`](Docs/README.md) |
| 给 AI Agent 的硬性纪律（编译门 / 层边界 / 禁区） | [`AGENTS.md`](AGENTS.md) |
| 每个文件夹装什么、命名约定、哪里不能动 | [`Docs/工程/目录说明.md`](Docs/工程/目录说明.md) |
| 层边界（asmdef 拓扑 / 依赖方向 / 改了依赖会不会红） | [`Docs/工程/架构约束.md`](Docs/工程/架构约束.md) |
| 注释怎么写、哪些必须删、注释预算超了怎么查 | [`Docs/工程/注释规范.md`](Docs/工程/注释规范.md) |
| 第一次接手：跑通验证 / 架构入口 / 前 3 项工作 | [`Docs/工程/人类接手与工程交接手册.md`](Docs/工程/人类接手与工程交接手册.md) |
| 还没做、有人得做的项 | [`Docs/待办.md`](Docs/待办.md) |
| 从空白场景重新接一遍线（含 Cinemachine） | [`Docs/表现/新场景接线.md`](Docs/表现/新场景接线.md) |
| 特效怎么建预制体、怎么播、怎么验、怎么排错 | [`Docs/表现/粒子特效系统.md`](Docs/表现/粒子特效系统.md) |
| Tile / 角色贴图怎么导入、怎么命名 | [`Docs/美术装配指南.md`](Docs/美术装配指南.md) |
| 怎么改表、导表怎么排障 | [`Docs/配表/`](Docs/配表/) |
| 反应网与数值的设计契约 | [`Docs/设计/策划案_荒原复苏指南_v4.0.md`](Docs/设计/策划案_荒原复苏指南_v4.0.md) |

## 代码在哪

```
Assets/Scripts/
├── Foundation/      与游戏无关的地基（单例基类、对象池、随机、Y 排序、状态机骨架、弹道数学）
├── Data/            数据层：数值配置（Luban）＋ 调参 SO ＋ 资源加载 ＋ 观测面
├── Logic/           逻辑层：纯逻辑，没有 MonoBehaviour（Actor / Movement / Player / Input / Bounds / Event / Services
│                    ＋ 战斗切片的 Combat / Grid / Projectile / Drop / Wave）
├── Presentation/    表现层：组合根（GameRoot / PlayerController / CombatRoot）、适配器、战斗切片各件、UI、特效
└── Generated/       🔴 机器生成，禁手改
```

逐目录的清单（含端口、命名空间例外、生成物禁区）在 [`Docs/工程/目录说明.md`](Docs/工程/目录说明.md)；那份目录树是**人工维护的快照**（改目录结构时请顺手同步）。

## 跑起来

- 打开 `Assets/Scenes/Official.unity`（**主场景**：唯一装配了完整玩法链的场景）进 Play；`Empty.unity` 是空白件，三个切片沙盒在 `Assets/Scenes/Sandboxes/`。
- **进 Play 后是菜单状态（`timeScale = 0`，一切冻结）**，点开始面板的 `StartBtn` 才进 `Running`。
- 导表：菜单 **Luban ▸ 表格数据导入**（`Ctrl/Cmd+Shift+D`）。
- 测试：`Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All`（当前 79 条；其中 `Assets/Tests/Tools/` 那 8 条走 `DeepseaOil.EditorTools.Tests` 程序集）。
- 只想验投掷链路：`CombatRoot` 的 Inspector 上把 **`enableWaves`（"是否刷敌人"）** 取消勾选即可（此时不会有敌人，但格子、球、掉落物、喷泉照常跑）。
- 编译门（不打开 Unity 也能查编译）：`pwsh Tools/compile-gate.ps1`。
