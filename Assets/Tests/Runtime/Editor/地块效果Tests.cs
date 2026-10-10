// ---------------------------------------------------------------------------
// 格上效果链 · 行为测试（"地块改了，敌人却没反应"这一类）
//
// 【权责切分】落地冲击（伤害/击退/麻痹/连锁）归 element_rule，经 ReactionResolver 裁决、
//   由 GridLogic.OnBallHit 一处提交；地面残留（减速/DoT/存活秒数）归 tile_state，
//   由 TableTileState 按帧提交。两张表不重叠、不互斥。
//
// 【守的是静默缺陷】都是"不报错、只是怪毫无反应"那一类：
//   ① 减速被丢弃：续命窗口短于一个物理拍就被扣穿，表现为"贴着泥浆也不减速"。
//   ② DoT 双重计时：攒拍只加单帧 Δt，燃烧 3 秒只掉一次血，等于不掉。
//   ③ 瞬时伤害逐帧重放：站一秒掉 60 点，进格"一下"的语义完全丢失。
//   ④ 首跳伤害必须当帧打出（落地那一帧就扣血）。
//   ⑤ 连锁只许发生在导通格上（蒸汽既不可燃也不导通，不许凭空带电）。
//
// 【覆盖边界】本文件只跑 Logic ＋ Data（真表、真状态机、真 StatusGroup），
//   不经过 EnemyActor / CombatDirector / 场景接线：那一层由人工 PlayMode 验收覆盖。
//
// 【跑法】Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// 【为什么自己 Init】EditMode 里 GameRoot 不跑，不自己初始化就撞 EnsureAssets 守卫。
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

            public int StunHits;

            public float StunSeconds;

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
                StunHits++;
                StunSeconds = seconds;
            }
        }

        /// <summary>生产同款消费者：EnemyLogic 内部建的那一份 StatusGroup（配置来自表＋EnemyTuning 默认值）。</summary>
        private static StatusGroup NewStatus()
        {
            var spec = new EnemySpec(RowFactory.EnemyRow(), ScriptableObject.CreateInstance<EnemyTuning>());

            return new EnemyLogic(new MotorProbe(), spec).Status;
        }

        /// <summary>与 CombatRoot.Assemble 同一条装配：状态清单、状态工厂、二级反应查询表都来自表。</summary>
        private static GridLogic NewGrid(EnemyCellRegistry registry, Vector3Int cell)
        {
            ReactionResolver.Initialize(ConfigModule.GetElementRules());
            TileChainReactor.Clear();

            var grid = new GridLogic(
                new GridGeometry(Vector2.zero, 1f),
                ConfigModule.GetAllTileStates(),
                CreateState,
                ConfigModule.GetDuoReactions(),
                registry);

            grid.RegisterCell(cell);

            return grid;
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

            grid.SwitchTileState(cell, TileStateType.Mud);

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

            Assert.Greater(target.SlowRenews, 0, "泥浆必须往目标身上续减速：续命窗口为 0 时会被物理帧当场扣穿");
            Assert.AreEqual(0.5f, target.LastSlowScale, 1e-4f, "减速倍率取表里的 slow_rate=0.5");
            Assert.Greater(
                slowed,
                ticks * 9 / 10,
                $"减速生效的物理拍要覆盖九成以上（实测 {slowed}/{ticks}）：续命窗口短于一个物理拍时会被扣穿，表现为「贴着泥浆也不减速」");
        }

        /// <summary>② DoT：导电区（dot_damage=1）按秒跳字。</summary>
        [Test]
        public void 持续伤害按秒掉血()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            // 先切状态再登记：模拟"球先落地、敌人随后走进来"
            grid.SwitchTileState(cell, TileStateType.ConductZone);

            grid.OnActorEnterCell(cell, target);

            int afterEnter = target.DamageHits;

            float now = 0f;

            for (int i = 0; i < 180; i++)
            {
                now += 1f / 60f;

                grid.Tick(now, 1f / 60f);
            }

            // 次数不钉死：浮点累加会让 3.0 秒那一次踩在边界上。
            // 钉死的是两条语义：① 进格那一下立刻痛（不是等满一秒）；② 之后按秒续上。
            Assert.GreaterOrEqual(afterEnter, 1, "走进 DoT 格必须当场痛一次：等满一秒才掉血是首跳丢失");
            Assert.GreaterOrEqual(target.DamageHits, afterEnter + 1, "DoT 必须按秒续上：内层累加器只加单帧 Δt 时 60 秒才掉一次");
        }

        /// <summary>③ 瞬时伤害：站着不动只吃进格那一次，逐帧重放就是每秒 60 点。</summary>
        [Test]
        public void 进格一次性伤害只结算一次()
        {
            StatusGroup status = NewStatus();
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            var target = new GridTarget(status) { Position = grid.Geometry.CellCenter(cell) };

            // 蒸汽先存在（球落地那一刻没人站在上面），敌人随后走进来
            grid.SwitchTileState(cell, TileStateType.Steam);

            Assert.AreEqual(0, target.DamageHits, "没人站在格上时不该产生伤害");

            registry.Register(cell, target);
            grid.OnActorEnterCell(cell, target);

            float now = 0f;

            for (int i = 0; i < 60; i++)
            {
                now += 1f / 60f;

                grid.Tick(now, 1f / 60f);
            }

            // 蒸汽 dot_damage=1：进格 1 次 + 1 秒内 1 次 ≈ 2 次；逐帧重放会是 60 次
            Assert.LessOrEqual(target.DamageHits, 3, "进格的一次性伤害不许逐帧重放：站一秒掉 60 点是这个缺陷的经典表现");
            Assert.GreaterOrEqual(target.DamageHits, 1, "进格那一下必须痛");
        }

        /// <summary>④ 表里没给时长的减速（duration=-1）：按"只要在格子上就持续生效"处理，不许直接丢弃。</summary>
        [Test]
        public void 没给时长的减速也生效()
        {
            StatusGroup status = NewStatus();

            status.ApplySlow(0.5f, 0f);

            status.Tick(new LogicContext(0f, PhysicsStep, default, new InputSnapshot(Vector2.zero, false, false)), Vector2.zero);

            Assert.AreEqual(0.5f, status.SlowScale, 1e-4f, "seconds<=0 = 没给时长，不是「无效」；按续命兜底至少活过一拍");
        }

        /// <summary>⑤ 续命停掉后减速必须过期：不做"粘住不放"的减速。</summary>
        [Test]
        public void 没续命时减速会过期()
        {
            StatusGroup status = NewStatus();
            var snapshot = new InputSnapshot(Vector2.zero, false, false);

            status.ApplySlow(0.5f, 0f);

            float now = 0f;

            // 续命窗口兜底 3 拍：4 拍不续就该过期
            for (int i = 0; i < 4; i++)
            {
                now += PhysicsStep;

                status.Tick(new LogicContext(now, PhysicsStep, default, snapshot), Vector2.zero);
            }

            Assert.AreEqual(1f, status.SlowScale, 1e-4f, "没人再续命时减速必须过期，否则离开泥浆还一直是半速");
        }

        /// <summary>⑥ 首跳伤害必须当帧打出：打不出这一帧就是「生成火池/蒸汽后怪走上去跳 0 点伤害」。</summary>
        [Test]
        public void 落地瞬间伤害当帧结算()
        {
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            // 造一格基础火池，敌人正站在上面
            grid.SwitchTileState(cell, TileStateType.BasicFire);

            var target = new GridTarget(NewStatus()) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            Assert.AreEqual(0, target.DamageHits, "还没投球时不该有伤害");

            bool changed = grid.OnBallHit(cell, BallType.Water);

            Assert.IsTrue(changed, "水砸火池必须产生变化（蒸汽）");
            Assert.AreEqual(TileStateType.Steam, grid.StateOf(cell), "水 + 基础火池 → 蒸汽");
            Assert.AreEqual(
                1,
                target.DamageHits,
                "落地瞬间伤害必须在 OnBallHit 返回前结算，且只结算一次："
                + "等下一次 Tick 就是「生成蒸汽后怪站着不掉血」的静默 Bug，多打一次则是重复结算");
            Assert.AreEqual(2f, target.DamageTotal, 1e-3f, "element_rule #4 的瞬伤是 2 点");
            Assert.Greater(target.KnockImpulse, 0f, "element_rule 里这一行配了击退，必须折算成冲量打出去");

            // 蒸汽既不可燃也不导通 ⇒ 这一行不该有连锁方向，更不该凭空补一次电击
            Assert.AreEqual(0, target.StunHits, "蒸汽不导电：连锁泛洪的起点必须自己也是导通体");
        }

        /// <summary>⑦ 连锁标记必须真的把泛洪跑起来：表里 trigger_chain=TRUE 的那一行要额外打出电击。</summary>
        [Test]
        public void 连锁标记会触发泛洪()
        {
            var registry = new EnemyCellRegistry();
            var cell = Vector3Int.zero;

            GridLogic grid = NewGrid(registry, cell);

            grid.SwitchTileState(cell, TileStateType.BasicElectricity);

            var target = new GridTarget(NewStatus()) { Position = grid.Geometry.CellCenter(cell) };
            registry.Register(cell, target);

            grid.OnBallHit(cell, BallType.Water);

            Assert.AreEqual(TileStateType.ConductZone, grid.StateOf(cell), "水 + 基础电源 → 导电区");

            // 落地冲击是 1 伤 + 1.5s 麻痹；连锁电击再补 1 伤 + 1.5s。
            // 只钉"有没有"会假绿 —— 冲击那一份就能满足，必须钉次数才能证明这一列不是死列。
            Assert.AreEqual(2, target.DamageHits, "TriggerChain 为真时必须真的泛洪：只写一列标记等于连锁根本不存在");
            Assert.AreEqual(2, target.StunHits, "泛洪的麻痹与落地冲击是两次独立施加");
            Assert.AreEqual(1.5f, target.StunSeconds, 1e-4f, "连锁电击的麻痹时长");
        }
    }
}
