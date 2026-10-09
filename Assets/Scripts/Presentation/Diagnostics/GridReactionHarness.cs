#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Grid.States;
using DeepseaOil.Presentation.Adapters;
using UnityEngine;
using UnityEngine.InputSystem;
using cfg.dso;

namespace DeepseaOil.Presentation.Diagnostics
{
    /// <summary>地块反应独立驱动器：不打球、不投掷，直接用鼠标把"球元素"砸到格子顶端，专验元素反应链与地块效果链</summary>
    /// <remarks>
    /// 自愈装配：ConfigModule / AssetModule / GameRoot / GameState 与格子系统，缺哪补哪 —— 本场景可以单独打开进 Play，
    /// 也可以和 CombatRoot 同场（此时复用它的格子系统，不重复登记、不重复 Tick）。
    /// 驱动不走 Update：实现 ISceneRoot + IRenderTicked 由 GameRoot 推进（全工程唯一驱动入口，见 AGENTS 第 5 节）。
    /// 落地事实由 Logic 层的 [Reaction] 追踪输出（ReactionResolver.TraceEnabled），本类只负责点、选与读数。
    /// </remarks>
    public sealed class GridReactionHarness : MonoBehaviour, ISceneRoot, IRenderTicked
    {
        /// <summary>世界侧与 CombatRoot 同序：自己 Tick 格子时排在玩家之后即可</summary>
        public int Order => SceneOrder.World;

        [Tooltip("地板层适配器。必接：格子几何与合法格集合都从它来。")]
        [SerializeField] private TilemapAdapter adapter = default;

        [Tooltip("面板左上角原点（屏幕像素，左上为原点）。")]
        [SerializeField] private Vector2 panelOrigin = new Vector2(8f, 8f);

        [Tooltip("面板尺寸。要装得下 11 个球种按钮，别调太小。")]
        [SerializeField] private Vector2 panelSize = new Vector2(380f, 520f);

        [Tooltip("面板放大倍数（相对 IMGUI 默认 12px 字号）。实际倍数还会按屏幕收口，保证面板不过屏幕中线；0/负数按 2 倍兜底。")]
        [SerializeField] private float guiScale = 2f;

        /// <summary>本类是否自己持有格子系统：false = 复用 CombatRoot 的（那么 Tick 也归它）</summary>
        private bool _ownsGrid;

        /// <summary>本类是否订阅了状态变化事件（只在自己 Attach 过适配器时负责退订）</summary>
        private bool _attachedAdapter;

        private GridLogic _grid;
        private TileElementReactor _element;
        private EnemyCellRegistry _registry;

        private GameRoot _root;
        private Camera _camera;

        /// <summary>球种清单，配置就绪后取一次</summary>
        private IReadOnlyList<ProjectileSpec> _balls;

        /// <summary>状态号 → 中文名，只在面板上显示；构造期取一次，避免每帧查表</summary>
        private readonly Dictionary<TileStateType, string> _stateNames = new();

        private int _selectedBall;

        private Vector3Int _hoverCell;
        private bool _hasHover;

        private string _lastFact = "(还没有落地过)";

        private void Start()
        {
            EnsureRuntime();

            if (!ConfigModule.IsReady || !ConfigModule.AreAssetsBound)
            {
                Debug.LogError("[Harness] 配置没起来（ConfigModule 未就绪 / 未绑定）：地块反应切片无法工作。", this);
                return;
            }

            // 切片就是来看反应链的：默认打开 Logic 层的 [Reaction] 追踪。
            ReactionResolver.TraceEnabled = true;

            _balls = ConfigModule.GetAllBalls();
            BuildStateNameTable();

            _root = GameRoot.Instance;
            _root.RegisterSceneRoot(this);
        }

        private void OnDestroy()
        {
            if (_root != null) _root.UnregisterSceneRoot(this);

            if (_attachedAdapter && adapter != null) adapter.Detach();
        }

        /// <summary>自愈装配：配置 → 资源 → GameRoot → 运行态</summary>
        /// <remarks>顺序不能反：GameRoot.Assemble 自己会做 ConfigModule.Init → AssetModule.Init → BindAssets，先建它最省事。场景里已有 GameRoot 时它的 Awake 早已跑完，这里的判断全成 no-op。</remarks>
        private void EnsureRuntime()
        {
            if (!ConfigModule.IsReady) ConfigModule.InitFromStreamingAssets();

            // 场景里没有 GameRoot 就现造一个最小运行时（取 Instance 时会建对象并跑它的 Awake）
            GameRoot root = GameRoot.Instance;

            if (!AssetModule.IsInitialized) AssetModule.Init();

            if (!ConfigModule.AreAssetsBound) ConfigModule.BindAssets();

            // GameRoot 开局把自己停在 Menu（= 暂停，deltaTime 为 0），切片必须推回 Running 才能看到时间流动
            if (root.Game != null && root.Game.CurState != GameState.Running)
            {
                GameState previous = root.Game.CurState;

                root.Game.ChangeState(GameState.Running);

                Debug.Log($"[Harness] 已把游戏状态 {previous} 推回 Running（Menu / Paused 都是暂停态，时间会冻结）。");
            }
        }

