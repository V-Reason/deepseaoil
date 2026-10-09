using UnityEngine;

namespace DeepseaOil.Data
{
    /// <summary>敌人运动学调参（程序管辖）：enemy 表里搬出来的物理手感数值</summary>
    /// <remarks>
    /// 表里只留战斗结算核心与策划要调的追击极速（id / name / max_speed / hp），
    /// 判定半径、加速度、击退衰减、停止距离、脱战距离都在这里 —— 改它不用导表，Play Mode 即时生效。
    /// 唯一读取口是 <c>ConfigModule.GetEnemy()</c>；缺失时 <c>BindAssets</c> 当场抛 <c>ConfigLoadException</c>，不静默退回字段默认值。
    /// 多怪种时本资产要升级成按 id 的条目表（见 Docs/待办.md）——现在只有一种怪，一份就够。
    /// </remarks>
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
