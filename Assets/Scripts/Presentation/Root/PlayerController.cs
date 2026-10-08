using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Input;
using DeepseaOil.Logic.Movement;
using DeepseaOil.Logic.Player;
using DeepseaOil.Presentation.Adapters;
using DeepseaOil.Presentation.Input;
using DeepseaOil.Presentation.Visual;
using cfg.dso;
using UnityEngine;

namespace DeepseaOil.Presentation
{
    /// <summary>玩家组合根：组装执行器/配置/活动区域/输入缓冲</summary>
    /// <remarks>Push 先于 Tick，否则按下沿滞后一帧。方向只归一化一次，WorldInfo 与缓冲快照必须是同一份，各留一份会导致斜向快 √2 倍。</remarks>
    public sealed class PlayerController : MonoBehaviour, ISceneRoot, IPhysicsTicked, IRenderTicked, IManagedActor
    {
        /// <summary>玩家侧早于世界侧</summary>
        public int Order => SceneOrder.Player;

        [SerializeField] private PlayerMotor motor = default;
        [SerializeField] private InputProvider inputProvider = default;

        [Tooltip("瞄准用的相机。留空取 Camera.main（战斗场景里就是主相机，所以通常不用拖）")]
        [SerializeField] private Camera aimCamera = default;

        [Tooltip("地图活动区域：拖入覆盖可行走区域的 BoxCollider2D。不接则不钳位（不报错，调试面板会显示未接线）")]
        [SerializeField] private BoxCollider2D boundsArea = default;

        [Tooltip("视觉适配器，必须挂在子节点 View 上（Root 只放物理与逻辑）。留空则 Awake 自愈：先找子节点，找不到就地造一个 View")]
        [SerializeField] private ActorAnimationView animView = default;

        /// <summary>方向判零容差，吸收摇杆漂移与浮点残渣</summary>
        private const float DirectionEpsilon = 1e-6f;

        /// <summary>8 向吸附一档，45°</summary>
        private const float OctantRadians = 2f * Mathf.PI / 8f;

        /// <summary>键为档位序号</summary>
        private static readonly Dictionary<int, Vector2> Snapped = BuildSnappedTable();

        private InputBuffer _buffer;

        private WorldInfo _world;

        private BoundsArea _bounds;

        /// <summary>配置只在 Attach 取一次，不驻留表现层</summary>
        private PlayerSpec _spec;

        /// <summary>瞄准平面深度，世界单位，Attach 取一次</summary>
        private float _cameraPlaneDepth = 100f;

        /// <summary>视觉适配器，挂在子节点 View 上；Root 只承担物理真值与组合根</summary>
        private ActorAnimationView _animView;

        private Vector2 _spawnPoint;

        private GameRoot _root;

        public PlayerLogic Logic { get; private set; }

        /// <summary>碰撞用物理体位置，不是 transform</summary>
        public Vector2 Position => motor == null ? Vector2.zero : motor.Position;

        /// <summary>视觉适配器（挂在子节点 View 上）；白模期可能整类空转，外部调用一律判空</summary>
        public ActorAnimationView Visual => _animView;

        public WorldInfo World => _world;

        /// <summary>引擎回读速度，滞后一个物理步，用于与逻辑层对照</summary>
        public Vector2 EngineVelocity => motor == null ? Vector2.zero : motor.EngineVelocity;

        /// <summary>原始输入吸附到 8 向并归一化</summary>
        /// <remarks>零输入返回 Vector2.zero，否则模长恒为 1（WorldInfo 与逻辑层的契约）；吸附同时抹掉摇杆模拟幅度，轻推与推满同速。静态纯函数。</remarks>
        public static Vector2 SnapMoveToEightDirections(Vector2 move, bool snapToEightDirections)
        {
            if (move.sqrMagnitude <= DirectionEpsilon) return Vector2.zero;

            // 关掉吸附也不能原样放行：(1,1) 模长 √2，会写出快 41% 的速度。
            if (!snapToEightDirections) return move.normalized;

            // Round 把 ±22.5° 内输入归到最近一档，无需死区；查表而非 Cos/Sin 算值，让同一方向逐位相同。
            int octant = Mathf.RoundToInt(Mathf.Atan2(move.y, move.x) / OctantRadians);
            octant %= 8;
            if (octant < 0) octant += 8;

            return Snapped[octant];
        }

