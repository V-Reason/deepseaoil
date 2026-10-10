#if UNITY_EDITOR || DEVELOPMENT_BUILD
using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Grid.States;
using DeepseaOil.Presentation.Actor;
using DeepseaOil.Presentation.Adapters;
using UnityEngine;
using UnityEngine.InputSystem;
using cfg.dso;

namespace DeepseaOil.Presentation.Diagnostics
{
    /// <summary>战斗木桩与受击测试器：站桩敌人 ＋ 六个受击入口，逐条验"扣耐久 / 减速 / 击退 / 碎裂 / 格上减速"</summary>
    /// <remarks>自愈装配配置 / 资源 / GameRoot / 格子系统，缺哪补哪；驱动经 ISceneRoot 交 GameRoot（唯一驱动入口），与 CombatRoot 同场时不重复驱动木桩与格子；缺预制体只报错，不拼白模。</remarks>
    public sealed class CombatDummyHarness : MonoBehaviour, ISceneRoot, IPhysicsTicked, IRenderTicked
    {
        /// 世界侧：格子与木桩在玩家之后推进
        public int Order => SceneOrder.World;

        /// 预制体找不到后的重试间隔（秒）
        private const float PrefabRetryInterval = 2f;

        [Tooltip("木桩。场景里摆了敌人预制体实例就拖进来；留空则按 enemies/Enemy_{id} 约定实例化一只。")]
        [SerializeField] private EnemyActor enemy = default;

        [Tooltip("地板层适配器（可选）。接了才能验「脚下刷泥」：格子要是不登记，格上效果找不到木桩。")]
        [SerializeField] private TilemapAdapter adapter = default;

        [Tooltip("实例化木桩的落点（场景里已摆好木桩时以场景位置为准）。")]
        [SerializeField] private Vector2 spawnPosition = new Vector2(5f, 5f);

        [SerializeField] private Vector2 panelOrigin = new Vector2(8f, 8f);
        [SerializeField] private Vector2 panelSize = new Vector2(380f, 320f);

        [Tooltip("面板放大倍数（相对 IMGUI 默认 12px 字号）。实际倍数还会按屏幕收口，保证面板不过屏幕中线；0/负数按 2 倍兜底。")]
        [SerializeField] private float guiScale = 2f;

        /// 是否自持格子系统（false = 复用 CombatRoot 的）
        private bool _ownsGrid;

        /// 是否已装配过（Attach 必须幂等）
        private bool _attached;

        /// 是否已订阅状态变化事件
        private bool _attachedAdapter;

        /// 木桩是否由本类逐物理帧推进（有 CombatDirector 时归它）
        private bool _drivesEnemy;

        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private GameRoot _root;

        /// 最近一次操作反馈（面板与 Console 各一份）
        private string _lastAction = "(还没有操作)";

        /// 木桩计数，面板显示"第几只"
        private int _dummySerial;

        /// 装配失败后的下次重试时刻（逐物理帧重试会刷满 Console）
        private float _prefabRetryAt;

        /// 上次上报的减速倍率与耐久（只在变化时报一行，验收看"移速被减半"）
        private float _reportedSlow = 1f;

        private int _reportedHp = -1;

        /// 当前木桩的表定移速，报读数用（GetEnemy 会造 Spec，别每帧调）
        private float _spawnMaxSpeed = 1f;

        private void Start()
        {
            EnsureRuntime();

            if (!ConfigModule.IsReady || !ConfigModule.AreAssetsBound)
            {
                Debug.LogError("[Harness] 配置没起来（ConfigModule 未就绪 / 未绑定）：战斗受击切片无法工作。", this);
                return;
            }

            // 开 Logic 层 [Reaction] 追踪，看是哪条规则/效果打的
            ReactionResolver.TraceEnabled = true;

            _root = GameRoot.Instance;
            _root.RegisterSceneRoot(this);
        }

        private void OnDestroy()
        {
            if (_root != null) _root.UnregisterSceneRoot(this);

            if (_attachedAdapter && adapter != null) adapter.Detach();
        }

