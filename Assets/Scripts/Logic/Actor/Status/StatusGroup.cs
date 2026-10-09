using DeepseaOil.Foundation;
using DeepseaOil.Logic.Movement;
using UnityEngine;

namespace DeepseaOil.Logic
{
    /// <summary>受击/硬直这类角色效果的状态层，玩家与敌人共用</summary>
    /// <remarks>门禁串行第一层：本层→战斗层→移动层，写速度的权限只有移动层有。帧外递交的击退只能挂起，本层下一帧开头消费成一次状态进入。</remarks>
    public sealed class StatusGroup : StateGroup<StatusStateTag, LogicContext>
    {
        private readonly HurtState _hurt;

        /// <summary>续命窗口的兜底拍数：窗口短于消费者一拍就会被扣穿（见 TickSlow）</summary>
        private const int SlowFloorTicks = 3;

        private float _slowScale = 1f;

        /// <summary>减速剩余时长（秒）；续命型修饰按拍兜底写入，见 TickSlow</summary>
        private float _slowRemaining;

        /// <summary>本次施加数据里给的时长；&lt;=0 = 表里没填，按"只要在格子上就持续生效"处理</summary>
        private float _slowGivenSeconds;

        /// <summary>自上一拍以来是否刚被续过一次减速：跨帧续命靠它活过当拍（见 TickSlow）</summary>
        private bool _slowRenewed;

        public StatusGroup(ActorLogic logic)
        {
            _hurt = new HurtState(logic);

            AddState(new NormalState(logic));
            AddState(_hurt);
        }

        protected override StatusStateTag EmptyTag => StatusStateTag.Empty;

        protected override StatusStateTag Fallback(in LogicContext ctx) => StatusStateTag.Normal;

        public HurtState Hurt => _hurt;

        public float SlowScale => _slowRemaining > 0f ? _slowScale : 1f;

        /// <remarks>续一次减速修饰（格子执行者按格施加）；速度乘数 1=不减速、非数按 1 处理。seconds≤0 = 表里没填时长，按续命处理，不再直接丢弃</remarks>
        public void ApplySlow(float speedScale, float seconds)
        {
            _slowScale = float.IsNaN(speedScale) ? 1f : Mathf.Clamp01(speedScale);
            _slowGivenSeconds = seconds;
            _slowRenewed = true;
        }

        /// <remarks>受击时产出 Forced 门禁，不带乘数：外力滑停不该被地面减速拖短</remarks>
        public MoveGates Gates
        {
            get
            {
                if (Current == StatusStateTag.Hurt) return MoveGates.Forced(_hurt.ForcedVelocity);

                float scale = SlowScale;

                return scale < 1f ? MoveGates.Scaled(scale) : MoveGates.None;
            }
        }

        public bool IsHurt => Current == StatusStateTag.Hurt;

        public void Tick(in LogicContext ctx, Vector2 pendingKnockback)
        {
            TickSlow(in ctx);

            EnterHurtIfPending(in ctx, pendingKnockback);

            TickStates(in ctx);
        }

        /// <summary>减速计时：被续过的那一拍只抬窗口不扣时，没续才按物理 Δt 扣</summary>
        /// <remarks>
        /// 🔴 两个坑都在这里：
        /// ① 扣时按物理 Δt，而生产者的续命窗口是**渲染帧**（泥浆每帧提交一次），0.0167s &lt; 0.02s 时
        ///    一个物理拍就能把它扣穿 ⇒ 低帧率下大部分物理拍读到的 SlowScale 是 1，表现成"贴着泥浆也不减速"。
        ///    故窗口兜底到 <see cref="SlowFloorTicks"/> 拍（按消费者自己的钟算，不是写死秒数）。
        /// ② 表里没填时长（seconds≤0）时按"只要在格子上就持续生效"处理：窗口取兜底拍数，靠每帧续命活着，
        ///    走开后最多多减速几拍。曾经这里直接 return ⇒ 泥浆一点都不减速。
        /// </remarks>
        private void TickSlow(in LogicContext ctx)
        {
            if (_slowRenewed)
            {
                _slowRenewed = false;

                float window = Mathf.Max(_slowGivenSeconds, SlowFloorTicks * ctx.deltaTime);

                if (window > _slowRemaining) _slowRemaining = window;

                return;
            }

            if (_slowRemaining > 0f) _slowRemaining -= ctx.deltaTime;
        }

        private void EnterHurtIfPending(in LogicContext ctx, Vector2 knockback)
        {
            // 逐分量精确比较：Vector2 的 != 带 1e-10 容差，小冲量会被吞掉
            if (knockback.x == 0f && knockback.y == 0f) return;

            _hurt.Configure(knockback);

            ChangeState(StatusStateTag.Hurt, in ctx);
        }
    }
}
