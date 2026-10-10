using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>一个格子状态的取值边界：持有 tile_state 表行，只认"地面留下什么"</summary>
    /// <remarks>落地瞬间的冲击归 element_rule（ElementRuleSpec），地面残留归这里 —— 两张表不重叠、不互斥；行不对外暴露</remarks>
    public sealed class TileStateSpec
    {
        private readonly TileState _row;

        public TileStateSpec(TileState row)
        {
            _row = row;
        }

        public TileStateType Id => _row.Id;

        public string Name => _row.Name;

        /// <summary>存活秒数，&lt;=0=永久（只能被别的状态顶掉）</summary>
        public float Duration => _row.Duration;

        /// <summary>踩在上面的移速倍率，1=不减速；消费者按物理帧续期，&lt;1 才需要下发</summary>
        public float SlowRate => _row.SlowRate;

        /// <summary>每秒伤害，0=无伤害</summary>
        public int DotDamage => _row.DotDamage;

        /// <summary>是否物理阻挡墙体；表现层需要时按此挂碰撞体，逻辑层不做寻路</summary>
        public bool IsObstacle => _row.IsObstacle;

        /// <summary>是否作为网格连锁的导通体（水/泥浆为真）；TileChainReactor 泛洪的唯一判据</summary>
        public bool IsConductor => _row.IsConductor;
    }
}
