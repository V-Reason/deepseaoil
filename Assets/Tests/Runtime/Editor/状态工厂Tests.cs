// ---------------------------------------------------------------------------
// 状态工厂一致性 · 行为测试
//
// 守的是这一类"不报错、只是格子空着"的缺口：
//
//   1. 「表里写出来的每个地块」都必须能造出状态实例
//      tile_state 的每一行、element_rule 的每个结果地貌，都要能造出状态。
//      少了实现的表现不是异常，而是"反应发生了、格子却什么都没变"。
//
//   2. 切到一个"没有实现"的状态必须失败，且不留下空状态机
//
//   3. 水球砸空地要落到有实现的状态（不许停在 Normal —— 适配器把 Normal 当擦除）
//
//   4. 反应网自洽：element_rule 的 (原格, 球种) 组合不许重复；结果地貌都要有行
//      第 4 条是纯配置守卫：手改 xlsx 时最容易踩的两个坑
//
// 【跑法】Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// 【为什么要在 OneTimeSetUp 里自己 Init】测试是独立的 EditMode 程序集、也不进 PlayMode，
//   Init 链的调用方 GameRoot 在 EditMode 里根本不跑；不自己初始化就会撞 EnsureAssets 守卫。
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
    public class 状态工厂Tests
    {
        /// <summary>走完 Init 链的第二段：没有它，任何 <c>ConfigModule.GetXxx()</c> 都会抛。</summary>
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

        /// <summary>造一份"生产同款"的格子：geometry + 表里的状态清单 + 表驱动的状态工厂。</summary>
        private static GridLogic NewGrid()
        {
            var geometry = new GridGeometry(Vector2.zero, 1f);

            IReadOnlyList<TileStateSpec> specs = ConfigModule.GetAllTileStates();

            var grid = new GridLogic(geometry, specs, CreateState);

            grid.RegisterCell(Vector3Int.zero);

            return grid;
        }

        /// <summary>与 <c>CombatRoot.CreateTileState</c> 同一条判据：配置里有这一行就能造，没有就返回 <c>null</c>。</summary>
        private static ITileState CreateState(TileStateType id)
        {
            TileStateSpec spec = ConfigModule.TryGetTileState(id);

            return spec != null ? new TableTileState(spec) : null;
        }

        [Test]
        public void 配置里的每个状态都能造出实例()
        {
            IReadOnlyList<TileStateSpec> specs = ConfigModule.GetAllTileStates();

            Assert.Greater(specs.Count, 0, "tile_state 一行都没有：格子系统没有状态可切");

            var missing = new List<string>();

            for (int i = 0; i < specs.Count; i++)
            {
                TileStateType id = specs[i].Id;

                if (CreateState(id) == null) missing.Add($"{id}（id={(int)id}）");
            }

            Assert.IsEmpty(missing,
                "这些状态在 tile_state 里有行、却造不出实例（规则命中它们时格子会空着）：\n  " + string.Join("\n  ", missing));
        }

        [Test]
        public void 水球砸空地落到有贴图的状态()
        {
            GridLogic grid = NewGrid();

            var cell = Vector3Int.zero;

            bool changed = grid.OnBallHit(cell, BallType.Water);

            TileStateType landed = grid.StateOf(cell);

            Assert.IsTrue(changed, "水球落地没有产生状态转换");
            Assert.AreEqual(TileStateType.BasicWater, landed, "空地 + 纯水 → 基础水");
            Assert.IsNotNull(CreateState(landed), $"{landed} 没有实现：格子会空着");
        }

        [Test]
        public void 泥土球砸空地落到基础土()
        {
            GridLogic grid = NewGrid();

            var cell = Vector3Int.zero;

            Assert.IsTrue(grid.OnBallHit(cell, BallType.Earth), "土球落地没有产生状态转换");
            Assert.AreEqual(TileStateType.BasicEarth, grid.StateOf(cell), "空地 + 纯土 → 基础土");
        }

        [Test]
        public void 规则表命中的状态都有实现()
        {
            IReadOnlyList<ElementRuleSpec> rules = ConfigModule.GetElementRules();

            Assert.Greater(rules.Count, 0, "element_rule 一行都没有：投掷落地不会产生任何反应");

            var missing = new List<string>();

            for (int i = 0; i < rules.Count; i++)
            {
                TileStateType result = rules[i].ResultTile;

                // None = "不改动"，Normal = 落回常规：两者都不需要状态实现。
                if (result == TileStateType.None || result == TileStateType.Normal) continue;

                if (CreateState(result) != null) continue;

                missing.Add($"规则 {rules[i].Id} → {result}（id={(int)result}）");
            }

            Assert.IsEmpty(missing,
                "这些规则的结果地貌没有对应的状态实现：反应判定会通过、格子却什么都不变（最坏的一类静默）。\n  "
                + string.Join("\n  ", missing));
        }

        [Test]
        public void 反应规则的主键不重复()
        {
            IReadOnlyList<ElementRuleSpec> rules = ConfigModule.GetElementRules();

            var seen = new Dictionary<(TileStateType, BallType), int>();

            var dup = new List<string>();

            for (int i = 0; i < rules.Count; i++)
            {
                var key = (rules[i].SourceTile, rules[i].BallType);

                if (seen.TryGetValue(key, out int first))
                {
                    dup.Add($"({key.SourceTile}, {key.BallType})：id {first} 与 id {rules[i].Id}");
                    continue;
                }

                seen[key] = rules[i].Id;
            }

            Assert.IsEmpty(dup,
                "element_rule 里 (原格, 球种) 重复：ReactionResolver 按它建哈希表，重复会让后者被静默忽略。\n  "
                + string.Join("\n  ", dup));
        }

        [Test]
        public void 二级反应的地貌都有实现()
        {
            DuoReactionCatalog catalog = ConfigModule.GetDuoReactions();

            IReadOnlyList<SeedSpec> seeds = ConfigModule.GetAllSeeds();

            Assert.Greater(seeds.Count, 0, "seed 表一行都没有：战备配给无种子可发");

            for (int i = 0; i < seeds.Count; i++)
            {
                Assert.IsNotNull(
                    CreateState(seeds[i].SpawnTile),
                    $"种子 {seeds[i].Id} 的 spawn_tile={seeds[i].SpawnTile} 造不出状态：播下去会是空地");
            }

            Assert.Greater(catalog.Count, 0, "element_duo_reaction 一行都没有：二级元素连锁不会发生");
        }

        [Test]
        public void 没有实现的状态切不进去且不留空状态机()
        {
            GridLogic grid = NewGrid();

            Vector3Int cell = Vector3Int.zero;

            // 从表里挑一个真的没有行的枚举值：BasicIce 之后的新地貌里，用一个肯定没登记的值
            const TileStateType absent = (TileStateType)999;

            Assert.IsFalse(grid.SwitchTileState(cell, absent),
                "切到一个没有实现的状态必须返回 false");

            Assert.AreEqual(TileStateType.Normal, grid.StateOf(cell),
                "切换失败后格子状态必须保持原样（不能变成「有状态机但读作常规」）");

            Assert.IsTrue(grid.SwitchTileState(cell, TileStateType.Mud),
                "失败的那次不该污染状态机：随后切一个真有实现的状态仍应成功");

            Assert.AreEqual(TileStateType.Mud, grid.StateOf(cell));
        }
    }
}
