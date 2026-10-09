// ---------------------------------------------------------------------------
// 格上效果链 · 行为测试（"地块改了，敌人却没反应"这一类）
//
// 【为什么在这里】这几条守的都是"不报错、只是敌人毫无反应"的静默缺陷，实测过的三个：
//   ① 减速被丢弃：GridLogic 把续命窗口钉成渲染帧 0.0167s，而消费者按物理帧 0.02s 扣 ——
//      一个物理拍就扣穿；低帧率下大部分物理拍读到的 SlowScale 是 1（泥浆"贴着走也不减速"）。
//   ② DoT 双重计时：状态按自己的 TickInterval 提交，内层累加器却每次只加一帧 Δt ——
//      燃烧 3 秒只掉一次血（还是进格冲击那一次），等于不掉。
//   ③ 瞬时伤害逐帧重放：冰沙站一秒掉 62 点（60fps × 1 点），进格“一下”的语义完全丢失。
//   另加一条：燃烧第 2 档曾被 GridLogic 的表内覆盖钉回第 1 档（0.5 点），
//   而 EnemyStats 按 RoundToInt 取整 ⇒ 0.5 舍成 0 ⇒ 一点都不掉血。
//   球落地这条链还吃过两次"效果被吞"（⑥⑦）：结果状态是 None 时直接 return（兜底行的击退没了）、
//   规则行的 effects 列为空时把状态自己的进格效果整段顶掉（冰沙的瞬时伤害没了）。
//
// 【覆盖边界，写在明处】本文件只跑 Logic ＋ Data（真表、真状态机、真 StatusGroup），
//   不经过 EnemyActor / CombatDirector / 场景接线：预制体与场景那一段仍由
//   Assets/Scenes/Sandboxes/Slice_CombatSandbox.unity 的人工 PlayMode 验收覆盖。
//
// 【跑法】Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// 【为什么自己 Init】与 状态工厂Tests 同一口径：EditMode 里 GameRoot 不跑，不自己初始化就撞 EnsureAssets 守卫。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Grid.States;
using DeepseaOil.Logic.Input;
using NUnit.Framework;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Tests
{
    public class 地块效果Tests
    {
        /// <summary>消费者侧（StatusGroup）按物理帧扣时的步长；渲染帧比它快才是一帧两拍，最容易扣穿减速。</summary>
        private const float PhysicsStep = 0.02f;

        /// <summary>渲染帧步长：30fps 对应"一帧 2 个物理拍"，减速被扣穿的经典形态。</summary>
        private const float RenderStep = 1f / 30f;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            AssetModule.Dispose();

            if (!ConfigModule.IsReady)
                ConfigModule.InitFromStreamingAssets();

            AssetModule.Init();
            ConfigModule.BindAssets();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            AssetModule.Dispose();
        }

        /// <summary>格上目标替身：只记次数；减速转发给**真** StatusGroup，读到的 SlowScale 就是运行时那一份。</summary>
        private sealed class GridTarget : IDamageable, ISlowable, IKnockBackable, IStunnable
        {
            private readonly StatusGroup _status;

            public GridTarget(StatusGroup status)
            {
                _status = status;
            }

            public bool IsAlive { get; set; } = true;

            public Vector2 Position { get; set; }

            public int SlowRenews;

            public float LastSlowScale;

            public int DamageHits;

            public float DamageTotal;

            public int KnockHits;

            public float KnockImpulse;

            public void ApplySlow(float speedScale, float seconds)
            {
                SlowRenews++;
                LastSlowScale = speedScale;

                _status.ApplySlow(speedScale, seconds);
            }

            public void TakeDamage(in Damage damage)
            {
                DamageHits++;
                DamageTotal += damage.Amount;
            }

            public void ApplyKnockback(Vector2 impulse)
            {
                KnockHits++;
                KnockImpulse = impulse.magnitude;
            }

            public void ApplyStun(float seconds)
            {
            }
        }

        /// <summary>生产同款消费者：EnemyLogic 内部建的那一份 StatusGroup（配置来自表＋EnemyTuning 默认值）。</summary>
        private static StatusGroup NewStatus()
        {
            var spec = new EnemySpec(RowFactory.EnemyRow(), ScriptableObject.CreateInstance<EnemyTuning>());

            return new EnemyLogic(new MotorProbe(), spec).Status;
        }

        private static GridLogic NewGrid(EnemyCellRegistry registry, Vector3Int cell, TileElementReactor reactor = null)
        {
            // 与 CombatRoot.Assemble 同一条装配：状态清单与工厂都来自表
            var grid = new GridLogic(
                new GridGeometry(Vector2.zero, 1f),
                ConfigModule.GetAllTileStates(),
                CreateState,
                reactor,
                registry);

            grid.RegisterCell(cell);

            return grid;
        }

        private static TileElementReactor NewReactor()
        {
            return new TileElementReactor(ConfigModule.GetElementRules());
        }

        private static ITileState CreateState(TileStateType id)
        {
            TileStateSpec spec = ConfigModule.TryGetTileState(id);

            return spec != null ? new TableTileState(spec) : null;
        }

        /// <summary>① 续命型减速：渲染帧续命、物理帧扣时，两只钟交错跑，减速必须咬住每一个物理拍。</summary>
        [Test]
        public void 泥浆减速咬住每个物理拍()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            grid.SwitchState(cell, TileStateType.Mud, applyEnterImpact: true);

            var snapshot = new InputSnapshot(Vector2.zero, false, false);

            float now = 0f;
            float accumulator = 0f;
            int ticks = 0;
            int slowed = 0;

            for (int frame = 0; frame < 60; frame++)
            {
                // Unity 的帧序：先物理（可能不止一拍）再渲染 —— 渲染帧里才有一次格上续命
                while (accumulator >= PhysicsStep)
                {
                    accumulator -= PhysicsStep;
                    now += PhysicsStep;

                    status.Tick(new LogicContext(now, PhysicsStep, default, snapshot), Vector2.zero);

                    ticks++;

                    if (status.SlowScale < 0.99f) slowed++;
                }

                accumulator += RenderStep;
                grid.Tick(now, RenderStep);
            }

            Assert.Greater(target.SlowRenews, 0, "泥浆必须往目标身上续减速：seconds<=0 曾被直接丢弃");
            Assert.AreEqual(0.5f, target.LastSlowScale, 1e-4f, "减速倍率取表里的 value1=0.5");
            Assert.Greater(
                slowed,
                ticks * 9 / 10,
                $"减速生效的物理拍要覆盖九成以上（实测 {slowed}/{ticks}）：续命窗口短于一个物理拍时会被扣穿，表现为「贴着泥浆也不减速」");
        }

        /// <summary>② DoT：燃烧（第 2 档 = 2 点/秒）按节拍跳字，且每次都是自己那一档的伤害。</summary>
        [Test]
        public void 燃烧按节拍掉血且用自己那一档()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            // 进格冲击那一下算一次（applyEnterImpact 的语义）
            grid.SwitchState(cell, TileStateType.Burn, applyEnterImpact: true);

            float now = 0f;

            for (int i = 0; i < 180; i++)
            {
                now += 1f / 60f;

                grid.Tick(now, 1f / 60f);
            }

            // 次数不钉死：浮点累加会让 3.0 秒那一次踩在边界上（实测 3 次，偶尔 2 次）。
            // 钉死的是两条语义：① 不是"攒 60 次提交才掉一次"（那样最多 1 次）；② 每次都是第 2 档的 2 点。
            Assert.GreaterOrEqual(target.DamageHits, 3, "进格 1 次 + 至少 2 次周期：内层累加器只加单帧 Δt 时会 60 秒才掉一次");
            Assert.AreEqual(
                2f * target.DamageHits,
                target.DamageTotal,
                1e-3f,
                "每次伤害必须是状态自己那一档（燃烧 effectValuePos=2 → 2 点/次）：被表内第 1 档覆盖就变成 1 点/次");
        }

        /// <summary>③ 瞬时伤害：站着不动只吃进格那一次，逐帧重放就是每秒 60 点。</summary>
        [Test]
        public void 瞬时伤害只在进格那一下()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };

            // 冰沙先存在（球落地那一刻没人站在上面），敌人随后走进来
            grid.SwitchState(cell, TileStateType.Smoothie, applyEnterImpact: true);

            Assert.AreEqual(0, target.DamageHits, "没人站在格上时不该产生伤害");

            registry.Register(cell, target);
            grid.OnActorEnterCell(cell, target);

            float now = 0f;

            for (int i = 0; i < 60; i++)
            {
                now += 1f / 60f;

                grid.Tick(now, 1f / 60f);
            }

            Assert.AreEqual(1, target.DamageHits, "进格一次只该痛一次：逐帧提交会让冰沙一秒掉 60 点");
            Assert.AreEqual(1f, target.DamageTotal, 1e-3f);
        }

        /// <summary>④ 表里没给时长的减速：按"只要在格子上就持续生效"处理，不许直接丢弃。</summary>
        [Test]
        public void 没给时长的减速也生效()
        {
            StatusGroup status = NewStatus();

            status.ApplySlow(0.5f, 0f);

            status.Tick(new LogicContext(0f, PhysicsStep, default, new InputSnapshot(Vector2.zero, false, false)), Vector2.zero);

            Assert.AreEqual(0.5f, status.SlowScale, 1e-4f, "seconds<=0 = 没给时长，不是「无效」；按续命兜底至少活过一拍");
        }

        /// <summary>⑤ 离开泥浆要能恢复：续命停掉后不许永远挂着减速。</summary>
        [Test]
        public void 离开泥浆后减速会恢复()
        {
            StatusGroup status = NewStatus();
            var snapshot = new InputSnapshot(Vector2.zero, false, false);

            status.ApplySlow(0.5f, 0f);

            float now = 0f;

            // 续命窗口兜底 3 拍：4 拍不续就该过期（不做"粘住不放"的减速）
            for (int i = 0; i < 4; i++)
            {
                now += PhysicsStep;

                status.Tick(new LogicContext(now, PhysicsStep, default, snapshot), Vector2.zero);
            }

            Assert.AreEqual(1f, status.SlowScale, 1e-4f, "没人再续命时减速必须过期，否则离开泥浆还一直是半速");
        }

        /// <summary>⑥ 规则命中但结果状态是"无"（不改地形）：效果照样要落地，冲量不许被闷在判定里。</summary>
        [Test]
        public void 反应不改地形时击退照样落地()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell, NewReactor());

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            // 造一个"除兜底行外谁都不匹配"的地形元素：含沙 + 湿 3 + 温 0
            grid.SetCellElement(cell, new ElementValue(ElementType.Environment, ElementTag.Sand, 0, 3, 0));

            ElementValue ball = ConfigModule.GetBall(BallType.Water).Element;

            bool changed = grid.OnBallHit(cell, in ball);

            Assert.IsFalse(changed, "兜底规则的结果状态是 None：地形不变，返回值就该是 false");
            Assert.AreEqual(1, target.KnockHits, "地形没变也要把兜底行的击退打出去（曾经这里直接 return，冲量被吃掉）");
            Assert.Greater(target.KnockImpulse, 0f, "冲量要按格数与衰减率折算成速度，不能是 0");
        }

        /// <summary>⑦ 规则行的 effects 列是空的：状态自己的"进格一下"不能被空清单顶掉（冰沙的瞬时伤害）。</summary>
        [Test]
        public void 规则清单为空时状态自己的进格伤害仍落地()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell, NewReactor());

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            // 含沙 + 冷 ⇒ 命中冰沙那一行（它的 effects 列是空的，只有状态自己带"减速;瞬时伤害"）
            grid.SetCellElement(cell, new ElementValue(ElementType.Environment, ElementTag.Sand, 0, 3, 0));

            ElementValue ball = ConfigModule.GetBall(BallType.WaterCold).Element;

            bool changed = grid.OnBallHit(cell, in ball);

            Assert.IsTrue(changed, "含沙 + 冷 应命中冰沙规则");
            Assert.AreEqual(TileStateType.Smoothie, grid.StateOf(cell));
            Assert.AreEqual(1, target.DamageHits, "站在格上的目标要吃冰沙那 1 点瞬时伤害：规则清单为空时被顶掉就是 0");
        }
    }
}
