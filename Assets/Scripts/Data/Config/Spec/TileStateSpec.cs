using System.Collections.Generic;
using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>一个格子状态的取值边界：持有 tile_state 表行，暴露被消费的语义点，构造期把效果清单解析成已定值</summary>
    /// <remarks>行不对外暴露。效果清单是已定值的 TileEffectValue，档位在构造期由传入的解析器选好。</remarks>
    public sealed class TileStateSpec
    {
        private readonly TileState _row;

        /// <summary>已解析的效果清单，effects 与 effectValuePos 逐条配对、档位已选定</summary>
        public readonly IReadOnlyList<TileEffectValue> EnterEffects;

        /// <remarks>resolve=取"某效果第 pos 档"的解析器，null 时效果清单退化成"全是 None"（逻辑层单跑测试）</remarks>
        public TileStateSpec(TileState row, ElementRuleSpec.EffectResolver resolve = null)
        {
            _row = row;

            IReadOnlyList<TileEffectType> effects = row.Effects;
            IReadOnlyList<int> positions = row.EffectValuePos;

            int count = effects?.Count ?? 0;

            var resolved = new List<TileEffectValue>(count);

            for (int i = 0; i < count; i++)
            {
                int pos = positions != null && i < positions.Count ? positions[i] : 1;

                TileEffectValue effect = resolve != null ? resolve(effects[i], pos) : default;

                if (effect.Kind == TileEffectKind.None) continue;

                resolved.Add(effect);
            }

            EnterEffects = resolved;
        }

        public TileStateType Id => _row.Id;

        public string Name => _row.Name;

        /// <summary>持续时间（秒），&lt;=0=永久（只能被别的状态顶掉）</summary>
        public float Duration => _row.Duration;

        /// <summary>恒为 false（表已删掉这一列）</summary>
        public bool WillSpread => false;

        /// <summary>是否参与元素反应；本轮只读</summary>
        public bool CanReact => _row.CanReact;

        /// <summary>地形元素四件：温度/湿度/导电/标签，D9 初值来源</summary>
        public ElementValue Element => new ElementValue(ElementType.Environment, _row.Tags, _row.Temp, _row.Wet, _row.Cond);

        /// <summary>效果清单的触发节拍（秒），状态按此间隔重复提交 EnterEffects；0=每个 Tick 都提交。取各效果 interval 的最小正值。</summary>
        public float TickInterval
        {
            get
            {
                float interval = 0f;

                for (int i = 0; i < EnterEffects.Count; i++)
                {
                    float candidate = EnterEffects[i].Interval;

                    if (candidate <= 0f) continue;

                    if (interval <= 0f || candidate < interval) interval = candidate;
                }

                return interval;
            }
        }
    }
}
