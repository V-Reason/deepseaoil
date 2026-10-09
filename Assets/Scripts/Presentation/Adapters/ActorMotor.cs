using DeepseaOil.Data;
using DeepseaOil.Foundation;
using DeepseaOil.Logic.Movement;
using UnityEngine;

namespace DeepseaOil.Presentation.Adapters
{
    /// <summary>移动执行器基类，把逻辑层速度写进物理体、朝向落成镜像</summary>
    /// <remarks>重力由逻辑层施加，本类永不设阻尼。EngineVelocity=引擎真值回读，IActorLedger.Velocity=本帧工作速度，不一致即引擎否决了提交。Initialize 幂等自愈（Awake 并非总会跑）。</remarks>
    [RequireComponent(typeof(Rigidbody2D))]
    public abstract class ActorMotor : MonoBehaviour, IActorMotor
    {
        [SerializeField] private Rigidbody2D body = default;

        private ActorLedger _ledger;
        private Vector2 _facing = Vector2.right;
        private bool _initialized;

        protected Rigidbody2D Body
        {
            get
            {
                Initialize();
                return body;
            }
        }

        public bool IsInitialized => _initialized;

        private ActorLedger Ledger => _ledger ??= new ActorLedger(
            readVelocity: () => Body.velocity,
            writeVelocity: v => Body.velocity = v);

        /// <summary>立即固化物理参数（幂等）；不调它则首个物理步会用默认重力跑</summary>
        public void EnsureInitialized()
        {
            Initialize();
        }

        /// <summary>引擎当前速度（单位/秒），是回读口而非本帧工作速度</summary>
        public Vector2 EngineVelocity => Body.velocity;

        /// <summary>物理体位置；边界钳位用 Rigidbody2D.position 而非 transform.position</summary>
        public Vector2 Position => Body.position;

        /// <summary>以给定速度驱动一次移动，不经账本；只有账本 Commit 与重生该调它</summary>
        public void Move(Vector2 velocity)
        {
            Body.velocity = velocity;
        }

        public void SetPosition(Vector2 position)
        {
            Body.position = position;
        }

        /// <summary>朝向：内部存完整世界方向，只把水平分量落成 localScale.x 符号</summary>
        /// <remarks>竖直朝向不参与镜像；零向量时朝向与镜像都不动。</remarks>
        public Vector2 Facing
        {
            get => _facing;
            set
            {
                if (value.sqrMagnitude <= 0f) return;

                _facing = value;

                if (value.x == 0f) return;

                Vector3 scale = transform.localScale;
                scale.x = Mathf.Abs(scale.x) * (value.x < 0f ? -1f : 1f);
                transform.localScale = scale;
            }
        }

        Vector2 IMovementMotor.Velocity => EngineVelocity;

        /// <summary>角色共用运动参数，只写不读；状态机读 Motion</summary>
        public CharacterConfig Config => Ledger.Config;

        public MotionParams Motion => Ledger.Motion;

        public Vector2 FrameStartVelocity => Ledger.FrameStartVelocity;

        public Vector2 SubmittedDelta => Ledger.SubmittedDelta;

        Vector2 IActorLedger.Velocity => Ledger.Velocity;

        public float SpeedScale
        {
            get => Ledger.SpeedScale;
            set => Ledger.SpeedScale = value;
        }

        public void Configure(CharacterConfig config)
        {
            Ledger.Configure(config);
        }

        public void BeginStep(float now, float deltaTime)
        {
            Ledger.BeginStep(now, deltaTime);
        }

        public void Commit()
        {
            Ledger.Commit();
        }

        public void AddImpulse(Vector2 deltaVelocity) => Ledger.AddImpulse(deltaVelocity);

        public void AddForce(Vector2 acceleration) => Ledger.AddForce(acceleration);

        public void SetVelocity(Vector2 velocity) => Ledger.SetVelocity(velocity);

        public void SnapVelocity(Vector2 velocity)
        {
            Ledger.SnapVelocity(velocity, v => FaceTowards(new Vector2(v.x, 0f)));
        }

        public void MoveTowards(Vector2 direction, float speed)
        {
            FaceTowards(direction);
            Ledger.MoveTowards(direction, speed);
        }

        public void BrakeTowards()
        {
            Ledger.BrakeTowards();
        }

        public void StopMove()
        {
            Ledger.StopMove();
        }

        public void MoveDirection(Vector2 direction, float speed)
        {
            FaceTowards(direction);

            Ledger.MoveDirection(direction, speed);
        }

        /// <summary>面向给定方向，零向量表示不改朝向</summary>
        public void FaceTowards(Vector2 direction)
        {
            if (direction.sqrMagnitude <= 0f) return;

            Facing = direction;
        }

        protected virtual void Awake()
        {
            Initialize();
        }

        /// <summary>补齐物理体引用并固化物理参数；可重复调用，只生效一次</summary>
        /// <remarks>子类扩展物理参数时覆写本方法，或在 ApplyPhysics 里加。</remarks>
        protected virtual void Initialize()
        {
            if (_initialized) return;

            if (body == null) body = GetComponent<Rigidbody2D>();

            if (body == null) return;

            ApplyPhysics(body);

            _initialized = true;
        }

        /// <summary>俯视角共同物理参数：关重力、永不随旋转</summary>
        /// <remarks>子类覆写后应调 base.ApplyPhysics，再补自己的。</remarks>
        protected virtual void ApplyPhysics(Rigidbody2D rigidbody2D)
        {
            rigidbody2D.gravityScale = 0f;
            rigidbody2D.freezeRotation = true;
        }
    }
}
