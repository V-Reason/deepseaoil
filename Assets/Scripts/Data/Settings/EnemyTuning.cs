using UnityEngine;

namespace DeepseaOil.Data
{
    /// <summary>敌人运动学调参：enemy 表退役物理列落点</summary>
    // 唯一读取口 ConfigModule.GetEnemy()；缺失时抛 ConfigLoadException，不静默退回默认值
    [CreateAssetMenu(fileName = "EnemyTuning", menuName = "DeepseaOil/Tuning/Enemy")]
    public sealed class EnemyTuning : ScriptableObject
    {
        public const string ResourceKey = "tuning/EnemyTuning";

        [Header("体型")]
        [Tooltip("判定半径（世界单位，原 enemy 表的 radius 列）：Gizmo 圈读它；预制体上的 CircleCollider2D 半径必须与它一致")]
        public float radius = 0.45f;

        [Header("运动")]
        [Tooltip("加速度（单位/秒²，原 enemy 表的 acceleration 列）：起步与击退滑停的斜率")]
        public float acceleration = 14f;

        [Tooltip("击退衰减率（1/秒，原 enemy 表的 knockback_decay 列）：同时喂转向衰减与受击滑停")]
        public float knockbackDecay = 10f;

        [Tooltip("贴到这个距离就停住，不再压上（世界单位，原 enemy 表的 stop_distance 列）")]
        public float stopDistance = 0.6f;

        [Tooltip("超出这个距离就放弃追击（世界单位，原 enemy 表的 chase_range 列）")]
        public float chaseRange = 60f;
    }
}