        /// 自愈装配：配置 → 资源 → GameRoot → 运行态
        private void EnsureRuntime()
        {
            if (!ConfigModule.IsReady) ConfigModule.InitFromStreamingAssets();

            GameRoot root = GameRoot.Instance;

            if (!AssetModule.IsInitialized) AssetModule.Init();

            if (!ConfigModule.AreAssetsBound) ConfigModule.BindAssets();

            if (root.Game != null && root.Game.CurState != GameState.Running)
            {
                root.Game.ChangeState(GameState.Running);

                Debug.Log("[Harness] 已把游戏状态推回 Running（否则卡在 Menu 的暂停态，时间冻结）。");
            }
        }

        /// GameRoot 首个被驱动的帧调一次（幂等）
        public void Attach()
        {
            if (_attached) return;

            _attached = true;

            CombatRoot combat = FindObjectOfType<CombatRoot>();

            if (combat == null || !combat.IsReady)
            {
                BuildLocalGrid();
            }
            else
            {
                _ownsGrid = false;

                Debug.Log("[Harness] 检测到 CombatRoot：格子交给它推进，本类只驱动木桩与发受击指令。");
            }

            // 木桩非 CombatDirector 所刷，永远由本类逐物理帧推进
            _drivesEnemy = true;

            EnsureDummy();
        }

        /// 本地最小运行时：元素层 ＋ 格子层，登记地板格（只为"格上效果"链）
        private void BuildLocalGrid()
        {
            if (adapter == null || !adapter.IsWired)
            {
                Debug.LogWarning(
                    "[Harness] adapter 未接线：木桩仍可受击，但「脚下刷泥」拿不到格子（格上减速链验不了）。",
                    this);

                return;
            }

            _registry = new EnemyCellRegistry();

            ReactionResolver.Initialize(ConfigModule.GetElementRules());

            TileChainReactor.Clear();

            _grid = new GridLogic(
                adapter.ReadGeometry(),
                ConfigModule.GetAllTileStates(),
                CreateTileState,
                ConfigModule.GetDuoReactions(),
                _registry);

            adapter.Attach();
            _attachedAdapter = true;

            _ownsGrid = true;

            int cells = adapter.RegisterCells(_grid);

            Debug.Log($"[Harness] 本地装配最小运行时：登记地板 {cells} 格（格上效果生效的前提）。");
        }

        /// 状态工厂：与 CombatRoot 同一套表驱动实现
        private static ITileState CreateTileState(TileStateType id)
        {
            TileStateSpec spec = ConfigModule.TryGetTileState(id);

            return spec != null ? new TableTileState(spec) : null;
        }

        public void RenderTick(float deltaTime)
        {
            // 格子：泥浆到期、状态切换、待处理转换都在这里结算
            if (_ownsGrid && _grid != null) _grid.Tick(Time.time, deltaTime);

            PollKeys();
        }

        public void FixedTick(float deltaTime)
        {
            if (!_drivesEnemy) return;

            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            // 木桩的移动/受击滑停/登记格子都在这一步里；target 为 null ⇒ 原地站桩
            dummy.FixedTick(Time.fixedTime, deltaTime);

            ReportReadouts(dummy);
        }

        /// 减速倍率与耐久变化才报，逐帧报会刷爆 Console
        private void ReportReadouts(EnemyActor dummy)
        {
            float slow = dummy.SlowMultiplier;

            if (!Mathf.Approximately(slow, _reportedSlow))
            {
                _reportedSlow = slow;

                Debug.Log(slow < 1f
                    ? $"[Harness] 木桩进入减速：移速 ×{slow:F2}（{_spawnMaxSpeed:F2} → {_spawnMaxSpeed * slow:F2} 单位/秒，颜色转减速色）"
                    : $"[Harness] 木桩减速结束：移速恢复到 ×{slow:F2}（{_spawnMaxSpeed:F2} 单位/秒）");
            }

            if (dummy.Hp == _reportedHp) return;

            _reportedHp = dummy.Hp;

            Debug.Log($"[Harness] 木桩耐久：{dummy.Hp}（存活 {dummy.IsAlive}）");
        }

