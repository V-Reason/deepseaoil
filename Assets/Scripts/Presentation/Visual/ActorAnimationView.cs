using UnityEngine;

namespace DeepseaOil.Presentation.Visual
{
    /// <summary>挂在角色的 View 子节点上：把运动学与事件翻译成 Animator 参数</summary>
    /// <remarks>没有 Animator、或没挂 controller 时全部调用是空操作（零报错），白模期照常能跑。参数一律走 AnimHashes 的整数 id。本类只读不写自己的 localPosition：抖动/后坐力由 EffectModule 写，两边各写各的会互相覆盖。</remarks>
    [DisallowMultipleComponent]
    public sealed class ActorAnimationView : MonoBehaviour
    {
        [Tooltip("动画状态机。留空则 Awake 时从本节点自愈抓取")]
        [SerializeField] private Animator animator = default;

        [Tooltip("精灵渲染器。留空则 Awake 时从本节点自愈抓取")]
        [SerializeField] private SpriteRenderer spriteRenderer = default;

        [Tooltip("是否用 flipX 表达左右朝向。默认关：ActorMotor 已把朝向落成 Root 的 localScale.x 符号，两条镜像叠在同一根轴上会抵消（朝左显示成朝右）。改用状态机镜像时才开")]
        [SerializeField] private bool useFlipX = false;

        /// <summary>Animator 存在且挂了 controller：只判非空不够，空 Animator 上 SetFloat 会刷警告</summary>
        public bool HasValidAnimator => animator != null && animator.runtimeAnimatorController != null;

        /// <summary>精灵渲染器；可能为 null</summary>
        public SpriteRenderer Renderer => spriteRenderer;

        /// <summary>动画状态机；可能为 null</summary>
        public Animator Animator => animator;

        /// <summary>左右镜像是否交给本类</summary>
        public bool UseFlipX => useFlipX;

        private void Awake()
        {
            // 自愈抓取：预制体漏接线时不报错、不留空引用
            if (animator == null) animator = GetComponent<Animator>();

            if (spriteRenderer == null) spriteRenderer = GetComponent<SpriteRenderer>();
        }

        /// <summary>一帧运动学快照：镜像 + Speed/MoveX/MoveY/FacingX 四个 float 一起推</summary>
        /// <param name="moveInput">归一化移动方向，零向量=站住</param>
        /// <param name="facing">朝向；零向量=不改镜像</param>
        /// <param name="speed">速度标量，单位/秒</param>
        public void SetMotion(Vector2 moveInput, Vector2 facing, float speed)
        {
            UpdateFlip(facing.x);

            if (!HasValidAnimator) return;

            animator.SetFloat(AnimHashes.Speed, speed);
            animator.SetFloat(AnimHashes.MoveX, moveInput.x);
            animator.SetFloat(AnimHashes.MoveY, moveInput.y);
            animator.SetFloat(AnimHashes.FacingX, facing.x);
        }

        /// <summary>只推速度</summary>
        public void SetSpeed(float speed)
        {
            if (!HasValidAnimator) return;

            animator.SetFloat(AnimHashes.Speed, speed);
        }

        /// <summary>更新镜像；facingX=0 不动（竖直朝向不该把角色翻过来）</summary>
        /// <remarks>与 ActorMotor.Facing 的 localScale.x 镜像互斥：同一根轴翻两次等于没翻，故 useFlipX 默认关。</remarks>
        public void UpdateFlip(float facingX)
        {
            if (!useFlipX || spriteRenderer == null || facingX == 0f) return;

            spriteRenderer.flipX = facingX < 0f;
        }

        /// <summary>推布尔参数（id 取 AnimHashes / PlayerAnimHashes / EnemyAnimHashes）</summary>
        public void SetBool(int paramHash, bool value)
        {
            if (!HasValidAnimator) return;

            animator.SetBool(paramHash, value);
        }

        /// <summary>推浮点参数</summary>
        public void SetFloat(int paramHash, float value)
        {
            if (!HasValidAnimator) return;

            animator.SetFloat(paramHash, value);
        }

        /// <summary>触发一次性触发器</summary>
        public void Trigger(int triggerHash)
        {
            if (!HasValidAnimator) return;

            animator.SetTrigger(triggerHash);
        }

        /// <summary>撤销尚未被消费的触发器</summary>
        public void ResetTrigger(int triggerHash)
        {
            if (!HasValidAnimator) return;

            animator.ResetTrigger(triggerHash);
        }

        /// <summary>回到状态机默认状态：重生/复用对象时清掉上一条命留下的 Trigger、Bool 与状态位置</summary>
        public void ResetToDefault()
        {
            if (!HasValidAnimator) return;

            animator.Rebind();
        }

        /// <summary>受击</summary>
        public void TriggerHurt()
        {
            Trigger(AnimHashes.Hurt);
        }

        /// <summary>死亡</summary>
        public void TriggerDie()
        {
            Trigger(AnimHashes.Die);
        }

        /// <summary>撤销受击触发器</summary>
        public void ResetHurt()
        {
            ResetTrigger(AnimHashes.Hurt);
        }

        /// <summary>撤销死亡触发器</summary>
        public void ResetDie()
        {
            ResetTrigger(AnimHashes.Die);
        }
    }
}
