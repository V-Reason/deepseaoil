// ---------------------------------------------------------------------------
// 状态工厂一致性 · 行为测试
//
// 守的是这一类"不报错、只是格子空着"的缺口：
//
//   1. 「表里写出来的每个地貌」都必须能造出状态实例
//      tile_state 的每一行、element_rule 的每个结果地貌、二级反应的产物、种子的播种地貌。
//      少了实现的表现不是异常，而是"反应发生了、格子却什么都没变"。
//
//   2. 切到一个"没有行"的状态必须失败，且不留下空状态机
//
//   3. 投球要落到有实现的状态（不许停在 Normal —— 适配器把 Normal 当擦除）
//
//   4. 纯配置守卫：手改 xlsx 时最容易踩的两个坑 ——
//      (原格, 球种) 重复（字典静默后写覆盖）、trigger_chain 填在无方向的产物上。
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
        /// <summary>走完 Init 链的第二段：没有它，任何 ConfigModule.GetXxx() 都会抛。</summary>
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

        /// <summary>与 CombatRoot.CreateTileState 同一条判据：配置里有这一行就能造，没有就返回 null。</summary>
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
        public void 水球砸空地落到有实现的状态()
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
        public void 二级反应的产物地貌都有实现()
        {
            DuoReactionCatalog catalog = ConfigModule.GetDuoReactions();

            Assert.Greater(catalog.Rules.Count, 0, "element_duo_reaction 一行都没有：二级元素连锁不会发生");

            var missing = new List<string>();

            for (int i = 0; i < catalog.Rules.Count; i++)
            {
                DuoReactionSpec duo = catalog.Rules[i];

                if (duo.ResultTile == TileStateType.None || duo.ResultTile == TileStateType.Normal) continue;

                if (CreateState(duo.ResultTile) != null) continue;

                missing.Add($"反应 {duo.Id} → {duo.ResultTile}（id={(int)duo.ResultTile}）");
            }

            Assert.IsEmpty(missing,
                "这些二级反应的产物地貌没有对应的状态实现：两个发生器接通了、格子却什么都不变。\n  "
                + string.Join("\n  ", missing));
        }

        [Test]
        public void 种子的播种地貌都有实现()
        {
            IReadOnlyList<SeedSpec> seeds = ConfigModule.GetAllSeeds();

            Assert.Greater(seeds.Count, 0, "seed 表一行都没有：战备配给无种子可发");

            for (int i = 0; i < seeds.Count; i++)
            {
                Assert.IsNotNull(
                    CreateState(seeds[i].SpawnTile),
                    $"种子 {seeds[i].Id} 的 spawn_tile={seeds[i].SpawnTile} 造不出状态：播下去会是空地");
            }
        }

        /// <summary>连锁标记必须有可走的方向：结果地貌不可燃也不导通时，泛洪只会在受击格上凭空补一次电击。</summary>
        [Test]
        public void 连锁标记只出现在有方向的反应上()
        {
            var bad = new List<string>();

            IReadOnlyList<ElementRuleSpec> rules = ConfigModule.GetElementRules();

            for (int i = 0; i < rules.Count; i++)
            {
                if (!rules[i].TriggerChain) continue;

                if (HasChainDirection(rules[i].ResultTile)) continue;

                bad.Add($"element_rule id={rules[i].Id}：{rules[i].SourceTile} → {rules[i].ResultTile}");
            }

            DuoReactionCatalog catalog = ConfigModule.GetDuoReactions();

            for (int i = 0; i < catalog.Rules.Count; i++)
            {
                DuoReactionSpec duo = catalog.Rules[i];

                if (!duo.TriggerChain) continue;

                if (HasChainDirection(duo.ResultTile)) continue;

                bad.Add($"element_duo_reaction id={duo.Id}：{duo.ElemA} + {duo.ElemB} → {duo.ResultTile}");
            }

            Assert.IsEmpty(bad,
                "这些行 trigger_chain=TRUE，但产物既不可燃也不导通 —— 泛洪无处可去，"
                + "打开泛洪只会让受击格白白多吃一次电击：\n  " + string.Join("\n  ", bad));
        }

        /// <summary>连锁的两个方向，与 TileChainReactor 的分派同源：可燃/火海走燎原，导通格走导电。</summary>
        private static bool HasChainDirection(TileStateType result)
        {
            if (result == TileStateType.FlameField || TileChainReactor.IsFlammable(result)) return true;

            TileStateSpec spec = ConfigModule.TryGetTileState(result);

            return spec != null && spec.IsConductor;
        }

        [Test]
        public void 没有实现的状态切不进去且不留空状态机()
        {
            GridLogic grid = NewGrid();

            Vector3Int cell = Vector3Int.zero;

            // None 是唯一"枚举里有、tile_state 里没有行"的成员，也是"不改地"的语义值
            const TileStateType absent = TileStateType.None;

            Assert.IsFalse(grid.SwitchTileState(cell, absent),
                "切到一个没有行、也没有实现的状态必须返回 false");

            Assert.AreEqual(TileStateType.Normal, grid.StateOf(cell),
                "切换失败后格子状态必须保持原样（不能变成「有状态机但读作常规」）");

            Assert.IsTrue(grid.SwitchTileState(cell, TileStateType.Mud),
                "失败的那次不该污染状态机：随后切一个真有实现的状态仍应成功");

            Assert.AreEqual(TileStateType.Mud, grid.StateOf(cell));
        }
    }
}
