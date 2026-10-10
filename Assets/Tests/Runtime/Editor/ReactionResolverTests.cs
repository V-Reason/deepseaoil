// ---------------------------------------------------------------------------
// 元素反应求解器 · 行为测试
//
// 守的是三类"不报错、只是玩法不成立"的缺口：
//   ① 表命中必须优先于兜底：兜底是给"表缺行"用的保险，不能反过来把表吃掉
//   ② 未命中必须是"地形不变 + 零冲击"：任何一项非零都会让空过变成莫名的伤害
//   ③ (原格, 球种) 重复必须被报出来：字典会静默后者覆盖前者，这种配置事故最难查
//
// 【跑法】Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Element;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using cfg.dso;

namespace DeepseaOil.Tests
{
    public class ReactionResolverTests
    {
        [TearDown]
        public void TearDown()
        {
            // 静态索引：用例之间必须互不污染
            ReactionResolver.Clear();
        }

        /// <summary>造一条只装了少量规则的求解器。</summary>
        private static void Init(params ElementRuleSpec[] rules)
        {
            ReactionResolver.Initialize(new List<ElementRuleSpec>(rules));
        }

        [Test]
        public void 表命中时优先于兜底()
        {
            Init(RowFactory.ElementRuleSpecOf(1, (int)TileStateType.Normal, (int)BallType.Water, (int)TileStateType.Mud, 7, 2f, 1.5f, true));

            ReactionOutcome outcome = ReactionResolver.Resolve(TileStateType.Normal, BallType.Water);

            // 表里写的就是泥浆，兜底会把它变成基础水 —— 命中优先是这条用例的全部意义
            Assert.AreEqual(TileStateType.Mud, outcome.NextTile, "表命中被兜底盖掉了");
            Assert.AreEqual(7, outcome.InstantDamage);
            Assert.AreEqual(2f, outcome.KnockbackCells, 1e-4f);
            Assert.AreEqual(1.5f, outcome.StunSeconds, 1e-4f);
            Assert.IsTrue(outcome.TriggerChain);
        }

        [Test]
        public void 表缺行时空地兜底成基础水与基础土()
        {
            Init();

            Assert.AreEqual(TileStateType.BasicWater, ReactionResolver.Resolve(TileStateType.Normal, BallType.Water).NextTile);
            Assert.AreEqual(TileStateType.BasicEarth, ReactionResolver.Resolve(TileStateType.Normal, BallType.Earth).NextTile, "空地 + 土 → 基础土");
        }

        [Test]
        public void 兜底不产生任何冲击()
        {
            Init();

            ReactionOutcome outcome = ReactionResolver.Resolve(TileStateType.Normal, BallType.Water);

            Assert.AreEqual(0, outcome.InstantDamage, "兜底不该打伤害");
            Assert.AreEqual(0f, outcome.KnockbackCells, 1e-4f, "兜底不该击退");
            Assert.AreEqual(0f, outcome.StunSeconds, 1e-4f, "兜底不该麻痹");
            Assert.IsFalse(outcome.TriggerChain, "兜底不该触发连锁");
        }

        [Test]
        public void 未命中时地形与冲击都不动()
        {
            // 只配一条与查询无关的规则
            Init(RowFactory.ElementRuleSpecOf(1, (int)TileStateType.BasicWater, (int)BallType.Earth, (int)TileStateType.Mud));

            ReactionOutcome outcome = ReactionResolver.Resolve(TileStateType.BasicFire, BallType.Earth);

            Assert.AreEqual(TileStateType.BasicFire, outcome.NextTile, "未命中必须维持原状（NextTile = 原状态）");
            Assert.AreEqual(0, outcome.InstantDamage);
            Assert.AreEqual(0f, outcome.KnockbackCells, 1e-4f);
            Assert.AreEqual(0f, outcome.StunSeconds, 1e-4f);
            Assert.IsFalse(outcome.TriggerChain);
        }

        [Test]
        public void 重复键会被报出来且不静默覆盖()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape("[Reaction] element_rule 里 (BasicFire, Water) 出现了两次（id=2）") + ".*"));

            Init(
                RowFactory.ElementRuleSpecOf(1, (int)TileStateType.BasicFire, (int)BallType.Water, (int)TileStateType.Steam),
                RowFactory.ElementRuleSpecOf(2, (int)TileStateType.BasicFire, (int)BallType.Water, (int)TileStateType.Mud));

            // 先入为主：第一条留下
            Assert.AreEqual(TileStateType.Steam, ReactionResolver.Resolve(TileStateType.BasicFire, BallType.Water).NextTile);
            Assert.AreEqual(1, ReactionResolver.RuleCount, "重复键不该进索引");
        }

        [Test]
        public void 初始化可重复调用且清空生效()
        {
            Init(RowFactory.ElementRuleSpecOf(1, (int)TileStateType.BasicFire, (int)BallType.Water, (int)TileStateType.Steam));

            Assert.AreEqual(1, ReactionResolver.RuleCount);

            Init();

            Assert.AreEqual(0, ReactionResolver.RuleCount, "重复 Initialize 必须是重建而不是叠加");

            // 索引已空 → 走兜底
            Assert.AreEqual(TileStateType.BasicWater, ReactionResolver.Resolve(TileStateType.Normal, BallType.Water).NextTile);
        }

        [Test]
        public void 真表每条规则都能查得到()
        {
            AssetModule.Dispose();

            if (!ConfigModule.IsReady) ConfigModule.InitFromStreamingAssets();

            AssetModule.Init();
            ConfigModule.BindAssets();

            IReadOnlyList<ElementRuleSpec> rules = ConfigModule.GetElementRules();

            ReactionResolver.Initialize(rules);

            for (int i = 0; i < rules.Count; i++)
            {
                ReactionOutcome outcome = ReactionResolver.Resolve(rules[i].SourceTile, rules[i].BallType);

                Assert.AreEqual(
                    rules[i].ResultTile,
                    outcome.NextTile,
                    $"规则 {rules[i].Id} 在真表里查不出来：索引键与表不一致");

                Assert.AreEqual(rules[i].ImpactDamage, outcome.InstantDamage, $"规则 {rules[i].Id} 的伤害没传到结果");
            }

            AssetModule.Dispose();
        }
    }
}