        /// <summary>GameRoot 在第一个被驱动的帧调一次：拿不到 CombatRoot 的格子系统就地装一套最小运行时</summary>
        public void Attach()
        {
            if (_grid != null) return;

            if (adapter == null || !adapter.IsWired)
            {
                Debug.LogError(
                    "[Harness] GridReactionHarness.adapter 未接线（或没有接 Tilemap）：拿不到地板格，点击不会产生任何效果。",
                    this);

                return;
            }

            CombatRoot combat = FindObjectOfType<CombatRoot>();

            if (combat != null && combat.IsReady && combat.Grid != null)
            {
                _grid = combat.Grid;
                _ownsGrid = false;

                Debug.Log($"[Harness] 复用 CombatRoot 的格子系统：已登记 {_grid.CellCount} 格（Tick 归 CombatRoot，本类不重复推进）。");
            }
            else
            {
                BuildLocalGrid();
            }

            Debug.Log($"[Harness] 地块反应切片就绪：合法格 {_grid.CellCount} 个，球种 {_balls?.Count ?? 0} 个。");
        }

        /// <summary>本地装一套最小运行时：元素层（反应规则）＋ 格子层（状态与效果），并把地板格登记进去</summary>
        private void BuildLocalGrid()
        {
            _registry = new EnemyCellRegistry();

            // 元素层由组合根装配：GridLogic 只收端口，不认识规则表
            _element = new TileElementReactor(ConfigModule.GetElementRules());

            _grid = new GridLogic(
                adapter.ReadGeometry(),
                ConfigModule.GetAllTileStates(),
                CreateTileState,
                _element,
                _registry);

            // 先订阅"状态变了"再登记：否则开局那一批初始状态不会被画出来
            adapter.Attach();
            _attachedAdapter = true;

            _ownsGrid = true;

            int cells = adapter.RegisterCells(_grid);

            // 与 CombatRoot 同一口径：初始地块优先读场景里的 InitialSetup 笔刷层，没刷才退回表驱动。
            // 本切片没有 CombatRoot（这是它唯一的驱动器），少了这一行 InitialSetup 上刷的东西在这张场景里永远不会生效。
            int initialStates = adapter.LoadInitialSetupTiles(_grid);

            if (initialStates == 0)
            {
                initialStates = _grid.LoadInitialStates(ConfigModule.GetTileInitials());
            }

            Debug.Log(
                $"[Harness] 本地装配最小运行时：登记地板 {cells} 格，初始状态 {initialStates} 个，" +
                $"反应规则 {ConfigModule.GetElementRules().Count} 条。");
        }

        /// <summary>状态工厂：给 ID 造新实例，null=该 ID 没有实现（与 CombatRoot 用同一套表驱动实现）</summary>
        private static ITileState CreateTileState(TileStateType id)
        {
            TileStateSpec spec = ConfigModule.TryGetTileState(id);

            return spec != null ? new TableTileState(spec) : null;
        }

        /// <summary>渲染帧：格子先走（泥浆会到期、状态会切），再处理悬停与点击</summary>
        public void RenderTick(float deltaTime)
        {
            if (_grid == null) return;

            // 时间与 Δt 由驱动方给：格子层自己不读 Time
            if (_ownsGrid) _grid.Tick(Time.time, deltaTime);

            UpdateHover();

            if (PollWorldClick()) DropSelectedBall();
        }

        /// <summary>鼠标在 z=0 平面上的世界点；没有鼠标（无指针设备）时返回 false</summary>
        private bool TryMouseWorld(out Vector2 world)
        {
            world = Vector2.zero;

            Mouse mouse = Mouse.current;

            if (mouse == null) return false;

            if (_camera == null) _camera = Camera.main;

            if (_camera == null) return false;

            Vector2 screen = mouse.position.ReadValue();

            // 正交相机：取到 z=0 平面的深度用相机自身的 -z，别用远裁剪面（会随相机漂）
            float depth = Mathf.Abs(_camera.transform.position.z);

            Vector3 point = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth));

            world = new Vector2(point.x, point.y);

