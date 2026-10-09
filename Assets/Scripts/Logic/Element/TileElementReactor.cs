using System.Collections.Generic;
using UnityEngine;
using cfg.dso;
using DeepseaOil.Data;

namespace DeepseaOil.Logic.Element
{
    /// 一次元素反应的结果：切到哪个状态 ＋ 进格提交的效果清单
    public readonly struct ElementReaction
    {
        public readonly TileStateType Next;

        public readonly IReadOnlyList<TileEffectValue> Effects;

        /// 是否有规则真的命中，false = 无规则命中
        public readonly bool Matched;

        public ElementReaction(TileStateType next, IReadOnlyList<TileEffectValue> effects, bool matched)
        {
            Next = next;
            Effects = effects;
            Matched = matched;
        }
    }

    /// 元素层端口，持每格元素，负责"球元素 ⊕ 地形元素 → 查规则 → 结果"
    /// 元素是格子属性，不是状态的一部分。地形改写走 SetElement。
    public interface IElementReactor
    {
        /// 取某格当前元素，无记录时给全零元素
        ElementValue GetElement(Vector3Int cell);

        /// 改写某格元素，全零时摘掉该格记录
        void SetElement(Vector3Int cell, in ElementValue element);

        void FlushStateElement(Vector3Int cell, in TileStateSpec spec);

        /// 结算一次落地：读该格元素 → 与球元素合成 → 写回该格 → 查规则
        ElementReaction React(Vector3Int cell, in ElementValue ballElement, in TileStateSpec currentSpec);
    }

    /// 元素层实现：元素合成 ElementCombiner ＋ 规则匹配 ReactionResolver ＋ 每格元素的持有
    /// 本层不认识格子层，只吃格坐标 ＋ 两份元素 ＋ 状态包装件，产出下一状态 ＋ 效果清单。纯 C#，不碰 MonoBehaviour / Time / Physics2D。
    public sealed class TileElementReactor : IElementReactor
    {
        private readonly Dictionary<Vector3Int, ElementValue> _elements = new();

        private readonly IReadOnlyList<ElementRuleSpec> _rules;

        /// 元素反应规则，顺序即优先级；null 或空时任何反应都不发生
        public TileElementReactor(IReadOnlyList<ElementRuleSpec> rules)
        {
            _rules = rules;
        }

        /// 当前有元素记录的格数，诊断用
        public int ElementCellCount => _elements.Count;

        public ElementValue GetElement(Vector3Int cell)
        {
            return _elements.TryGetValue(cell, out ElementValue value) ? value : default;
        }

        public void SetElement(Vector3Int cell, in ElementValue element)
        {
            if (element.IsEmpty)
            {
                _elements.Remove(cell);
                return;
            }

            _elements[cell] = element;
        }

        /// 切进某状态时把该状态元素刷到格上作初值
        /// 常规格表自己的元素也要刷得上去，那片地的脾性写在 tile_state 的 Normal 行里；空元素才摘记录，留全零条目只会让常驻内存长大。
        public void FlushStateElement(Vector3Int cell, in TileStateSpec spec)
        {
            SetElement(cell, spec.Element);
        }

        /// 结算一次落地并写回该格元素
        /// 规则未命中且状态不变时调用方不刷新初值，须自行收尾；合成后的元素先写回再匹配，命中的规则会把该状态的元素初值重新刷上。
        public ElementReaction React(Vector3Int cell, in ElementValue ballElement, in TileStateSpec currentSpec)
        {
            ElementValue old = GetElement(cell);

            ElementValue combined = ElementCombiner.Combine(in ballElement, in old);

            SetElement(cell, in combined);

            ReactionMatch match = ReactionResolver.Match(_rules, in combined);

            // 诊断通道：开关关着时不构造字符串，热路径零分配。
            ReactionResolver.LogTrace(new ReactionTrace(in old, in ballElement, in combined, in match));

            if (!match.Matched)
            {
                return new ElementReaction(TileStateType.None, null, false);
            }

            return new ElementReaction(match.Result, match.Effects, true);
        }

        /// 清空全部元素记录，随格子复位
        public void Clear()
        {
            _elements.Clear();
        }
    }
}