        private static Dictionary<int, Vector2> BuildSnappedTable()
        {
            var table = new Dictionary<int, Vector2>(8);

            for (int octant = 0; octant < 8; octant++)
            {
                float radians = octant * OctantRadians;
                table[octant] = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
            }

            return table;
        }

        /// <summary>回出生点并满血</summary>
        /// <remarks>时机由世界侧决定，本类只执行回哪、满血、停住；出生点越界时钳回来，否则会回到地图外。</remarks>
        public void RespawnToSpawn()
        {
            if (Logic == null) return;

            Vector2 position = _spawnPoint;

            if (_bounds.TryClamp(position, out Vector2 clamped)) position = clamped;

            Logic.RespawnTo(position);

            // 死亡触发器未必已被消费，不清会在复活瞬间补播一次倒地
            _animView?.ResetToDefault();
        }

        /// <summary>已死、进入重生等待：由世界侧裁决出死亡时调</summary>
        public void OnDeathTriggered()
        {
            _animView?.TriggerDie();
        }

        /// <summary>自愈装配视觉子节点：先找子节点 View，找不到就地造一个</summary>
        /// <remarks>兜底造出来的 View 照抄 Root 上渲染器的 sprite/颜色/排序层，老预制体自愈后视觉不跳变。Sprite 与颜色不在这里配：那属各场景观感。</remarks>
        private void EnsureAnimationView()
        {
            if (animView == null) animView = GetComponentInChildren<ActorAnimationView>(true);

            if (animView == null)
            {
                var view = new GameObject("View");

                view.transform.SetParent(transform, false);

                var sprite = view.AddComponent<SpriteRenderer>();

                // 老拓扑的渲染器在 Root 上：参数照搬，免得自愈之后玩家变白块
                var legacy = GetComponent<SpriteRenderer>();

                if (legacy != null)
                {
                    sprite.sprite = legacy.sprite;
                    sprite.color = legacy.color;
                    sprite.sortingLayerID = legacy.sortingLayerID;
                    sprite.sortingOrder = legacy.sortingOrder;
                }

                animView = view.AddComponent<ActorAnimationView>();
            }

            // 两个字段各司其职：animView 是序列化接线（Inspector 可见），_animView 是运行期缓存（热路径零判断）
            _animView = animView;
        }

        private void Awake()
        {
            if (motor == null
                || inputProvider == null)
            {
                Debug.LogError("PlayerController 引用未接线（motor / inputProvider），已停用。", this);
                enabled = false;
                return;
            }

            EnsureAnimationView();

            _bounds = ReadBounds();
            _world = new WorldInfo(Vector2.zero, in _bounds); // 首帧前也不留 default
            _spawnPoint = motor.Position;

            if (!_bounds.IsValid)
            {
                Debug.LogWarning(
                    "PlayerController.boundsArea 未接线、被停用或尺寸为 0：玩家不会被限制在地图边界内。",
                    this
                    );
            }
        }

        private void Start()
        {
            _root = GameRoot.Instance;
            _root.RegisterSceneRoot(this);
        }

        private void OnDestroy()
        {
            // 用 Start 里抓住的引用：销毁期再问 GameRoot.Instance 可能当场造一个新的出来
            if (_root != null) _root.UnregisterSceneRoot(this);
        }

        /// <summary>装配玩家逻辑，由 GameRoot 在第一个被驱动的帧调</summary>
        /// <remarks>读表必须放这里：ConfigModule 由 GameRoot.Awake 装配，组件 Awake 顺序不保证，写在 Awake 里 ConfigModule 未就绪会抛。缓冲先取 PlayerSpec 再建。</remarks>
        public void Attach()
        {
            if (Logic != null || motor == null) return;

            _spec = ConfigModule.GetPlayer();

            // 缓冲容量取容量参数与各输入窗口的较大者：小于窗口时窗口内按下会被挤出历史。
            _buffer = new InputBuffer(
                Mathf.Max(_spec.InputBufferSeconds, _spec.DashBufferSeconds),
                Mathf.RoundToInt(1f / Time.fixedDeltaTime)
                );

            _cameraPlaneDepth = _spec.CameraPlaneDepth;

            Logic = new PlayerLogic(motor, _spec, _buffer);
        }