            return true;
        }

        private void UpdateHover()
        {
            _hasHover = false;

            if (!TryMouseWorld(out Vector2 world)) return;

            _hoverCell = _grid.WorldToCell(world);
            _hasHover = true;
        }

        /// <summary>左键按下沿；面板上的点击不算落地（IMGUI 的 y 轴朝下，要翻过来判）</summary>
        private bool PollWorldClick()
        {
            Mouse mouse = Mouse.current;

            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return false;

            Vector2 screen = mouse.position.ReadValue();

            float guiY = Screen.height - screen.y;

            if (PanelRect().Contains(new Vector2(screen.x, guiY)))
            {
                return false;
            }

            return true;
        }

        /// <summary>把选中的球元素砸到悬停格上：唯一入口是 GridLogic.OnBallHit</summary>
        private void DropSelectedBall()
        {
            if (_balls == null || _balls.Count == 0) return;

            if (!_hasHover) return;

            Vector3Int cell = _hoverCell;

            if (!_grid.HasCell(cell))
            {
                Debug.LogWarning($"[Harness] 格 {cell} 没有登记地板（不在 Ground 层上）：本次落地被丢弃。");

                return;
            }

            ProjectileSpec ball = _balls[_selectedBall];

            TileStateType before = _grid.StateOf(cell);

            bool changed = _grid.OnBallHit(cell, ball.Element);

            TileStateType after = _grid.StateOf(cell);

            _lastFact = $"格 {cell}：{ball.Name} → {Label(before)} ⇒ {Label(after)}（{(changed ? "已转换" : "未转换")}）";

            Debug.Log($"[Harness] 落地：球={ball.Name} 元素={ball.Element} 格={cell} 状态 {before} → {after}（changed={changed}）");
        }

        /// <summary>把脚下格直接切成泥浆（不走反应，专验"格上效果"链）</summary>
        private void CutMudUnderCursor()
        {
            if (!_hasHover || !_grid.HasCell(_hoverCell)) return;

            _grid.Transition(_hoverCell, TileStateType.Mud);

            _lastFact = $"格 {_hoverCell}：已提交切泥（下一帧结算）";
        }

        private void BuildStateNameTable()
        {
            IReadOnlyList<TileStateSpec> states = ConfigModule.GetAllTileStates();

            for (int i = 0; i < states.Count; i++)
            {
                _stateNames[states[i].Id] = states[i].Name;
            }
        }

        private string Label(TileStateType state)
        {
            return _stateNames.TryGetValue(state, out string name) ? $"{state}({name})" : state.ToString();
        }

        /// <summary>面板在屏幕上的实际矩形：落点判定用，倍数与 OnGUI 同一口径</summary>
        private Rect PanelRect() => HarnessGui.ScreenRect(panelOrigin, panelSize, HarnessGui.Scale(guiScale, panelSize));

        private void OnGUI()
        {
            Matrix4x4 saved = GUI.matrix;
            float scale = HarnessGui.Scale(guiScale, panelSize);

            // 整体缩放：控件坐标仍按设计值写，屏幕占位与字号一起放大（落点判定按放大后的算）
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            GUILayout.BeginArea(new Rect(panelOrigin.x, panelOrigin.y, panelSize.x, panelSize.y), GUI.skin.box);

            GUILayout.Label("GridReactionHarness · 地块反应切片");
            GUILayout.Label($"合法格: {(_grid == null ? 0 : _grid.CellCount)}   有状态格: {(_grid == null ? 0 : _grid.ActiveStateCount)}");

            GUILayout.Label(_hasHover
                ? $"悬停格: {_hoverCell}   当前状态: {Label(_grid.StateOf(_hoverCell))}"
                : "悬停格: (鼠标不在场景里)");

            GUILayout.Label($"已注册格数: {(_grid == null ? 0 : _grid.CellCount)}   元素记录格: {(_element == null ? 0 : _element.ElementCellCount)}");
            GUILayout.Label("选中球种后左键点格落地：");

            if (_balls != null)
            {
                for (int i = 0; i < _balls.Count; i++)
                {
                    ProjectileSpec ball = _balls[i];

                    string caption = (i == _selectedBall ? "▶ " : "   ") + $"{ball.Name}（T{ball.Element.Temperature}/W{ball.Element.Wet}/C{ball.Element.Conductivity}/Tags{(int)ball.Element.Tags}）";

                    if (GUILayout.Button(caption))
                    {
                        _selectedBall = i;

                        Debug.Log($"[Harness] 选中球种：{ball.Name}");
                    }
                }
            }

            GUILayout.BeginHorizontal();

            bool trace = GUILayout.Toggle(ReactionResolver.TraceEnabled, " [Reaction] 追踪", GUILayout.Width(150f));

            if (trace != ReactionResolver.TraceEnabled) ReactionResolver.TraceEnabled = trace;

            if (GUILayout.Button("悬停格刷泥（验格上减速）")) CutMudUnderCursor();

            GUILayout.EndHorizontal();

            GUILayout.Label($"最近一次落地: {_lastFact}");
            GUILayout.Label("提示：只有登记过地板的格才吃反应；贴图按 tiles/Tile_<状态> 懒加载。");

            GUILayout.EndArea();

            GUI.matrix = saved;
        }
    }
}
#endif
