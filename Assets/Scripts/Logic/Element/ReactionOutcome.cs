using cfg.dso;

namespace DeepseaOil.Logic.Element
{
    /// <summary>一次反应的原子产物：新地块 + 落地瞬间冲击 + 连锁标记；NextTile 等于原状态即表示地形不变、只结算冲击</summary>
    public readonly struct ReactionOutcome
    {
        public readonly TileStateType NextTile;

        public readonly int InstantDamage;

        public readonly float KnockbackCells;

        public readonly float StunSeconds;

        public readonly bool TriggerChain;

        public readonly float ImpactRadius;

        public ReactionOutcome(
                    TileStateType nextTile,
                    int instantDamage,
                    float knockbackCells,
                    float stunSeconds,
                    bool triggerChain,
                    float impactRadius = 1.0f) // 默认 1.0
        {
            NextTile = nextTile;
            InstantDamage = instantDamage;
            KnockbackCells = knockbackCells;
            StunSeconds = stunSeconds;
            TriggerChain = triggerChain;
            ImpactRadius = impactRadius > 0f ? impactRadius : 1.0f;
        }

        /// <summary>不改地形、不打任何冲击</summary>
        public static ReactionOutcome Unchanged(TileStateType current)
            => new ReactionOutcome(current, 0, 0f, 0f, false, 1.0f);
    }
}
