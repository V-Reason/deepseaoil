// ---------------------------------------------------------------------------
// 网格连锁 · 行为测试
//
// 守的是"连锁只是表里的一列数字"这一类静默缺陷：
//   ① 连锁导电：水/泥浆网格要真的把电传下去，且**同帧**全网受击
//   ② 步数上限：环状水网不许无限扩散，也不许抛异常/卡死
//   ③ 燎原爆燃：按 0.2s 步长逐圈蔓延，烧完回空地
//   ④ 二级元素反应：两个发生器经水线接通要激发质变
//
// 【跑法】Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// 【为什么自己 Init】EditMode 里 GameRoot 不跑，不自己初始化就撞 EnsureAssets 守卫。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Grid.States;
using NUnit.Framework;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Tests
{
    public class 网格连锁Tests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            AssetModule.Dispose();

            if (!ConfigModule.IsReady)
                ConfigModule.InitFromStreamingAssets();

            AssetModule.Init();
            ConfigModule.BindAssets();

            ReactionResolver.Initialize(ConfigModule.GetElementRules());
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            AssetModule.Dispose();
        }

        /// <summary>只记伤害与麻痹的替身；连锁关心的就是这两件事。</summary>
        private sealed class ChainTarget : IDamageable, IStunnable, IKnockBackable
        {
            public bool IsAlive { get; set; } = true;

            public Vector2 Position { get; set; }

            public int Hits;

            public float DamageTotal;

            public float StunSeconds;

            public void TakeDamage(in Damage damage)
            {
                Hits++;
                DamageTotal += damage.Amount;
            }

            public void ApplyStun(float seconds)
            {
                StunSeconds = seconds;
            }

            public void ApplyKnockback(Vector2 impulse)
            {
            }
        }

        private static GridLogic NewGrid(EnemyCellRegistry registry)
        {
            return new GridLogic(
                new GridGeometry(Vector2.zero, 1f),
                ConfigModule.GetAllTileStates(),
                CreateState,
                ConfigModule.GetDuoReactions(),
                registry);
        }

        private static ITileState CreateState(TileStateType id)
        {
            TileStateSpec spec = ConfigModule.TryGetTileState(id);

            return spec != null ? new TableTileState(spec) : null;
        }

        /// <summary>登记一条从 origin 起沿 +x 的水线，长度 count。</summary>
        private static void BuildWaterLine(GridLogic grid, Vector3Int origin, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var cell = new Vector3Int(origin.x + i, origin.y, origin.z);

                grid.RegisterCell(cell);
                grid.SwitchTileState(cell, TileStateType.BasicWater);
            }
        }

        /// <summary>① 连锁导电：远端格必须同帧受击，且麻痹时长与表一致。</summary>
        [Test]
        public void 连锁导电沿水网全网同帧受击()
        {
            TileChainReactor.Clear();

            var registry = new EnemyCellRegistry();

            GridLogic grid = NewGrid(registry);

            var origin = new Vector3Int(0, 0, 0);

            BuildWaterLine(grid, origin, 6);

            // 让 0 号格成为基础电源（水砸电源 → 导电区 + 连锁）
            grid.SwitchTileState(origin, TileStateType.BasicElectricity);

            var far = new Vector3Int(5, 0, 0);

            var farTarget = new ChainTarget { Position = grid.Geometry.CellCenter(far) };

            registry.Register(far, farTarget);

            bool changed = grid.OnBallHit(origin, BallType.Water);

            Assert.IsTrue(changed, "水砸基础电源必须产生变化");
            Assert.AreEqual(TileStateType.ConductZone, grid.StateOf(origin));
            Assert.AreEqual(1, farTarget.Hits, "远端格必须同帧受击：连锁要真的泛洪，不是只写一列标记");
            Assert.AreEqual(1f, farTarget.DamageTotal, 1e-4f, "连锁电击每格 1 点");
            Assert.AreEqual(1.5f, farTarget.StunSeconds, 1e-4f, "连锁电击麻痹 1.5 秒");
        }

        /// <summary>② 步数上限：环状水网不许无限扩散。</summary>
        [Test]
        public void 环状水网不会无限扩散()
        {
            TileChainReactor.Clear();

            var registry = new EnemyCellRegistry();

            GridLogic grid = NewGrid(registry);

            var origin = new Vector3Int(0, 0, 0);

            // 造一个 8×8 的水面（64 格），远超 MaxChainSteps=32
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var cell = new Vector3Int(x, y, 0);

                    grid.RegisterCell(cell);
                    grid.SwitchTileState(cell, TileStateType.BasicWater);
                }
            }

            grid.SwitchTileState(origin, TileStateType.BasicElectricity);

            // 把替身铺满整片：任何一格被重复电击都会被数出来
            var targets = new List<ChainTarget>();

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var target = new ChainTarget { Position = grid.Geometry.CellCenter(new Vector3Int(x, y, 0)) };

                    registry.Register(new Vector3Int(x, y, 0), target);

                    targets.Add(target);
                }
            }

            Assert.DoesNotThrow(() => grid.OnBallHit(origin, BallType.Water), "连锁泛洪不许抛异常");

            int hitCells = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Hits > 0) hitCells++;
            }

            Assert.LessOrEqual(
                hitCells,
                TileChainReactor.MaxChainSteps,
                $"连锁波及格数必须被 MaxChainSteps={TileChainReactor.MaxChainSteps} 截断：环状水网会无限扩散");

            Assert.Greater(hitCells, 1, "连锁至少要走几步：一格就停说明泛洪根本没启动");
        }

        /// <summary>③ 燎原爆燃：按 0.2s 步长逐圈蔓延，烧穿后回空地。</summary>
        [Test]
        public void 火海沿植物按步长蔓延并烧穿()
        {
            TileChainReactor.Clear();

            var registry = new EnemyCellRegistry();

            GridLogic grid = NewGrid(registry);

            var origin = new Vector3Int(0, 0, 0);

            // 一条 4 格的荆棘带
            for (int i = 0; i < 4; i++)
            {
                var cell = new Vector3Int(i, 0, 0);

                grid.RegisterCell(cell);
                grid.SwitchTileState(cell, TileStateType.BasicPlant);
            }

            var far = new Vector3Int(3, 0, 0);

            // 基础植物区 + 纯水 → 普通泥浆，所以水球砸不出火；火海只能由二级反应（火 × 植物）
            // 或火种本身产生。这里直接把起点摆成火海，只测蔓延引擎本身的那两条语义。
            grid.SwitchTileState(origin, TileStateType.FlameField);

            TileChainReactor.Trigger(grid, origin, TileStateType.FlameField);

            Assert.AreEqual(TileStateType.FlameField, grid.StateOf(origin), "火海起步格保持火海");

            float now = 0f;

            // 推进 1 秒：0.2s 一步，至少能把两步之外的格子烧到
            for (int i = 0; i < 60; i++)
            {
                now += 1f / 60f;

                grid.Tick(now, 1f / 60f);
            }

            Assert.AreEqual(
                TileStateType.FlameField,
                grid.StateOf(far),
                "火海必须沿植物带逐圈蔓延到远端：不蔓延等于「燎原」只是文案");

            // 火海存活 3 秒，烧完回空地
            for (int i = 0; i < 60 * 4; i++)
            {
                now += 1f / 60f;

                grid.Tick(now, 1f / 60f);
            }

            Assert.AreEqual(
                TileStateType.Normal,
                grid.StateOf(far),
                "火海到期后必须烧穿成空地：烧完还留着火，通路永远打不开");
        }

        /// <summary>④ 二级元素反应：火种与电种相邻时激发蒸汽（等离子爆轰），并打出 4 点伤害。</summary>
        [Test]
        public void 火种与电种相邻激发二级反应()
        {
            TileChainReactor.Clear();

            var registry = new EnemyCellRegistry();

            GridLogic grid = NewGrid(registry);

            var fire = new Vector3Int(0, 0, 0);
            var elec = new Vector3Int(1, 0, 0);

            grid.RegisterCell(fire);
            grid.RegisterCell(elec);

            grid.SwitchTileState(fire, TileStateType.BasicFire);
            grid.SwitchTileState(elec, TileStateType.BasicElectricity);

            var target = new ChainTarget { Position = grid.Geometry.CellCenter(fire) };

            registry.Register(fire, target);

            // 入口就是"球落地后判四邻有没有能配对的元素发生器"
            TileChainReactor.TriggerDuo(grid, fire, ConfigModule.GetDuoReactions());

            Assert.AreEqual(
                TileStateType.Steam,
                grid.StateOf(fire),
                "火 + 电 → 蒸汽（等离子爆轰）：二级反应没激发说明 element_duo_reaction 这条链断了");

            // 钉 4 而不是 ≥4：蒸汽既不可燃也不导通，这一行的 trigger_chain 已改为 FALSE，
            // 多出来的那 1 点只可能来自"泛洪落在非导体起点上"的那次凭空电击。
            Assert.AreEqual(4f, target.DamageTotal, 1e-3f, "等离子爆轰的瞬发伤害是 4 点，不多不少");
        }
    }
}
