using System.Collections.Generic;
using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>二级元素反应查询表：装配期建成双向字典，帧内 O(1) 零分配</summary>
    /// <remarks>成对无序，所以 (A,B) 与 (B,A) 指向同一条；同对重复配置由构造期直接报出，不静默后写覆盖</remarks>
    public sealed class DuoReactionCatalog
    {
        private readonly Dictionary<(TileStateType, TileStateType), DuoReactionSpec> _map;

        public DuoReactionCatalog(IReadOnlyList<DuoReactionSpec> rules)
        {
            int count = rules?.Count ?? 0;

            _map = new Dictionary<(TileStateType, TileStateType), DuoReactionSpec>(count * 2);

            for (int i = 0; i < count; i++)
            {
                DuoReactionSpec rule = rules[i];

                Add(rule.ElemA, rule.ElemB, rule);
                Add(rule.ElemB, rule.ElemA, rule);
            }
        }

        public int Count => _map.Count;

        /// <summary>查两个地貌能否发生二级反应；不含同种地貌自反应</summary>
        public bool TryGet(TileStateType a, TileStateType b, out DuoReactionSpec rule)
        {
            return _map.TryGetValue((a, b), out rule);
        }

        private void Add(TileStateType a, TileStateType b, DuoReactionSpec rule)
        {
            if (_map.ContainsKey((a, b)))
            {
                UnityEngine.Debug.LogError(
                    $"[Config] element_duo_reaction 里 ({a}, {b}) 出现了两次（id={rule.Id}）：后者会被忽略。");

                return;
            }

            _map[(a, b)] = rule;
        }
    }
}
