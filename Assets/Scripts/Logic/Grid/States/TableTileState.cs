using DeepseaOil.Data;
using cfg.dso;

namespace DeepseaOil.Logic.Grid.States
{
    /// <summary>表驱动地块状态：行为全来自 TileStateSpec，效果清单在数据层已解析成定值，这里只按节拍提交并到点到期；所有状态共用这一个实现</summary>
    /// <remarks>减速/伤害是推而非被查询：OnEnter 与每次 OnTick 只提交清单里的效果，找人与施加由结算口完成；interval=0 的效果即每帧触发，泥浆靠每帧续命表达持续，不续就等于离开。清单在进入时提交一次、此后按 TickInterval 重复提交，表里一次性效果本轮无区分口。DoT 扣血节奏在本类累加（格子每帧 Tick），除 GridLogic 外零新增依赖。元素由元素层在状态切换时刷，本类只读表值。计时用一次性 Tick 队列而非协程：暂停时 DeltaTime 为 0 ⇒ 累加不动。</remarks>
    public sealed class TableTileState : ITileState
    {
        private readonly TileStateSpec _spec;

        /// <summary>本状态累计存在时长，判 Duration 到期</summary>
        private float _elapsed;

        /// <summary>效果清单的节拍累加，TickInterval>0 时才有意义</summary>
        private float _tickAccumulator;

        /// <summary>DoT 自身扣血累加，与节拍累加分开</summary>
        private float _dotAccumulator;

        public TableTileState(TileStateSpec spec)
        {
            _spec = spec;
        }

        public TileStateType Id => _spec.Id;

        /// <remarks>进入即开始计时；同状态不重入，已有状态时不会走到这里</remarks>
        public void OnEnter(in TileContext ctx)
        {
            _elapsed = 0f;
            _tickAccumulator = 0f;
            _dotAccumulator = 0f;

            // 进格这一拍按"一帧"算：切状态时的进格冲击（ApplyEnterImpact）已经打过一次一次性效果，
            // 这里只负责把周期效果（DoT）的累加器起个头，不给它当帧补一次伤害。
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

        /// <summary>按节拍提交效果清单；清单为空也每帧提交 Tick，状态还要判到期</summary>
        /// <remarks>提交窗口（elapsed）必须往下传：DoT 的累加器按它推进，续命型效果按它续命 —— 见 SubmitAll。</remarks>
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

        /// <summary>清单效果逐个交给结算口；DoT 攒够 Interval 才提交，一次性效果不在这里提交</summary>
        /// <param name="elapsed">本次提交覆盖的时长（秒）：每帧提交时是一帧，按节拍提交时是 TickInterval</param>
        /// <remarks>
        /// ctx.Resolver 为 null（逻辑层单跑测试）时静默跳过，不报错也不自己 new 执行者。
        /// 🔴 一次性效果（瞬时伤害 / 击退）在这里**刻意跳过**：它们由切状态时的进格冲击（ApplyEnterImpact）与
        /// 目标跨格时的 OnActorEnterCell 各补一次，逐帧提交会变成每秒 60 次掉血（实测冰沙 62 点/秒）。
        /// </remarks>
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

                // 续命型（表里没给时长）：把"本次提交的窗口"当它的续命时长交出去。
                // 生产者的窗口是渲染帧或提交节拍，消费者按物理帧扣时 —— 只续单帧会被扣穿（泥浆"贴着走也不减速"）。
                TileEffectValue value = effect.Seconds <= 0f && effect.Kind == TileEffectKind.Slow
                    ? TileEffectValue.Slow(effect.Scale, elapsed)
                    : effect;

                resolver.Apply(ctx.Cell, in value);
            }
        }

        /// <summary>DoT 累加：按 elapsed 攒够 interval 返回 true；accumulator 按效果分开持有，暂停时 elapsed 为 0 不推进</summary>
        /// <remarks>
        /// 🔴 累加量必须是"本次提交覆盖的时长"而不是单帧 DeltaTime：按 TickInterval 提交时（燃烧/蒸汽/导电，
        /// 节拍就是从 DoT 自己的 interval 来的）每次提交只加一帧的话，攒够 1 秒要 60 次提交 = 60 秒才掉一次血 ——
        /// 状态 3 秒就到期了，等于永远不掉血。实测燃烧 3 秒只掉 1 点（还是进格冲击那一次）。
        /// </remarks>
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
