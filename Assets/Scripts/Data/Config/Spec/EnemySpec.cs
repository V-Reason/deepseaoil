using cfg.dso;
using UnityEngine;

namespace DeepseaOil.Data
{
    /// <summary>敌人种类取值边界，合并 enemy 表行与 EnemyTuning；行不出 Data 层</summary>
    /// <remarks>
    /// 表行管耐久与追击极速（策划要调的两项）；判定半径 / 加速度 / 击退衰减 / 停止距离 / 脱战距离归 EnemyTuning（调参 SO，改它不用导表）。
    /// 半径=世界单位，速度=单位/秒，FlashHz=Hz；StopDistance=进入即不再压上，ChaseRange=超出即放弃追击。
    /// 冲刺/8向吸附/外力显式清零，否则继承玩家默认值；受击滑停取 EnemyTuning.knockbackDecay，喂 turnDecayRate/hurtDecay。
    /// </remarks>
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

        /// <remarks>来自调参资产：预制体上的 CircleCollider2D 半径必须与它一致，否则 Gizmo 圈与实际碰撞体是两个答案</remarks>
        public float Radius => _tuning.radius;

        /// <remarks>表里的"追击极速"是策划要调的压迫感，不搬 SO</remarks>
        public float MaxSpeed => _row.MaxSpeed;

        public float StopDistance => _tuning.stopDistance;

        public float ChaseRange => _tuning.chaseRange;

        public int Hp => _row.Hp;

        /// <summary>受击闪烁频率（Hz）</summary>
        /// <remarks>表已不再配这一列：频率是观感参数，取 VisualPalette.enemyFlashHz，改它不用导表</remarks>
        public float FlashHz => ConfigModule.Visuals != null ? ConfigModule.Visuals.enemyFlashHz : 4f;

        /// <remarks>运行期按种类造一份、每只各持一份；只有装配链消费</remarks>
        public CharacterConfig Config { get; }
    }
}
