using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Grid.States;
using DeepseaOil.Logic.Player;
using DeepseaOil.Presentation.Actor;
using DeepseaOil.Presentation.Adapters;
using DeepseaOil.Presentation.Ball;
using DeepseaOil.Presentation.Drop;
using DeepseaOil.Presentation.Grid;
using DeepseaOil.Presentation.Visual;
using DeepseaOil.Presentation.World;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Presentation
{
    /// <summary>战斗切片的组合根：装配一次，每帧驱动</summary>
    /// <remarks>无 Update/FixedUpdate，GameRoot 调 RenderTick 与 FixedTick：渲染帧=格子→球→喷泉，物理帧=冲量→敌人→玩家受击</remarks>
    public sealed class CombatRoot : MonoBehaviour, ISceneRoot, IRenderTicked, IPhysicsTicked, IThrowSink
    {
        /// <summary>世界侧排在玩家侧之后</summary>
        public int Order => SceneOrder.World;

        [Header("必需接线")]
        [Tooltip("玩家组合根（场景里的 PlayerController）。")]
        [SerializeField] private PlayerController player = default;

        [Tooltip("格子视图（提供格子几何与地板）。")]
        [SerializeField] private TilemapAdapter gridView = default;

        [Header("可选接线")]
        [Tooltip("球与瞄准件的父物体。留空则建在场景根下。")]
        [SerializeField] private Transform ballRoot = default;

        [Tooltip("敌人的父物体。留空则建在场景根下。")]
        [SerializeField] private Transform actorRoot = default;

        [Tooltip("场景里的喷泉。每帧由本类驱动（水球不是自驱的）。留空则资源系统不生效。")]
        [SerializeField] private Fountain[] fountains = new Fountain[0];

        [Tooltip("是否刷敌人。关掉可以只验投掷链路。")]
        [SerializeField] private bool enableWaves = true;

        private GameRoot _root;

        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private TileElementReactor _element;

        private BallDirector _balls;

        private ImpulseExecutor _impulses;

        private DropDirector _drops;

        private CombatDirector _combat;
        private TileHighlightView _highlight;

        private readonly List<Vector3Int> _contactCells = new List<Vector3Int>(9);

        private float _retryAt = float.PositiveInfinity;

        public bool IsReady { get; private set; }

        public GridLogic Grid => _grid;

        private void Start()
        {
            _root = GameRoot.Instance;
            _root.RegisterSceneRoot(this);
        }

        public void Attach()
        {
            if (IsReady) return;

            Assemble();
        }

        private void OnDestroy()
        {
            _highlight?.Detach();
            gridView?.Detach();

            // 销毁期再问 GameRoot.Instance 会当场造一个新的，用 Start 里抓的引用
            if (_root != null) _root.UnregisterSceneRoot(this);
        }

        private void OnEnable()
        {
            EventBus<DropCollected>.Subscribe(OnDropCollected);
            EventBus<RequestHudRefresh>.Subscribe(OnRequestHudRefresh);
        }

        private void OnDisable()
        {
            EventBus<DropCollected>.Unsubscribe(OnDropCollected);
            EventBus<RequestHudRefresh>.Unsubscribe(OnRequestHudRefresh);
        }

        public void RenderTick(float deltaTime)
        {
            if (!IsReady) return;

            // 顺序：格子先跑（泥浆可能本帧到期），再推球（落地改格加冲量），最后喷泉
            _grid.Tick(Time.time, deltaTime);

            _balls.Tick(deltaTime);

            _drops.Tick(deltaTime);

            for (int i = 0; i < fountains.Length; i++)
            {
                Fountain fountain = fountains[i];

                if (fountain != null) fountain.Tick(deltaTime);
            }
        }

        public void FixedTick(float deltaTime)
        {
            if (!IsReady) return;

            float now = Time.fixedTime;

            // ① 落地冲量：必须在物理帧施加，渲染帧施加会漂
            _impulses.FixedTick();

            // ② 敌人先按本帧位置追一步，再让格子按新位置结算（顺序固定=可复现）
            if (_combat != null) _combat.FixedTick(now, deltaTime);

            // ③ 玩家受击：读物理体位置，须在敌人移动之后
            UpdatePlayerContact(now);
        }

        /// <summary>世界侧两件玩家相关裁决：谁打到玩家、打空怎么重来</summary>
        /// <remarks>判定是纯函数（ContactProbe.TryFindAttacker），可在 EditMode 测。世界→玩家只走通知：组装 Damage 经 PlayerLogic.TakeDamage，扣血/无敌/推多远由玩家侧定</remarks>
        private void UpdatePlayerContact(float now)
        {
            PlayerLogic logic = player != null ? player.Logic : null;

            if (logic == null || _grid == null) return;

            if (!logic.IsAlive)
            {
                if (now < _retryAt) return;

                _retryAt = float.PositiveInfinity;

                player.RespawnToSpawn();
                ClearAll();

                return;
            }

            PlayerSpec spec = logic.Stats.Spec;

            Vector2 position = player.Position;
            Vector3Int cell = _grid.WorldToCell(position);

            if (!ContactProbe.TryFindAttacker(
                    cell,
                    position,
                    spec.ContactRadius,
                    _registry,
                    _contactCells,
                    out Vector2 attacker,
                    out _))
            {
                return;
            }

            // 方向由 Damage.At 算，与格子伤害共用同一份方向数学
            Damage damage = Damage.At(
                attacker,
                position,
                spec.ContactDamage,
                DamageSource.Contact,
                spec.KnockbackImpulse);

            if (!logic.TakeDamage(in damage, now)) return;

            if (!logic.IsAlive)
            {
                _retryAt = now + spec.RetryDelay;

                // 只报事实，怎么播由玩家侧翻译（表现细节不外泄到世界侧）
                player.OnDeathTriggered();
            }
        }

        public void ClearAll()
        {
            if (_combat != null) _combat.ClearAll();

            // 球也要清，否则玩家复活后会被上一局的球砸出一片泥
            _balls?.ClearAll();

            // 掉落物同理：上一局没捡完的水球不该留到下一局
            _drops?.ClearAll();
        }

        /// <summary>裁决投掷请求（IThrowSink）：落点合法性属世界信息</summary>
        /// <remarks>唯一否决判据：落点格没有地板（GridLogic.HasCell）。将来的阻挡/占位物加在这里，玩家侧不用改</remarks>
        public bool RequestThrow(in ThrowIntent intent)
        {
            if (!IsReady) return false;

            if (_grid == null || !_grid.HasCell(intent.Cell)) return false;

            return _balls != null && _balls.Throw(in intent);
        }

        /// <summary>组装战斗切片：依赖全来自参数与 Data 层（无 FindObjectOfType 与 Inspector 数值），射程取 PlayerSpec.MaxThrowDistance</summary>
        private void Assemble()
        {
            if (player == null || gridView == null)
            {
                Debug.LogError(
                    "CombatRoot 引用未接线（player / gridView 至少缺一个），战斗内容已停用。",
                    this);
                return;
            }

            if (!gridView.IsWired)
            {
                Debug.LogError("CombatRoot 的 TilemapAdapter 没有接 Tilemap，战斗内容已停用。", this);
                return;
            }

            if (player.Logic == null)
            {
                Debug.LogError(
                    "CombatRoot 拿不到玩家的逻辑层（PlayerController 的 motor / inputProvider 没接好，" +
                    "或它的装配失败），战斗内容已停用。",
                    this);
                return;
            }

            PlayerSpec playerSpec = player.Logic.Stats.Spec;

            IReadOnlyList<ProjectileSpec> balls = ConfigModule.GetAllBalls();

            // 元素层的表数据：反应规则（顺序即优先级）+ 地块效果（DoT 数值与节奏来源）
            IReadOnlyList<ElementRuleSpec> elementRules = ConfigModule.GetElementRules();
            IReadOnlyList<TileEffectSpec> tileEffects = ConfigModule.GetTileEffects();

            _registry = new EnemyCellRegistry();

            GridGeometry geometry = gridView.ReadGeometry();

            // 元素层由组合根装配：GridLogic 只收端口，不认识规则表与 ConfigModule
            _element = new TileElementReactor(elementRules);

            _grid = new GridLogic(
                geometry,
                ConfigModule.GetAllTileStates(),
                CreateTileState,
                _element,
                _registry);

            // 先订阅格子状态变化再灌初始状态，否则那批泥浆不会被画出来
            gridView.Attach();

            int cells = gridView.RegisterCells(_grid);

            // 关卡初始地块优先从场景里的 InitialSetup 笔刷层读（策划在编辑器里画）；没画才退回表驱动
            int initialStates = gridView.LoadInitialSetupTiles(_grid);

            if (initialStates == 0)
            {
                initialStates = _grid.LoadInitialStates(ConfigModule.GetTileInitials());
            }

            _highlight = CreateHighlightView(geometry);
            _highlight.Attach();

            _impulses = new ImpulseExecutor();

            _balls = new BallDirector();
            _balls.Attach(_grid, balls, _impulses, ballRoot);

            _drops = new DropDirector();
            _drops.Attach(actorRoot, player.transform);

            for (int i = 0; i < fountains.Length; i++)
            {
                if (fountains[i] != null) fountains[i].Attach(_drops);
            }

            player.Logic.ConfigureAim(in geometry, playerSpec.MaxThrowDistance, this);

            if (enableWaves)
            {
                _combat = CreateCombatDirector();
            }

            IsReady = true;

            Debug.Log(
                $"[Combat] 装配完成：格子 {cells} 个（初始状态 {initialStates} 个），" +
                $"球种 {balls.Count} 个，喷泉 {fountains.Length} 个，" +
                $"反应规则 {elementRules.Count} 条，地块效果 {tileEffects.Count} 个，" +
                (enableWaves
                    ? "敌人 启用"
                    : "敌人 关闭（CombatRoot 的「是否刷敌人」未勾选：想要刷怪请在 Inspector 上勾上它）"));
        }

        /// <summary>状态工厂：给 ID 造新实例，null=该 ID 没有实现</summary>
        /// <remarks>不共享原型：状态自己记持续时长，否则全场共用一个计时器。所有状态共用表驱动实现 TableTileState，只读 TileStateSpec，规则表命中的状态与有实现的状态同一集合；配置里没有那一行才返回 null</remarks>
        private static ITileState CreateTileState(TileStateType id)
        {
            TileStateSpec spec = ConfigModule.TryGetTileState(id);

            return spec != null ? new TableTileState(spec) : null;
        }

        private TileHighlightView CreateHighlightView(in GridGeometry geometry)
        {
            var go = new GameObject("AimHighlight");

            go.layer = RenderOrder.OverlayLayer;

            if (ballRoot != null) go.transform.SetParent(ballRoot, true);

            var view = go.AddComponent<TileHighlightView>();

            view.Initialize(in geometry, geometry.IsValid ? geometry.CellSize : 1f);

            return view;
        }

        private CombatDirector CreateCombatDirector()
        {
            var go = new GameObject("EnemyDirector");

            if (actorRoot != null) go.transform.SetParent(actorRoot, true);

            var director = go.AddComponent<CombatDirector>();

            director.Initialize(
                player,
                ConfigModule.GetWave(),
                ConfigModule.GetEnemy(),
                _grid,
                _registry,
                actorRoot);

            return director;
        }

        /// <summary>掉落物被领取：世界→玩家通知，按种类裁决给什么</summary>
        /// <remarks>裁决在这里而不在掉落物里（掉落物只发事实）；加一种掉落物在这里加 case，玩家侧不用改</remarks>
        private void OnDropCollected(DropCollected evt)
        {
            switch (evt.Type)
            {
                case DropType.Water:
                    player?.Logic?.Stats.AddWaterBall(evt.Amount);
                    return;

                default:
                    Debug.LogWarning(
                        $"[Combat] 领取了没有接线效果的掉落物 {evt.Type}（{evt.Amount} 个），本次不产生任何变化。");
                    return;
            }
        }

        /// <summary>HUD 加载完成时重播：三块读数各播一次当前值</summary>
        private void OnRequestHudRefresh(RequestHudRefresh evt)
        {
            player?.Logic?.Stats.Announce();
            _combat?.Announce();
        }
    }
}
