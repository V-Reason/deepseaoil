using DeepseaOil.Data;
using UnityEngine;

namespace DeepseaOil.Logic
{
    public interface IEnemyBrain
    {
        EnemyIntent Decide(in EnemyBrainContext ctx);
    }

    /// <remarks>无目标走 WithoutTarget，别用 HasTarget=false</remarks>
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

    public static class EnemyBrainFactory
    {
        public static IEnemyBrain Create(in EnemySpec spec)
        {
            return new ChaseBrain(spec);
        }
    }
}
