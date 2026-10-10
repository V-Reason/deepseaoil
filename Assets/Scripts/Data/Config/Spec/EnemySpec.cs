using cfg.dso;
using UnityEngine;

namespace DeepseaOil.Data
{

    // 耐久与极速来自 enemy 表，半径/加速度/击退衰减/停距/追击范围退役归 EnemyTuning（世界单位、单位每秒）
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

        /// <summary>贴身一次对玩家造成的伤害；敌人伤害的唯一权威，玩家表的 contact_damage 不用在这条链上</summary>
        public int ContactDamage => _row.ContactDamage;

        public float FlashHz => ConfigModule.Visuals != null ? ConfigModule.Visuals.enemyFlashHz : 4f;

        /// <remarks>每种类一份、每只各持一份；只装配链消费</remarks>
        public CharacterConfig Config { get; }
    }
}
