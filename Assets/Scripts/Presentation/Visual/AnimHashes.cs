using UnityEngine;

namespace DeepseaOil.Presentation.Visual
{
    /// <summary>动画参数哈希：参数名即与美术状态机的契约，改名即改状态机</summary>
    /// <remarks>热路径只用整数 id；Speed 单位/秒，MoveX/MoveY 归一化，FacingX 站住不动也推；Bool 表持续段，Trigger 表瞬时</remarks>
    public static class AnimHashes
    {
        public static readonly int Speed = Animator.StringToHash("Speed");

        public static readonly int MoveX = Animator.StringToHash("MoveX");

        public static readonly int MoveY = Animator.StringToHash("MoveY");

        public static readonly int FacingX = Animator.StringToHash("FacingX");

        public static readonly int Hurt = Animator.StringToHash("Hurt");

        public static readonly int Die = Animator.StringToHash("Die");
    }

    public static class PlayerAnimHashes
    {
        public static readonly int IsDashing = Animator.StringToHash("IsDashing");

        public static readonly int TriggerThrow = Animator.StringToHash("TriggerThrow");

        public static readonly int TriggerPlant = Animator.StringToHash("TriggerPlant");
    }

    public static class EnemyAnimHashes
    {
        public static readonly int Attack = Animator.StringToHash("Attack");

        public static readonly int IsHurt = Animator.StringToHash("IsHurt");
    }

    public static class ItemAnimHashes
    {
        public static readonly int Impact = Animator.StringToHash("Impact");

        public static readonly int Collect = Animator.StringToHash("Collect");
    }
}
