using UnityEngine;

namespace DeepseaOil.Presentation.Visual
{
    /// <summary>把运动学与事件翻译成 Animator 参数</summary>
    /// <remarks>无 Animator 或未挂 controller 时全部调用是空操作；localPosition 归 EffectModule，本类不写。</remarks>
    [DisallowMultipleComponent]
    public sealed class ActorAnimationView : MonoBehaviour
    {
        [Tooltip("动画状态机。留空则 Awake 时从本节点自愈抓取")]
        [SerializeField] private Animator animator = default;

        [Tooltip("精灵渲染器。留空则 Awake 时从本节点自愈抓取")]
        [SerializeField] private SpriteRenderer spriteRenderer = default;

        [Tooltip("是否用 flipX 表达左右朝向。默认关：ActorMotor 已把朝向落成 Root 的 localScale.x 符号，两条镜像叠在同一根轴上会抵消（朝左显示成朝右）。改用状态机镜像时才开")]
        [SerializeField] private bool useFlipX = false;

        /// <summary>非空且挂了 controller 才有效，否则 SetFloat 刷警告</summary>
        public bool HasValidAnimator => animator != null && animator.runtimeAnimatorController != null;

        /// <summary>可能为 null</summary>
        public SpriteRenderer Renderer => spriteRenderer;

        /// <summary>可能为 null</summary>
        public Animator Animator => animator;

        public bool UseFlipX => useFlipX;

        private void Awake()
        {
            // 自愈抓取，漏接线不报错
            if (animator == null) animator = GetComponent<Animator>();

            if (spriteRenderer == null) spriteRenderer = GetComponent<SpriteRenderer>();
        }

        /// <summary>moveInput/facing 零向量=站住/不改镜像，speed 单位/秒</summary>
        public void SetMotion(Vector2 moveInput, Vector2 facing, float speed)
        {
            UpdateFlip(facing.x);

            if (!HasValidAnimator) return;

            animator.SetFloat(AnimHashes.Speed, speed);
            animator.SetFloat(AnimHashes.MoveX, moveInput.x);
            animator.SetFloat(AnimHashes.MoveY, moveInput.y);
            animator.SetFloat(AnimHashes.FacingX, facing.x);
        }

        public void SetSpeed(float speed)
        {
            if (!HasValidAnimator) return;

            animator.SetFloat(AnimHashes.Speed, speed);
        }

        /// <summary>facingX=0 不动；镜像与 ActorMotor 互斥</summary>
        public void UpdateFlip(float facingX)
        {
            if (!useFlipX || spriteRenderer == null || facingX == 0f) return;

            spriteRenderer.flipX = facingX < 0f;
        }

        public void SetBool(int paramHash, bool value)
        {
            if (!HasValidAnimator) return;

            animator.SetBool(paramHash, value);
        }

        public void SetFloat(int paramHash, float value)
        {
            if (!HasValidAnimator) return;

            animator.SetFloat(paramHash, value);
        }

        public void Trigger(int triggerHash)
        {
            if (!HasValidAnimator) return;

            animator.SetTrigger(triggerHash);
        }

        /// <summary>撤销未被消费的触发器</summary>
        public void ResetTrigger(int triggerHash)
        {
            if (!HasValidAnimator) return;

            animator.ResetTrigger(triggerHash);
        }

        /// <summary>重生/复用对象时清掉上条命的状态与参数</summary>
        public void ResetToDefault()
        {
            if (!HasValidAnimator) return;

            animator.Rebind();
        }

        public void TriggerHurt()
        {
            Trigger(AnimHashes.Hurt);
        }

        public void TriggerDie()
        {
            Trigger(AnimHashes.Die);
        }

        public void ResetHurt()
        {
            ResetTrigger(AnimHashes.Hurt);
        }

        public void ResetDie()
        {
            ResetTrigger(AnimHashes.Die);
        }
    }
}
