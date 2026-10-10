using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>一条二级元素反应：两个发生器地貌 → 激发产物 + 广域波及 + 存续秒数</summary>
    public sealed class DuoReactionSpec
    {
        private readonly ElementDuoReaction _row;

        public DuoReactionSpec(ElementDuoReaction row)
        {
            _row = row;
        }

        public int Id => _row.Id;

        public TileStateType ElemA => _row.ElemA;

        public TileStateType ElemB => _row.ElemB;

        public TileStateType ResultTile => _row.ResultTile;

        public int ImpactDamage => _row.ImpactDamage;

        public float ImpactKnockback => _row.ImpactKnockback;

        public float ResultDuration => _row.ResultDuration;

        public float EffectRadius => _row.EffectRadius;

        public bool TriggerChain => _row.TriggerChain;
    }
}
