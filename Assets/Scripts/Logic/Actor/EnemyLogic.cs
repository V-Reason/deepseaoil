using DeepseaOil.Data;
using DeepseaOil.Logic.Input;
using DeepseaOil.Logic.Movement;
using UnityEngine;

namespace DeepseaOil.Logic
{
    /// <summary>敌人逻辑层：账本＋状态层＋移动层＋大脑</summary>
    public sealed class EnemyLogic : ActorLogic
    {
        private readonly StatusGroup _status;
        private readonly EnemyMoveGroup _move;

        private Vector2? _target;

        /// <remarks>冲量必须挂起，不能当场写账本：账本在帧首清累积区，两次 Tick 之间写会被下一固定帧清掉</remarks>
        private Vector2 _pendingKnockback;

        public EnemyLogic(IActorMotor motor, EnemySpec spec)
            : this(motor, spec, EnemyBrainFactory.Create(in spec))
        {
        }

        /// <summary>注入大脑；brain=null 按 spec 造默认</summary>
        public EnemyLogic(IActorMotor motor, EnemySpec spec, IEnemyBrain brain)
            : base(motor, spec.Config)
        {
            Brain = brain ?? EnemyBrainFactory.Create(in spec);
            _status = new StatusGroup(this);
            _move = new EnemyMoveGroup(this, motor);
        }

        public IEnemyBrain Brain { get; }

        /// <summary>本帧意图，由 Brain.Decide 产出；状态层唯一读口</summary>
        public EnemyIntent Intent { get; private set; }

        public StatusGroup Status => _status;

        public EnemyMoveGroup MoveGroup => _move;

        public bool IsHurt => _status.IsHurt;

        public void SetTarget(Vector2? target)
        {
            _target = target;
        }

        public void ApplyKnockback(float impulse, Vector2 direction)
        {
            if (impulse <= 0f) return;

            _pendingKnockback += direction * impulse;
        }

        public void Tick(float now, float deltaTime)
        {
            // 意图必须先入上下文，故大脑决策放此处
            var brainContext = _target.HasValue
                ? new EnemyBrainContext(Motor.Position, _target.Value, true)
                : EnemyBrainContext.WithoutTarget(Motor.Position);

            Intent = Brain.Decide(in brainContext);

            var snapshot = new InputSnapshot(Intent.Direction, false, false);
            var context = new LogicContext(now, deltaTime, default, snapshot);

            FixedTick(context);
        }

        protected override void OnTick(in LogicContext ctx)
        {
            _status.Tick(in ctx, _pendingKnockback);
            _pendingKnockback = Vector2.zero;

            // 移动层唯一写速度处，门禁在此落地
            _move.Tick(in ctx, _status.Gates);
        }
    }
}
