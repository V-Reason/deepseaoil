using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Drop;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Grid.States;
using DeepseaOil.Logic.Player;
using DeepseaOil.Logic.World;
using DeepseaOil.Presentation.Actor;
using DeepseaOil.Presentation.Adapters;
using DeepseaOil.Presentation.Ball;
using DeepseaOil.Presentation.Drop;
using DeepseaOil.Presentation.Effects;
using DeepseaOil.Presentation.Grid;
using DeepseaOil.Presentation.Input;
using DeepseaOil.Presentation.Visual;
using DeepseaOil.Presentation.World;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Presentation
{
    /// 战斗切片的组合根：装配一次，每帧驱动
    // 无 Update/FixedUpdate：GameRoot 调 RenderTick 与 FixedTick
    // 渲染帧=格子→球→落物→喷泉，物理帧=冲量→敌人→玩家受击
    public sealed class CombatRoot : MonoBehaviour, ISceneRoot, IRenderTicked, IPhysicsTicked, IThrowSink
    {
        /// 世界侧排在玩家侧之后
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

        [Tooltip("场景里的泉眼。每帧由本类驱动（落物不是自驱的）。留空则只靠掉落物补给。")]
        [SerializeField] private Fountain[] fountains = new Fountain[0];

        [Tooltip("是否刷敌人。关掉可以只验投掷链路。")]
        [SerializeField] private bool enableWaves = true;

        private GameRoot _root;

        private GridLogic _grid;        private EnemyCellRegistry _registry;

        private BallDirector _balls;

        private ImpulseExecutor _impulses;

        private DropDirector _drops;

        private CombatDirector _combat;
        private TileHighlightView _highlight;

        // 生命神泉的静止累计，随场景重建
        private LifeFountainState _lifeFountain;

        private Fountain _lifeFountainSource;

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

            // 销毁期再问 GameRoot.Instance 会当场造一个新的，只能用 Start 里抓的引用
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

            // 顺序：格子先跑（泥浆可能本帧到期），再推球与落物，最后喷泉
            _grid.Tick(Time.time, deltaTime);

            _balls.Tick(deltaTime);

            _drops.Tick(deltaTime);

            for (int i = 0; i < fountains.Length; i++)
            {
                Fountain fountain = fountains[i];

                if (fountain != null) fountain.Tick(deltaTime);
            }

            TickLifeFountain(deltaTime);

            TickPlanting();
        }

        public void FixedTick(float deltaTime)
        {
            if (!IsReady) return;

            float now = Time.fixedTime;

            // ① 落地冲量：必须在物理帧施加，渲染帧施加会漂
            _impulses.FixedTick();

            // ② 敌人先按本帧位置追一步，再让格子按新位置结算（顺序不能反）
            if (_combat != null) _combat.FixedTick(now, deltaTime);

            // ③ 玩家受击：读物理体位置，须在敌人移动之后
            UpdatePlayerContact(now);
        }

        // 播种：E 键把手上的种子落成环境地貌
        // 合法性判据在 Logic 层（SeedPlanter），本类只转发与报错
        private void TickPlanting()
        {
            InputProvider input = player != null ? player.Input : null;

            if (input == null || !input.PlantPressedThisFrame) return;

            PlayerLogic logic = player.Logic;

            if (logic == null || !logic.Combat.HasAim) return;

            Vector3Int cell = logic.Combat.AimCell;

            if (!SeedPlanter.CanPlant(_grid, cell, logic.Stats.Seed)) return;

            SeedSpec seed = ConfigModule.GetSeed(logic.Stats.Seed);

            if (seed == null) return;

            if (!logic.Stats.TryConsumeSeed()) return;

            _grid.SwitchTileState(cell, seed.SpawnTile);

            EffectContext ctx = EffectContext.At(_grid.Geometry.CellCenter(cell));

            ctx.Radius = _grid.Geometry.CellSize * 0.5f;

            EffectModule.Play(EffectId.Highlight, in ctx);
        }

        // 生命神泉：九宫格内完全静止达阈值即回血
        // 三重静止判据（无输入＋速度近零＋位移近零）由驱动方给定
        private void TickLifeFountain(float deltaTime)
        {
            if (_lifeFountain == null || _lifeFountainSource == null) return;

            PlayerLogic logic = player != null ? player.Logic : null;

            if (logic == null) return;

            InputProvider input = player.Input;

            Vector2 position = player.Position;

            bool hasMoveInput = input != null && input.MoveInput.sqrMagnitude > 0.0001f;

            bool healed = _lifeFountain.Tick(
                position,
                _lifeFountainSource.PlayerInside,
                hasMoveInput,
                player.EngineVelocity.magnitude,
                deltaTime);

            if (!healed) return;

            // 不看 TryHeal 的返回值：healed 已经保证这次该回血，满血顶掉不算错
            logic.Stats.TryHeal();
        }

        // 世界侧两件玩家相关裁决：谁打到玩家、打空怎么重来
        // 判定是纯函数（ContactProbe.TryFindAttacker），可在 EditMode 测
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
                    out float _,
                    out int contactDamage,
                    out _))
            {
                return;
            }

            // 方向由 Damage.At 算，与格子伤害共用同一份方向数学
            Damage damage = Damage.At(
                attacker,
                position,
                contactDamage,
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

            TileChainReactor.Clear();
        }

        // 裁决投掷请求（IThrowSink）：落点合法性属世界信息
        // 唯一否决判据：落点格没有地板（GridLogic.HasCell）；将来的阻挡/占位物加在这里，玩家侧不用改
        public bool RequestThrow(in ThrowIntent intent)
        {
            if (!IsReady) return false;

            if (_grid == null || !_grid.HasCell(intent.Cell)) return false;

            return _balls != null && _balls.Throw(in intent);
        }

        // 组装战斗切片：依赖全来自参数与 Data 层，无 FindObjectOfType
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

            // 元素层两张表：球砸地面的规则网，以及地面与地面的二级反应
            IReadOnlyList<ElementRuleSpec> elementRules = ConfigModule.GetElementRules();

            DuoReactionCatalog duo = ConfigModule.GetDuoReactions();

            ReactionResolver.Initialize(elementRules);

            TileChainReactor.Clear();

            _registry = new EnemyCellRegistry();

            GridGeometry geometry = gridView.ReadGeometry();

            _grid = new GridLogic(
                geometry,
                ConfigModule.GetAllTileStates(),
                CreateTileState,
                duo,
                _registry);

            // 先订阅格子状态变化再灌初始状态，否则那批泥浆不会被画出来
            gridView.Attach();

            int cells = gridView.RegisterCells(_grid);

            // 关卡初始地块优先从场景里的 InitialSetup 笔刷层读（策划在编辑器里画）
            // 没画才退回表驱动的 tile_initial
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

            AttachFountains();

            player.Logic.ConfigureAim(in geometry, playerSpec.MaxThrowDistance, this);

            if (enableWaves)
            {
                _combat = CreateCombatDirector();
            }

            IsReady = true;

            Debug.Log(
                $"[Combat] 装配完成：格子 {cells} 个（初始状态 {initialStates} 个），" +
                $"球种 {balls.Count} 个，泉眼 {fountains.Length} 个（生命神泉 {(_lifeFountain != null ? 1 : 0)} 个），" +
                $"反应规则 {elementRules.Count} 条，二级反应 {duo.Count} 条，" +
                (_lifeFountain != null
                    ? $"回血静止 {_lifeFountain.HealInterval} 秒，"
                    : "（未接线生命神泉：本局没有回血站，请在场景里放一个 kind=Life 的 Fountain）") +
                (enableWaves ? "敌人 启用" : "敌人 关闭（「是否刷敌人」未勾选）"));
        }

        // 泉眼接线：弹药泉挂落物产出
        private void AttachFountains()
        {
            for (int i = 0; i < fountains.Length; i++)
            {
                Fountain fountain = fountains[i];

                if (fountain == null) continue;

                fountain.Attach(_drops);

                if (fountain.Kind != FountainKind.Life) continue;

                if (_lifeFountain != null)
                {
                    Debug.LogWarning(
                        "[Combat] 场景里有多个 kind=Life 的泉眼：只认第一个，其余不会回血。", fountain);

                    continue;
                }

                _lifeFountainSource = fountain;

                _lifeFountain = new LifeFountainState(player.Logic.Stats.Spec.LifeHealInterval);
            }
        }

        // 状态工厂：给 ID 造新实例，null = 该 ID 没有实现
        // 不共享原型：状态自己记持续时长，否则全场共用一个计时器
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
                ConfigModule.GetWaves(),
                ConfigModule.GetEnemy(),
                _grid,
                _registry,
                actorRoot,
                OnEnemyKilled);

            return director;
        }

        // 精英怪战利品：只有 EliteEnemyId 那一行会掉种子
        // 掉落方式走既有 IDropSpawner 约定，本类不加新规则
        private void OnEnemyKilled(EnemySpec spec, Vector2 position)
        {
            if (spec == null || spec.Id != EliteEnemyId) return;

            if (_drops == null) return;

            _drops.TrySpawn(new DropSpawnRequest(DropType.Seed, position, position));

            Debug.Log("[Combat] 精英怪被击杀，掉落了 1 颗战备种子。");
        }

        // 精英怪在 enemy 表里的 id
        private const int EliteEnemyId = 2;

        // 掉落物被领取：世界→玩家通知
        // 裁决在这里而不在掉落物里（掉落物只发事实）；加一种掉落物在这里加 case
        private void OnDropCollected(DropCollected evt)
        {
            PlayerStats stats = player?.Logic?.Stats;

            if (stats == null) return;

            switch (evt.Type)
            {
                case DropType.Water:
                    stats.Refill(ResourceKind.Water);
                    return;

                case DropType.Earth:
                    stats.Refill(ResourceKind.Earth);
                    return;

                case DropType.Seed:
                    stats.GrantSeed(ConfigModule.GetWave().GrantSeed);
                    return;

                default:
                    Debug.LogWarning(
                        $"[Combat] 领取了没有接线效果的掉落物 {evt.Type}（{evt.Amount} 个），本次不产生任何变化。");
                    return;
            }
        }

        // HUD 加载完成时重播：三块读数各播一次当前值
        private void OnRequestHudRefresh(RequestHudRefresh evt)
        {
            player?.Logic?.Stats.Announce();
            _combat?.Announce();
        }
    }
}
