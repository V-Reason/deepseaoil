using DeepseaOil.Data;
using cfg.dso;

namespace DeepseaOil.Logic.Grid.States
{
    /// <summary>表驱动地块状态：行为全来自 TileStateSpec，效果清单在数据层已解析成定值，这里只按节拍提交并到点到期</summary>
    /// <remarks>减速/伤害是推而非被查询：OnEnter/OnTick 只提交清单效果，找人与施加由结算口完成；interval=0 即每帧触发，泥浆靠每帧续命表达持续；DoT 扣血节奏在本类累加，除 GridLogic 外零新增依赖；元素由元素层在状态切换时刷；计时用一次性 Tick 队列而非协程，暂停时 DeltaTime 为 0 累加不动。</remarks>
    public sealed class TableTileState : ITileState
    {
        private readonly TileStateSpec _spec;

        private float _elapsed;

        /// <summary>节拍累加，TickInterval>0 才有意义</summary>
        private float _tickAccumulator;

        private float _dotAccumulator;

        public TableTileState(TileStateSpec spec)
        {
            _spec = spec;
        }

        public TileStateType Id => _spec.Id;

        /// <remarks>进入即开始计时；同状态不重入</remarks>
        public void OnEnter(in TileContext ctx)
        {
            _elapsed = 0f;
            _tickAccumulator = 0f;
            _dotAccumulator = 0f;

            // 进格这一拍按“一帧”算：进格冲击已打过一次性效果，这里只给 DoT 累加器起头，不当帧补伤害
            SubmitAll(in ctx, ctx.DeltaTime);

            ctx.Scheduler?.ScheduleTick(ctx.Cell);
        }

        public void OnTick(in TileContext ctx)
        {
            // 先结算再判到期：到期那一帧也要算上本格效果
            ApplyTick(in ctx);

            if (_spec.Duration > 0f)
            {
                _elapsed += ctx.DeltaTime;

                if (_elapsed >= _spec.Duration)
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

        /// <summary>按节拍提交效果清单；清单为空也每帧提交，状态还要判到期</summary>
        private void ApplyTick(in TileContext ctx)
        {
            float interval = _spec.TickInterval;

            if (interval <= 0f)
            {
                SubmitAll(in ctx, ctx.DeltaTime);
                return;
            }

            _tickAccumulator += ctx.DeltaTime;

            // while 而非 if：一帧跨多拍按次数补齐，掉帧不吞效果
            while (_tickAccumulator >= interval)
            {
                _tickAccumulator -= interval;

                SubmitAll(in ctx, interval);
            }
        }

        /// <summary>清单效果逐个交给结算口</summary>
        // 本次提交覆盖的时长（秒）：每帧提交为一帧，按节拍提交为 TickInterval
        // ctx.Resolver 为 null（逻辑层单跑测试）静默跳过；一次性效果刻意跳过，由进格冲击 ApplyEnterImpact 与 OnActorEnterCell 各补一次，逐帧提交会变成每秒 60 次掉血
        private void SubmitAll(in TileContext ctx, float elapsed)
        {
            ITileResolver resolver = ctx.Resolver;

            if (resolver == null) return;

            for (int i = 0; i < _spec.EnterEffects.Count; i++)
            {
                TileEffectValue effect = _spec.EnterEffects[i];

                if (effect.IsEnterOnly) continue;

                if (effect.Kind == TileEffectKind.DamageOverTime)
                {
                    if (ShouldFire(effect.Interval, elapsed, ref _dotAccumulator))
                        resolver.Apply(ctx.Cell, in effect);

                    continue;
                }

                // 续命型（表里没给时长）：把本次提交的窗口当它的续命时长；生产者窗口是渲染帧或提交节拍，消费者按物理帧扣时，只续单帧会被扣穿
                TileEffectValue value = effect.Seconds <= 0f && effect.Kind == TileEffectKind.Slow
                    ? TileEffectValue.Slow(effect.Scale, elapsed)
                    : effect;

                resolver.Apply(ctx.Cell, in value);
            }
        }

        // DoT 累加：accumulator 按效果分开持有，按 elapsed 攒够 interval 返回 true；累加量必须是“本次提交覆盖的时长”而非单帧 DeltaTime，按 TickInterval 提交只加一帧则攒够 1 秒要 60 次提交=60 秒才掉血
        private static bool ShouldFire(float interval, float elapsed, ref float accumulator)
        {
            if (elapsed <= 0f) return false;

            if (interval <= 0f) interval = elapsed;

            accumulator += elapsed;

            if (accumulator < interval) return false;

            accumulator = 0f;

            return true;
        }
    }
}
