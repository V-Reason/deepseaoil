using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>一条元素反应规则：原格地貌 + 投入球种 → 生成地貌 + 落地瞬间冲击</summary>
    /// <remarks>无顺序语义、无优先级：ReactionResolver 按 (SourceTile, BallType) 建哈希表做 O(1) 查询；匹配条件全在这一对主键里，不再有标签/温湿度区间</remarks>
    public sealed class ElementRuleSpec
    {
        private readonly ElementRule _row;

        public ElementRuleSpec(ElementRule row)
        {
            _row = row;
        }

        public int Id => _row.Id;

        /// <summary>作用的地块原状态</summary>
        public TileStateType SourceTile => _row.SourceTile;

        public BallType BallType => _row.BallType;

        /// <summary>生成的新地块；等于 SourceTile 表示"地形不变、只结算冲击"</summary>
        public TileStateType ResultTile => _row.ResultTile;

        /// <summary>落地瞬间伤害，当帧结算（不再等第一次 Tick）</summary>
        public int ImpactDamage => _row.ImpactDamage;

        /// <summary>落地瞬间击退距离，格</summary>
        public float ImpactKnockback => _row.ImpactKnockback;

        /// <summary>落地瞬间麻痹秒数</summary>
        public float ImpactStun => _row.ImpactStun;

        /// <summary>是否触发网格连锁泛洪</summary>
        public bool TriggerChain => _row.TriggerChain;
    }
}