        /// 木桩不在（首次装配 / 刚碎裂）就按约定预制体补一只
        private EnemyActor EnsureDummy()
        {
            if (enemy == null) enemy = SpawnFromPrefab();

            if (enemy == null) return null;

            if (enemy.Stats != null) return enemy;

            Vector2 position = enemy.transform.position;

            EnemySpec spec = ConfigModule.GetEnemy();

            enemy.Initialize(
                position,
                spec,
                null,
                Vector2.right,
                _grid,
                _registry,
                null);

            _spawnMaxSpeed = spec.MaxSpeed;
            _reportedSlow = enemy.SlowMultiplier;
            _reportedHp = enemy.Hp;

            Debug.Log($"[Harness] 木桩初始化：位置 {position}，耐久 {enemy.Hp}，移速 {_spawnMaxSpeed:F2}，格子系统 {(_grid == null ? "未接" : "已接")}。");

            return enemy;
        }

        /// 按 id 寻址 enemies/Enemy_{id} 实例化；找不到就报错返回 null
        /// <remarks>失败后退避 PrefabRetryInterval 秒再试：本方法由逐物理帧的 FixedTick 调，不退避就是每秒 60 条同样报错。</remarks>
        private EnemyActor SpawnFromPrefab()
        {
            if (Time.time < _prefabRetryAt) return null;

            string prefabKey = $"enemies/Enemy_{ConfigModule.GetEnemy().Id}";

            GameObject prefab = AssetModule.IsInitialized ? AssetModule.Load<GameObject>(prefabKey) : null;

            if (prefab == null)
            {
                Debug.LogError($"[Harness] 严重阻断：未找到敌人预制体 Assets/Resources/{prefabKey}.prefab！木桩无法生成。请先在 Unity 中创建该预制体。");

                _prefabRetryAt = Time.time + PrefabRetryInterval;

                return null;
            }

            GameObject go = Instantiate(prefab, spawnPosition, Quaternion.identity);
            EnemyActor actor = go.GetComponent<EnemyActor>();

            if (actor == null)
            {
                Debug.LogError($"[Harness] 预制体 {prefabKey} 根节点未挂载 EnemyActor 组件！木桩生成已作废。");
                Destroy(go);

                _prefabRetryAt = Time.time + PrefabRetryInterval;

                return null;
            }

            _prefabRetryAt = 0f;

            _dummySerial++;
            go.name = $"Enemy_Dummy_{_dummySerial}";

            Debug.Log($"[Harness] 已在 {spawnPosition} 实例化第 {_dummySerial} 只木桩（预制体 {prefabKey}）。");

            return actor;
        }

        /// 六个入口各对应一键：1 单次受击 / 2 减速 / 3 击退 / 4 致死 / 5 脚下刷泥 / 6 脚下点燃
        private void PollKeys()
        {
            Keyboard keyboard = Keyboard.current;

            if (keyboard == null) return;

            if (keyboard.digit1Key.wasPressedThisFrame) HitOnce();
            if (keyboard.digit2Key.wasPressedThisFrame) Slow();
            if (keyboard.digit3Key.wasPressedThisFrame) Knockback();
            if (keyboard.digit4Key.wasPressedThisFrame) Kill();
            if (keyboard.digit5Key.wasPressedThisFrame) CutTileUnderfoot(TileStateType.Mud, "泥浆");
            if (keyboard.digit6Key.wasPressedThisFrame) CutTileUnderfoot(TileStateType.FlameField, "燎原火海");
        }

        /// [1] 单次受击：验扣耐久与头顶数字
        private void HitOnce()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            Damage damage = Damage.At(Vector2.zero, dummy.Position, 1f, DamageSource.Tile);

            dummy.TakeDamage(in damage);

