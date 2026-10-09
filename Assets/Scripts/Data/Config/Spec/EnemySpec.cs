using cfg.dso;
using UnityEngine;

namespace DeepseaOil.Data
{
    /// <summary>enemy 表列 id/name/max_speed/hp；radius/acceleration/knockback_decay/stop_distance/chase_range 退役归 EnemyTuning（世界单位、单位/秒、Hz）；FlashHz 取 VisualPalette；滑停同源 knockbackDecay</summary>
    public sealed class EnemySpec
    {
        private readonly Enemy _row;
        private readonly EnemyTuning _tuning;

        public EnemySpec(Enemy row, EnemyTuning tuning)
        {
            _row = row;
            _tuning = tuning;

            var config = ScriptableObject.CreateInstance<CharacterConfig>();

            config.name = $"EnemyConfig_{row.Id}";
            config.Name = row.Name;
            config.moveSpeed = row.MaxSpeed;
            config.snapToEightDirections = false;
            config.moveAcceleration = tuning.acceleration;
            config.turnDecayRate = tuning.knockbackDecay;
            config.hurtDecay = tuning.knockbackDecay;
            config.extraForceScale = 0f;
            config.dashSpeed = 0f;
            config.dashDuration = 0f;

            Config = config;
        }

        public int Id => _row.Id;

        public string Name => _row.Name;

        /// <remarks>预制体 CircleCollider2D 半径须与它一致</remarks>
        public float Radius => _tuning.radius;

        public float MaxSpeed => _row.MaxSpeed;

        public float StopDistance => _tuning.stopDistance;

        public float ChaseRange => _tuning.chaseRange;

        public int Hp => _row.Hp;

        public float FlashHz => ConfigModule.Visuals != null ? ConfigModule.Visuals.enemyFlashHz : 4f;

        /// <remarks>每种类一份、每只各持一份；只装配链消费</remarks>
        public CharacterConfig Config { get; }
    }
}
