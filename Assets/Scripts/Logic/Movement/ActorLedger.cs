using DeepseaOil.Data;
using UnityEngine;

namespace DeepseaOil.Logic.Movement
{
    /// <summary>速度账本 ＋ 角色控制律：一帧的速度累加区、帧末一次写出，以及逼近目标速度的全部数学</summary>
    public sealed class ActorLedger : IActorLedger
    {
        private readonly System.Func<Vector2> _readVelocity;
        private readonly System.Action<Vector2> _writeVelocity;

        private Vector2 _frameStart;
        private Vector2 _delta;
        private Vector2 _accel;
        private float _speedScale = 1f;
        private float _now;
        private float _dt = 0.02f;

        public ActorLedger(System.Func<Vector2> readVelocity, System.Action<Vector2> writeVelocity)
        {
            _readVelocity = readVelocity;
            _writeVelocity = writeVelocity;
        }

        public CharacterConfig Config { get; private set; }

        public Foundation.MotionParams Motion { get; private set; }

        public float Now => _now;

        public float DeltaTime => _dt;

        /// <remarks>config 折算成 Motion 快照，事后改 SO 不生效</remarks>
        public void Configure(CharacterConfig config)
        {
            Config = config;

            Motion = config == null
                ? Foundation.MotionParams.None
                : new Foundation.MotionParams(
                    config.moveSpeed,
                    config.moveAcceleration,
                    config.turnDecayRate,
                    config.dashSpeed,
                    config.dashDuration,
                    config.hurtDecay);
        }

        public Vector2 FrameStartVelocity => _frameStart;

        public Vector2 SubmittedDelta => _delta + _accel * _dt;

        public Vector2 Velocity => _frameStart + SubmittedDelta;

        /// <summary>速度乘数，1=不缩放；写口夹取 NaN→1、负数→0、&gt;1→1</summary>
        public float SpeedScale
        {
            get => _speedScale;
            set
            {
                if (float.IsNaN(value))
                {
                    _speedScale = 1f;
                    return;
                }

                _speedScale = value < 0f ? 0f : (value > 1f ? 1f : value);
            }
        }

        /// <remarks>时刻与 Δt 由驱动方给出</remarks>
        public void BeginStep(float now, float deltaTime)
        {
            _now = now;
            _dt = deltaTime;
            _frameStart = _readVelocity();
            _delta = Vector2.zero;
            _accel = Vector2.zero;

            // 乘数一次性：上一帧声明的到这一帧开头就失效，门禁必须每帧重新提交，否则"这一帧被推了一下"会变成"从此一直被限速"。
            _speedScale = 1f;
        }

        public void Commit()
        {
            // 零提交帧不写速度，没有变更就不覆盖引擎。
            if (!HasSubmission) return;

            _writeVelocity(_frameStart + SubmittedDelta);
        }

        public void AddImpulse(Vector2 deltaVelocity) => _delta += deltaVelocity;

        public void AddForce(Vector2 acceleration) => _accel += acceleration;

        public void SnapVelocity(Vector2 velocity, System.Action<Vector2> facing)
        {
            SetVelocity(velocity);

            if (velocity.x == 0f && velocity.y == 0f) return;

            facing?.Invoke(velocity);
        }

        public void SetVelocity(Vector2 velocity)
        {
            SetVelocityX(velocity.x);
            _accel.y = 0f;
            _delta.y = velocity.y - _frameStart.y;
        }

        public void MoveDirection(Vector2 direction, float speed)
        {
            SetVelocity(direction.normalized * speed);
        }

        public void StopMove()
        {
            SetVelocity(Vector2.zero);
        }

        /// <summary>移动层的"走"：有惯性按加速度逼近，零惯性当帧直达；方向可未归一化，零向量表示没有期望方向</summary>
        /// <remarks>判据是 MotionParams.MoveAcceleration（≤0 即零惯性）。速度乘数乘的是目标速度，零惯性当帧生效，有惯性稳态等于配置速度 × 乘数</remarks>
        public void MoveTowards(Vector2 direction, float speed)
        {
            float scaled = speed * _speedScale;

            if (Motion.MoveAcceleration <= 0f)
            {
                MoveDirection(direction, scaled);
                return;
            }

            SteerTowards(direction, scaled, Motion.MoveAcceleration, Motion.TurnDecayRate);
        }

        public void BrakeTowards()
        {
            if (Motion.MoveAcceleration <= 0f)
            {
                StopMove();
                return;
            }

            SteerTowards(Vector2.zero, 0f, Motion.MoveAcceleration, Motion.TurnDecayRate);
        }

        /// <summary>二维渐进逼近目标速度，控制律的唯一实现点；方向可未归一化，零向量表示没有期望方向</summary>
        /// <remarks>零方向走指数衰减（Δ -= 当前速度 × (1 − e^(−衰减率·Δt))），符号保持、模长单调收缩；方向变号时取两支中较快的一支。目标速度与当前速度都为零时不写速度</remarks>
        public void SteerTowards(Vector2 direction, float targetSpeed, float acceleration, float decayPerSecond)
        {
            Vector2 current = Velocity;
            Vector2 desired;

            if (direction.sqrMagnitude <= 0f || targetSpeed <= 0f)
            {
                desired = Vector2.zero;
            }
            else
            {
                desired = direction.normalized * targetSpeed;
            }

            Vector2 delta = desired - current;

            if (desired.sqrMagnitude > 0f && current.sqrMagnitude > 0f && Vector2.Dot(desired, current) < 0f)
            {
                Vector2 decay = -current * (1f - Mathf.Exp(-decayPerSecond * _dt));

                if (decay.sqrMagnitude > delta.sqrMagnitude) delta = decay;
            }
            else
            {
                float maxStep = acceleration * _dt;

                if (delta.sqrMagnitude > maxStep * maxStep) delta = delta.normalized * maxStep;
            }

            _delta += delta;
        }

        private void SetVelocityX(float vx)
        {
            _accel.x = 0f;
            _delta.x = vx - _frameStart.x;
        }

        private bool HasSubmission => _delta.x != 0f || _delta.y != 0f || _accel.x != 0f || _accel.y != 0f;
    }
}
