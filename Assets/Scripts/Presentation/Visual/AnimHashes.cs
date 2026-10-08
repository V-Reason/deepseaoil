using UnityEngine;

namespace DeepseaOil.Presentation.Visual
{
    /// <summary>角色通用动画参数哈希：连续运动学 + 受击/死亡触发器</summary>
    /// <remarks>参数名是与美术状态机的契约，改名即改状态机。全部 static readonly，热路径只读整数 id，不做字符串比较。</remarks>
    public static class AnimHashes
    {
        /// <summary>速度标量（单位/秒）</summary>
        public static readonly int Speed = Animator.StringToHash("Speed");

        /// <summary>归一化移动方向的水平分量</summary>
        public static readonly int MoveX = Animator.StringToHash("MoveX");

        /// <summary>归一化移动方向的垂直分量</summary>
        public static readonly int MoveY = Animator.StringToHash("MoveY");

        /// <summary>朝向水平分量：站住不动时也要保留</summary>
        public static readonly int FacingX = Animator.StringToHash("FacingX");

        /// <summary>受击触发器</summary>
        public static readonly int Hurt = Animator.StringToHash("Hurt");

        /// <summary>死亡触发器</summary>
        public static readonly int Die = Animator.StringToHash("Die");
    }

    /// <summary>玩家专有参数哈希</summary>
    public static class PlayerAnimHashes
    {
        /// <summary>冲刺状态（Bool：冲刺有持续时间）</summary>
        public static readonly int IsDashing = Animator.StringToHash("IsDashing");

        /// <summary>投掷出手瞬间</summary>
        public static readonly int TriggerThrow = Animator.StringToHash("TriggerThrow");

        /// <summary>放置/种植瞬间</summary>
        public static readonly int TriggerPlant = Animator.StringToHash("TriggerPlant");
    }

    /// <summary>敌人专有参数哈希</summary>
    public static class EnemyAnimHashes
    {
        /// <summary>近战出手</summary>
        public static readonly int Attack = Animator.StringToHash("Attack");

        /// <summary>受击状态（Bool：受击有僵直持续段）</summary>
        public static readonly int IsHurt = Animator.StringToHash("IsHurt");
    }

    /// <summary>掉落物专有参数哈希</summary>
    public static class ItemAnimHashes
    {
        /// <summary>落地/入水冲击</summary>
        public static readonly int Impact = Animator.StringToHash("Impact");

        /// <summary>被领取</summary>
        public static readonly int Collect = Animator.StringToHash("Collect");
    }
}
