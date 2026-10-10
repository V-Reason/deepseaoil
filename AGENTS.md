# AGENTS.md

> 本文件是所有参与本工程的 AI Agent 的**最高行为宪章**。
> 任何代码生成、修改、重构必须严格无条件遵守以下纪律。

---

## 1. 唯一机器判据：编译门 (Compile Gate)

你在本工程的任何代码改动，最终的唯一验收标准是执行并全绿通过：
```powershell
pwsh Tools/compile-gate.ps1
```
- **通过判据**：**0 error / 0 warning**（当前基线，2026-10 实测）。
  - 历史基线警告 `Assets/Scripts/Logic/Services/SaveService.cs:14` (CS0649: 字段未赋值) 已在 `cce11ac` 随那个无用字段一起清除，**不再是预期输出**。
  - 出现任何 warning 均视为不合规。
- **守卫机制**：编译门会为每个 asmdef 独立生成临时工程，脱离 Unity 引擎也能严格拦截：
  1. 反向依赖（见下节拓扑）
  2. 缺失程序集引用
  3. asmdef 重名（严禁安装 Luban 的 UPM 包）
  4. 手写文件被误放入生成物镜像覆盖区

---

## 2. 四层程序集拓扑与边界铁律

程序集依赖具有严格单向性：`Presentation ➔ Logic ➔ Data ➔ Foundation`。

```text
  [DeepseaOil.Presentation]  ← 表现层/组合根/MonoBehaviour/UI/特效/驱动
              │
              ▼
    [DeepseaOil.Logic]       ← 纯业务逻辑/状态机/数学运算（严禁碰场景）
              │
              ▼
    [DeepseaOil.Data]        ← 数值查询/Spec包装/AssetModule/调参SO (引 cfg)
              │
              ▼
  [DeepseaOil.Foundation]    ← 纯地基：只引 UnityEngine，谁都能引它，它谁都不引
```

### 🔴 致命雷区：asmdef 的 References「不具备传递性」
Unity asmdef 引用**不传递**！若在 A 层使用了 B 层公开的类型，而该类型的基类或接口定义在 C 层，A 的 asmdef **必须显式引用 C**，否则报 `CS0012: 类型在未引用的程序集中定义` 连带 `CS0117`。

### 🔴 逻辑层纯净度纪律 (`Logic/`)
- **严禁**继承 `MonoBehaviour`。
- **严禁**触碰场景对象（`GameObject`、`Transform`、`Component`、`Tilemap`）。
- **严禁**直接读 `Time.time` / `Time.deltaTime`（时间与 Δt 必须由上下文参数或 `IGameTime` 注入）。
- 仅允许使用 `Vector2`、`Vector3Int`、`Mathf` 等纯值类型。

---

## 3. 生成物与修改禁区 (Forbidden Zones)

以下目录每次导表或生成时会被**镜像清空或覆盖**，**严禁在此新建或手动修改任何代码**（修改必丢）：
- `Assets/Scripts/Generated/Config/**`（Luban 代码镜像区）
- `Assets/StreamingAssets/Luban/**`（Luban 数据镜像区）
- `Assets/Scripts/Generated/Input/InputSys.cs`（InputActions 生成物）
- `ConfigWorkspace/output/**`（Luban 导表暂存区）

*注：`Assets/Scripts/Generated/cfg.asmdef` 是刻意放在上一级的手写件，严禁挪进 `Generated/Config/`！*

---

## 4. 数据、调参与表现权责边界 (Data vs SO vs Art)

1. **Excel 表格 (`ConfigWorkspace/Data/*.xlsx`)**：
   - 归策划管辖：只放核心结算数值（HP/伤害）、元素反应网、地块生效节奏、波次编排。
   - 严禁放置物理加速度、击退衰减、手感曲线、闪白频率及美术文件路径。
   - 现状参照：`enemy` 表已按此瘦身到 **`id` / `name` / `max_speed` / `hp`**（见 `Docs/表格数据配置/最新表格与SO规范.md`）。
   - 改表前必须阅读 `ConfigWorkspace/AGENTS.md`，执行校验并走镜像发布。
2. **调参 SO (`Assets/Scripts/Data/Settings/*.cs` & `Resources/tuning/`)**：
   - 归程序管辖：玩家/敌人运动学参数（`CharacterConfig` / `PlayerConfig` / `EnemyTuning`）、投掷抛物线手感（`ThrowTuning`）、掉落物手感（`DropTuning`）、颜色与视觉频率（`VisualPalette`）。
   - 编辑器内实时生效，无需反复导表；**表里删掉的边缘数值必须落到这里，不能留在代码常量里**（`EnemyTuning` 承接的正是 `enemy` 表的 `radius` / `acceleration` / `knockback_decay` / `stop_distance` / `chase_range`）。
3. **Spec 防腐隔离原则**：
   - 上层 `Logic` 与 `Presentation` 只读 `*Spec` 包装类（`ProjectileSpec`、`EnemySpec` 等）。
   - 底层表结构删改字段时，**由 Spec 内部吸收重定向，对外公开属性签名保持不变**，严禁波及上层逻辑报错。
4. **美术资源按需懒加载**：
   - 严禁在游戏启动时全量预加载所有 Tile / Sprite / Prefab。
   - 表现层统一通过 `AssetModule.Load<T>()` 按需懒加载并经内部局部字典缓存，利用 LRU 机制实现内存稳态。

---

## 5. 运行时驱动模型与生命周期 (Runtime Model)

