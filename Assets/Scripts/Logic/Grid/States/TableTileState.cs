using DeepseaOil.Data;
using cfg.dso;

namespace DeepseaOil.Logic.Grid.States
{
    /// <summary>表驱动地块状态：只管"这块地能活多久、每帧对格上目标做什么"</summary>
    /// <remarks>行为只来自 TileStateSpec 的三个数（slow_rate/dot_damage/duration），不再有可执行效果清单；减速续命窗口必须是本帧 Δt（0 会被物理帧扣穿）；DoT 按 1 秒攒拍、进格那下由 GridLogic.OnActorEnterCell 补。</remarks>
    public sealed class TableTileState : ITileState
    {
        private readonly TileStateSpec _spec;

        private float _elapsed;

        private float _dotAccumulator;

        /// <summary>覆盖后的存活秒数；未覆盖时等于表值</summary>
        private float _duration;

        public TableTileState(TileStateSpec spec)
        {
            _spec = spec;
            _duration = spec.Duration;
        }

        public TileStateType Id => _spec.Id;

        public void OverrideDuration(float seconds)
        {
            if (seconds > 0f) _duration = seconds;
        }

        /// <remarks>进入即开始计时；同状态不重入，所以反复投球不会刷新泥浆计时</remarks>
        public void OnEnter(in TileContext ctx)
        {
            _elapsed = 0f;
            _dotAccumulator = 0f;

            ctx.Scheduler?.ScheduleTick(ctx.Cell);
        }

        public void OnTick(in TileContext ctx)
        {
            // 先结算再判到期：到期那一帧也要算上本格效果
            ApplyTick(in ctx);

            if (_duration > 0f)
            {
                _elapsed += ctx.DeltaTime;

                if (_elapsed >= _duration)
                {
                    // 到期落回常规；转换在本帧 Tick 循环之后统一结算，不会立刻重入 OnExit
                    ctx.Scheduler?.Transition(ctx.Cell, TileStateType.Normal);

                    return;
                }
            }

            ctx.Scheduler?.ScheduleTick(ctx.Cell);
        }

        public void OnExit(in TileContext ctx)
        {
        }

        /// <summary>每帧结算：减速续命 + DoT 攒拍</summary>
        private void ApplyTick(in TileContext ctx)
        {
            ITileResolver resolver = ctx.Resolver;

            if (resolver == null) return;

            if (_spec.SlowRate < 1f) resolver.ApplySlowCell(ctx.Cell, _spec.SlowRate, ctx.DeltaTime);

            if (_spec.DotDamage <= 0) return;

            _dotAccumulator += ctx.DeltaTime;

            // while 而非 if：掉帧时按次数补齐，不吞伤害
            while (_dotAccumulator >= 1f)
            {
                _dotAccumulator -= 1f;

                resolver.DealCell(ctx.Cell, _spec.DotDamage);
            }
        }
    }
}
