using System.Collections.Generic;
using UnityEngine;
using cfg.dso;
using DeepseaOil.Data;

namespace DeepseaOil.Logic.Element
{
    /// <remarks>未命中恒为 false/None/null/-1，不兜底</remarks>
    public readonly struct ReactionMatch
    {
        public readonly bool Matched;

        public readonly TileStateType Result;

        public readonly IReadOnlyList<TileEffectValue> Effects;

        public readonly int RuleIndex;

        public readonly int Priority;

        public ReactionMatch(
            bool matched,
            TileStateType result,
            IReadOnlyList<TileEffectValue> effects,
            int ruleIndex,
            int priority)
        {
            Matched = matched;
            Result = result;
            Effects = effects;
            RuleIndex = ruleIndex;
            Priority = priority;
        }

        public int EffectCount => Effects?.Count ?? 0;

        public static ReactionMatch None => new ReactionMatch(false, TileStateType.None, null, -1, 0);
    }

    /// <remarks>值类型零分配；原格元素是合成前的旧值</remarks>
    public readonly struct ReactionTrace
    {
        public readonly ElementValue OldTile;

        public readonly ElementValue Ball;

        public readonly ElementValue Combined;

        public readonly ReactionMatch Match;

        public ReactionTrace(in ElementValue oldTile, in ElementValue ball, in ElementValue combined, in ReactionMatch match)
        {
            OldTile = oldTile;
            Ball = ball;
            Combined = combined;
            Match = match;
        }

        public string Describe()
        {
            string verdict = Match.Matched
                ? $"命中规则 #{Match.Priority} [生成地块: {Match.Result}({(int)Match.Result}), 效果数: {Match.EffectCount}]"
                : "无规则命中";

            return $"[Reaction] 原格元素 (T:{OldTile.Temperature}, W:{OldTile.Wet}, C:{OldTile.Conductivity}, Tags:{OldTile.Tags}) " +
                   $"+ 球元素 (T:{Ball.Temperature}, W:{Ball.Wet}, C:{Ball.Conductivity}, Tags:{Ball.Tags}) " +
                   $"= 合成元素 (T:{Combined.Temperature}, W:{Combined.Wet}, C:{Combined.Conductivity}) -> {verdict}";
        }
    }

    /// <summary>在一份元素上找第一条命中的规则，纯函数</summary>
    /// <remarks>顺序即优先级，清单顺序就是判定顺序（ConfigModule.GetElementRules() 保证）；无命中返回 None，调用方不做事；关闭时零分配</remarks>
    public static class ReactionResolver
    {
        /// <summary>追踪开关：置 true 每次落地输出一行 [Reaction]，正式构建保持 false</summary>
        public static bool TraceEnabled;

        public static ReactionMatch Match(IReadOnlyList<ElementRuleSpec> rules, in ElementValue element)
        {
            if (rules == null || rules.Count == 0) return ReactionMatch.None;

            for (int i = 0; i < rules.Count; i++)
            {
                if (!rules[i].Match(in element)) continue;

                return new ReactionMatch(true, rules[i].ResultTileType, rules[i].Effects, i, rules[i].Priority);
            }

            return ReactionMatch.None;
        }

        public static void LogTrace(in ReactionTrace trace)
        {
            if (!TraceEnabled) return;

            Debug.Log(trace.Describe());
        }
    }
}
