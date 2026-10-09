using DeepseaOil.Data;
using UnityEngine;

namespace DeepseaOil.Logic
{
    /// <summary>敌人大脑接口：吃一帧世界信息吐一条意图；实现不查世界，位置与系数由 EnemyLogic 组装成 Context 喂入，可喂假数据复现</summary>
    public interface IEnemyBrain
    {
        EnemyIntent Decide(in EnemyBrainContext ctx);
    }

    /// <summary>大脑的输入快照：自己位置、目标位置、有没有目标</summary>
    /// <remarks>无目标走 WithoutTarget，别用 HasTarget=false 的重载手拼，两者语义相同但后者容易漏。</remarks>
    public readonly struct EnemyBrainContext
    {
        public readonly Vector2 Self;

        public readonly Vector2 Target;

        public readonly bool HasTarget;

        public EnemyBrainContext(Vector2 self, Vector2 target, bool hasTarget)
        {
            Self = self;
            Target = target;
            HasTarget = hasTarget;
        }

        public static EnemyBrainContext WithoutTarget(Vector2 self)
        {
            return new EnemyBrainContext(self, default, false);
        }
    }

    /// <summary>连续追逐：进 ChaseRange 就压上，到 StopDistance 就停</summary>
    public sealed class ChaseBrain : IEnemyBrain
    {
        private readonly EnemySpec _enemy;

        public ChaseBrain(EnemySpec spec)
        {
            _enemy = spec;
        }

        public EnemyIntent Decide(in EnemyBrainContext ctx)
        {
            if (!ctx.HasTarget) return EnemyIntent.Idle;

            Steering steering = Steering.Resolve(
                ctx.Self,
                ctx.Target,
                _enemy.StopDistance,
                _enemy.ChaseRange,
                _enemy.MaxSpeed);

            return new EnemyIntent(steering.Direction, steering.Speed);
        }
    }

    /// <summary>按 EnemySpec 造大脑：调用方只认 IEnemyBrain，换策略不动 EnemyLogic</summary>
    /// <remarks>加怪种在这里分流（按 spec.Id 或 Spec 上新增的字段）；不要在生产代码里 new ChaseBrain。</remarks>
    public static class EnemyBrainFactory
    {
        public static IEnemyBrain Create(in EnemySpec spec)
        {
            return new ChaseBrain(spec);
        }
    }
}