- **唯一真单例**：全工程仅 `GameRoot` 为真单例（DontDestroyOnLoad）。
- **驱动入口唯二**：
  - 严禁在业务组件中私自编写散乱的 `Update()` / `FixedUpdate()`。
  - 所有驱动收敛在 `GameRoot.Update`（渲染帧）与 `GameRoot.FixedUpdate`（物理帧）。
  - 场景系统实现 `ISceneRoot` 接口并声明 `Order`，由 `GameRoot` 统一拓扑编排推进。
- **UI 与服务**：
  - 场景内**严禁**放置 `Canvas`、`EventSystem`、第二个 `AudioListener`。由 `UIMgr` 自动实例化常驻三件套。
  - UI 打开与关闭通过 `UIMgr.ShowPanel<T>()` 与 `HidePanel<T>()` 调度。

---

## 6. 高性能与 2D 避坑铁律 (0 GC & Guardrails)

1. **核心热路径 0 GC (Hot-Path 0 GC)**：
   - 在 `Tick`、`FixedTick`、`Update`、`OnBallHit`、`TakeDamage` 等每帧或高频调用链中：
     - **严禁**使用 LINQ。
     - **严禁**闭包分配与匿名 lambda（使用静态委托或成员方法）。
     - **严禁**频繁 `new List`、`new Array`（强制使用类字段复用或 Scratch 缓冲池）。
     - **严禁**在热路径使用 `GetComponent`。
2. **2D 渲染与物理**：
   - 角色移动刚体必须配置：`gravityScale = 0`、`freezeRotation = true`、`interpolation = Interpolate`。
   - 物理检测必须使用 NonAlloc 版本（如 `OverlapCircleNonAlloc`）。
   - 2D 排序强制使用 `RenderOrder` 统一频带与 `Sorting Layer`，严禁靠 Z 轴粗暴排序。

---

## 7. 注释纪律 (Comment Discipline)

> 完整规范：`Docs/注释规范.md`。机器判据：`pwsh Tools/comment-lint.ps1`（退出码 0 = 通过）。

注释是本工程唯一**没有编译期兜底**的产物——写多了不会编译失败、不会测试变红。
于是它必然单调膨胀：实测 `e2f08be` 之后若干轮开发，代码只长了 8%，注释字符长了 **29%**（53,893 → 69,704）。
因此**注释量受预算约束**，不是"随手补点说明"的自由区。

1. **唯一判据**（每次想写注释先问这一句）：
   这条注释是不是在防止某个人做出一个「**编译器不会拦、测试不会红、只表现为手感 / 观感 / 性能错**」的改动？
   是 → 写，且压成**一行**；否 → **不写**。
2. **只写这 9 类**：跨层时序约定 / 配表字段映射 / 单位与量纲 / 魔法值来源 / 非直觉算法依据 /
   接口契约（null 与 false 的语义）/ 已知坑与硬约束 / 静默失败 / 性能约束。
3. **禁止这 4 类**：`重复复述`（命名已说清的事）、`背景论证`（变更史、"曾经…"、"实测…"、未采纳方案）、
   `排版标签`、`冗余修饰`。进度叙事（`TODO` / 待办）写进 `Docs/待办.md`，**不进代码**。
4. **XML 标签白名单只有 `<summary>` 和 `<remarks>`**：`<c>` `<para>` `<b>` `<i>` `<em>` `<strong>`
   `<paramref>` `<param>` `<returns>` `<list>` `<item>` `<code>` `<br>` `<p>` `<inheritdoc>`
   一律物理删除、只留其中文字；`<see cref="X"/>` 降级为纯文本 `X`。**注释一律单行**，禁止多行注释块。
5. **预算只降不升**：`Tools/comment-budget.json` 登记每个文件的注释有效字符上限，
   新增文件按密度上限 5.37 字符/代码行判定（`Assets/Tests/**` 例外：测试文件一律按文件登记，
   不吃密度线）。确需上调预算必须在 PR 里写明理由并跑 `-Mode seal`；
   **日常开发不要 seal**。判据覆盖 `Assets/Scripts/**` 与 `Assets/Tests/**`。
6. **改注释 == 改代码**：同样要过守卫与编译门。清理注释时不许顺手改代码 token、缩进、
   字符串字面量（`[Tooltip("…")]` 里的中文**是代码**）、行尾风格或 BOM。

---

## 8. Agent 执行改动工作流 (DoD Checklist)

当你接受任务并准备交付代码时，必须按顺序自检：
- [ ] 1. 物理目录与命名空间严格对齐（见 `Docs/目录说明.md`）。
- [ ] 2. 任何需要使用的跨层类型，其基类与接口程序集已在 asmdef 的 `references` 中**显式写全**。
- [ ] 3. 没有触碰任何生成物禁区。
- [ ] 4. 热路径无堆内存分配（0 GC）。
- [ ] 5. 新增/修改的注释符合第 7 节：只写 9 类信息、标签白名单内、单行、在预算内；
  执行 `pwsh Tools/comment-lint.ps1` 退出码为 0。
- [ ] 6. 执行 `pwsh Tools/compile-gate.ps1`，终端输出 `0 Error(s)` 且 `0 Warning(s)`。
```

---

- [x] 架构层：将隐性知识显性化，确立 Agent 行为规范与边界铁律
- [x] 工程层：对齐编译门契约，补足 References 不传递与 Spec 防腐说明
- [x] 性能层：固化 Hot-Path 0 GC、驱动入口唯二与 2D 渲染物理准则
- [x] 交付层：输出完整可直接替换的 `AGENTS.md` 文本
```