            Report($"单次受击 1 点 → 耐久 {dummy.Hp}（存活 {dummy.IsAlive}）");
        }

        /// [2] 减速：验变色（观感取 VisualPalette 减速色）
        private void Slow()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            dummy.ApplySlow(0.5f, 3f);

            Report("施加减速 0.5× / 3s → 身体应转减速色");
        }

        /// [3] 击退：验冲量滑停
        private void Knockback()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            dummy.ApplyKnockback(Vector2.up * 8f);

            Report("施加击退 up × 8 → 应被推走再滑停");
        }

        /// [4] 致死：验死亡与 Shatter 碎片粒子
        private void Kill()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            Damage damage = Damage.At(Vector2.zero, dummy.Position, 999f, DamageSource.Tile);

            dummy.TakeDamage(in damage);

            Report("致死 999 点 → 木桩碎裂（Shatter）；再按任意键会自动补一只");
        }

        /// [5]/[6] 把木桩所站格切成目标状态，验"格上减速 / 格上掉血"
        private void CutTileUnderfoot(TileStateType state, string label)
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            if (_grid == null)
            {
                Report($"脚下刷{label}：格子未接线（adapter 没接）→ 这条链验不了");

                Debug.LogWarning($"[Harness] 脚下刷{label}需要 TilemapAdapter：格子没登记就找不到木桩所在的格。", this);

                return;
            }

            Vector3Int cell = _grid.WorldToCell(dummy.Position);

            _grid.Transition(cell, state);

            Report($"脚下刷{label}：格 {cell} 已提交切 {state}（下一帧结算；减速看 Console 的「进入减速」行，燃烧看耐久跳字）");
        }

        private void Report(string fact)
        {
            _lastAction = fact;

            Debug.Log($"[Harness] {fact}");
        }

        private void OnGUI()
        {
            Matrix4x4 saved = GUI.matrix;
            float scale = HarnessGui.Scale(guiScale, panelSize);

            // 整体缩放：控件坐标按设计值写，屏幕占位与字号一起放大（倍数按屏幕收口，见 HarnessGui）
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            GUILayout.BeginArea(new Rect(panelOrigin.x, panelOrigin.y, panelSize.x, panelSize.y), GUI.skin.box);

            GUILayout.Label("CombatDummyHarness · 战斗受击切片");

            if (enemy == null)
            {
                GUILayout.Label("木桩: 已碎裂（按任意键 / 点按钮自动补一只）");
            }
            else
            {
                string cell = _grid == null ? "无" : _grid.WorldToCell(enemy.Position).ToString();

                GUILayout.Label($"木桩: 耐久 {enemy.Hp}   存活 {enemy.IsAlive}   受击 {enemy.IsHurt}");
                GUILayout.Label($"减速 ×{enemy.SlowMultiplier:F2}   所处格 {cell}");
                GUILayout.Label($"位置: ({enemy.Position.x:F2}, {enemy.Position.y:F2})");
            }

            GUILayout.Label($"格子系统: {(_grid == null ? "未接线" : $"已登记 {_grid.CellCount} 格")}   状态格: {(_grid == null ? 0 : _grid.ActiveStateCount)}");

            if (GUILayout.Button("[1] 单次受击（扣耐久）")) HitOnce();
            if (GUILayout.Button("[2] 施加减速 0.5× / 3s（变色）")) Slow();
            if (GUILayout.Button("[3] 施加击退 up × 8（冲量滑停）")) Knockback();
            if (GUILayout.Button("[4] 致死碎裂 999（Shatter 碎片）")) Kill();
            if (GUILayout.Button("[5] 脚下刷泥浆（格上自动减速）")) CutTileUnderfoot(TileStateType.Mud, "泥浆");
            if (GUILayout.Button("[6] 脚下生火海（格上持续掉血）")) CutTileUnderfoot(TileStateType.FlameField, "燎原火海");

            GUILayout.Label($"最近一次: {_lastAction}");

            GUILayout.EndArea();

            GUI.matrix = saved;
        }
    }
}
#endif
