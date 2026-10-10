using System.Collections.Generic;
using DeepseaOil.Data;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Logic.Element
{
    // 离散二元反应求解器：(原格地貌, 球种)
    // 纯函数：不读不写世界状态
    public static class ReactionResolver
    {
        private static Dictionary<(TileStateType, BallType), ElementRuleSpec> _rules;

        // 追踪开关：置 true 时每次落地都打印命中或未命中
        public static bool TraceEnabled;

        public static int RuleCount => _rules?.Count ?? 0;

        // 装配期初始化：建 (SourceTile, BallType) 双键字典
        // 重复键只报错不覆盖，先到的那行生效
        public static void Initialize(IReadOnlyList<ElementRuleSpec> rules)
        {
            Clear();

            if (rules == null || rules.Count == 0) return;

            _rules = new Dictionary<(TileStateType, BallType), ElementRuleSpec>(rules.Count);

            for (int i = 0; i < rules.Count; i++)
            {
                ElementRuleSpec rule = rules[i];

                var key = (rule.SourceTile, rule.BallType);

                if (_rules.ContainsKey(key))
                {
                    Debug.LogError(
                        $"[Reaction] element_rule 里 ({rule.SourceTile}, {rule.BallType}) 出现了两次（id={rule.Id}）：" +
                        "后者会被忽略，请检查源表。");

                    continue;
                }

                _rules[key] = rule;
            }
        }

        public static void Clear()
        {
            _rules = null;
        }

        /// <summary>裁决一次落地</summary>
        // 未命中走 Fallback：一级反应兜底，不做空转
        public static ReactionOutcome Resolve(TileStateType currentTile, BallType ball)
        {
            if (_rules != null && _rules.TryGetValue((currentTile, ball), out ElementRuleSpec rule))
            {
                var outcome = new ReactionOutcome(
                rule.ResultTile,
                rule.ImpactDamage,
                rule.ImpactKnockback,
                rule.ImpactStun,
                rule.TriggerChain,
                rule.ImpactRadius);

                LogTrace(currentTile, ball, rule);

                return outcome;
            }

            ReactionOutcome fallback = Fallback(currentTile, ball);

            LogTrace(currentTile, ball, null);

            return fallback;
        }

        // 表未命中的兜底：空地 + 水/土 才给基础地貌
        private static ReactionOutcome Fallback(TileStateType currentTile, BallType ball)
        {
            if (currentTile != TileStateType.Normal) return ReactionOutcome.Unchanged(currentTile);

            switch (ball)
            {
                case BallType.Water:
                    return new ReactionOutcome(TileStateType.BasicWater, 0, 0f, 0f, false, 1.0f);

                case BallType.Earth:
                    return new ReactionOutcome(TileStateType.BasicEarth, 0, 0f, 0f, false, 1.0f);

                default:
                    return ReactionOutcome.Unchanged(currentTile);
            }
        }

        private static void LogTrace(TileStateType currentTile, BallType ball, ElementRuleSpec rule)
        {
            if (!TraceEnabled) return;

            Debug.Log(rule != null
                ? $"[Reaction] {currentTile} + {ball} → {rule.ResultTile}（规则 #{rule.Id}，伤害 {rule.ImpactDamage}，击退 {rule.ImpactKnockback}，麻痹 {rule.ImpactStun}，连锁 {rule.TriggerChain}）"
                : $"[Reaction] {currentTile} + {ball} → 无规则命中（走兜底）");
        }
    }
}