        /// <summary>物理帧，由 GameRoot.FixedUpdate 按 Order 驱动</summary>
        public void FixedTick(float deltaTime)
        {
            if (Logic == null) return;

            InputSnapshot raw = inputProvider.ConsumeSnapshot();

            Vector2 move = SnapMoveToEightDirections(raw.Move, _spec.SnapToEightDirections);

            // 归一化后的方向要写回快照：WorldInfo 与 InputBuffer 必须是同一份，否则 MoveGroup 与 PlayerLogic 的最近朝向会看到未处理值。
            var snapshot = new InputSnapshot(move, raw.DashPressed, raw.GrabHeld);

            _buffer.Push(in snapshot, Time.fixedTime);

            _world = new WorldInfo(move, in _bounds);

            Logic.FixedTick(
                new LogicContext(
                    Time.fixedTime,
                    Time.fixedDeltaTime,
                    in _world,
                    in snapshot
                )
            );

            // 保险丝：只在真越界时写位置，避免每帧打断刚体位置积分。
            if (_world.Bounds.TryClamp(motor.Position, out Vector2 clamped))
            {
                motor.SetPosition(clamped);
            }
        }

        /// <summary>渲染帧：瞄准与开火意图，由 GameRoot.Update 按 Order 驱动</summary>
        /// <remarks>瞄准不吃也不产物理量，故放渲染帧；屏幕→世界只有本类能做，算完把世界点交逻辑层算吸附格。暂停只认 InputProvider.IsInputEnabled 一个真值。</remarks>
        public void RenderTick(float deltaTime)
        {
            if (Logic == null) return;

            // 遮挡属观感与物理步无关；放暂停判断之前，暂停时也要保持档位正确。
            UpdateSortingOrder();

            // 运动学快照每渲染帧推一次（含暂停帧）：插值出来的位置比物理帧平滑，动画不该跟着 50Hz 跳。
            _animView?.SetMotion(_world.MoveDirection, Logic.Motor.Facing, EngineVelocity.magnitude);

            if (inputProvider == null || !inputProvider.IsInputEnabled)
            {
                Logic.ClearAim();
                return;
            }

            Camera camera = aimCamera != null ? aimCamera : Camera.main;

            if (camera == null || inputProvider == null) return;

            float now = Time.time;

            Logic.UpdateAim(AimWorldPoint(camera), now);

            // 动作表无攻击动作：由 InputProvider 直读指针产出。主攻击=水球（耗弹药），副攻击=土球（不耗）。
            // 只有世界侧采纳了才播出手动作：否则空放/射程外也会抬手。
            if (inputProvider.AttackPressedThisFrame && Logic.RequestThrow(BallType.Water, now))
            {
                _animView?.Trigger(PlayerAnimHashes.TriggerThrow);
            }

            if (inputProvider.AltAttackPressedThisFrame && Logic.RequestThrow(BallType.Earth, now))
            {
                _animView?.Trigger(PlayerAnimHashes.TriggerThrow);
            }
        }

        /// <summary>按 y 刷新本体渲染档位，场景里填的 sortingOrder 只是初始值</summary>
        /// <remarks>基准取 Root 的物理体 y 而非 View 的 transform y：View 的 localPosition 会被受击抖动/伪高度改写，拿它排序会让角色随特效上下乱插队。</remarks>
        private void UpdateSortingOrder()
        {
            SpriteRenderer body = _animView != null ? _animView.Renderer : null;

            if (body == null) return;

            body.sortingOrder = RenderOrder.ActorOrder(Position.y);
        }

        /// <summary>屏幕点→世界点；不读相机 z，它被 Cinemachine 每帧驱动，正交相机下用足够大的常量深度更稳，见 ThrowTuning.cameraPlaneDepth</summary>
        private Vector2 AimWorldPoint(Camera camera)
        {
            Vector2 screen = inputProvider.AimScreen;

            Vector3 world = camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, _cameraPlaneDepth));

            return new Vector2(world.x, world.y);
        }

        /// <summary>活动区域碰撞体折算成纯数据矩形，Awake 读一次即可（地图尺寸运行期不变）</summary>
        private BoundsArea ReadBounds()
        {
            if (boundsArea == null || !boundsArea.enabled) return default;

            Bounds b = boundsArea.bounds;

            return new BoundsArea(b.min, b.max);
        }
    }
}
