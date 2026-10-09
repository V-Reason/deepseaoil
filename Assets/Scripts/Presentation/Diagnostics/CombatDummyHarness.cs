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
    /// <summary>战斗木桩与受击测试器：一只站着不动的敌人 ＋ 五个不同来源的受击入口，逐条验"扣耐久 / 减速 / 击退 / 碎裂 / 格上减速"</summary>
    /// <remarks>
    /// 自愈装配：ConfigModule / AssetModule / GameRoot / GameState 与格子系统，缺哪补哪；场景里没有敌人就按约定预制体实例化一只（缺预制体只报错，不拼白模）。
    /// 驱动不走 Update / FixedUpdate：实现 ISceneRoot ＋ IRenderTicked / IPhysicsTicked 交给 GameRoot（唯一驱动入口）。
    /// 与 CombatRoot 同场时不再自己驱动木桩与格子（避免一帧两次扣血），木桩也交给 CombatDirector。
    /// </remarks>
    public sealed class CombatDummyHarness : MonoBehaviour, ISceneRoot, IPhysicsTicked, IRenderTicked
    {
        /// <summary>世界侧：格子与木桩都在玩家之后推进</summary>
        public int Order => SceneOrder.World;

        /// <summary>预制体找不到后的重试间隔（秒）</summary>
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

        /// <summary>本类是否自己握了格子系统（false = 复用 CombatRoot 的）</summary>
        private bool _ownsGrid;

        /// <summary>是否已经装配过（ISceneRoot.Attach 必须幂等）</summary>
        private bool _attached;

        /// <summary>本类是否自己订阅了状态变化事件</summary>
        private bool _attachedAdapter;

        /// <summary>木桩是否由本类逐物理帧推进（有 CombatDirector 时归它）</summary>
        private bool _drivesEnemy;

        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private GameRoot _root;

        /// <summary>最近一次操作的可见反馈（面板与 Console 各一份）</summary>
        private string _lastAction = "(还没有操作)";

        /// <summary>致死碎裂后木桩计数，面板上显示"第几只"</summary>
        private int _dummySerial;

        /// <summary>预制体装配失败后的下次寻址时刻；逐物理帧重试会把 Console 刷满，反而盖住别的日志</summary>
        private float _prefabRetryAt;

        /// <summary>上一只木桩的减速倍率 / 耐久读数：只在变化时报一行，验收靠它看"移速被减半"与"耐久跳字"</summary>
        private float _reportedSlow = 1f;

        private int _reportedHp = -1;

        /// <summary>当前这只木桩的表定移速，报读数用（GetEnemy 会现场造 Spec，别每帧调）</summary>
        private float _spawnMaxSpeed = 1f;

        private void Start()
        {
            EnsureRuntime();

            if (!ConfigModule.IsReady || !ConfigModule.AreAssetsBound)
            {
                Debug.LogError("[Harness] 配置没起来（ConfigModule 未就绪 / 未绑定）：战斗受击切片无法工作。", this);
                return;
            }

            // 木桩被格子结算时，追踪开关有助于看清是哪条规则/效果打上来的。
            ReactionResolver.TraceEnabled = true;

            _root = GameRoot.Instance;
            _root.RegisterSceneRoot(this);
        }

        private void OnDestroy()
        {
            if (_root != null) _root.UnregisterSceneRoot(this);

            if (_attachedAdapter && adapter != null) adapter.Detach();
        }

        /// <summary>自愈装配：配置 → 资源 → GameRoot → 运行态</summary>
        private void EnsureRuntime()
        {
            if (!ConfigModule.IsReady) ConfigModule.InitFromStreamingAssets();

            GameRoot root = GameRoot.Instance;

            if (!AssetModule.IsInitialized) AssetModule.Init();

            if (!ConfigModule.AreAssetsBound) ConfigModule.BindAssets();

            // GameRoot 默认停在 Menu（暂停 ⇒ Time.deltaTime 为 0，击退与减速都不动）
            if (root.Game != null && root.Game.CurState != GameState.Running)
            {
                root.Game.ChangeState(GameState.Running);

                Debug.Log("[Harness] 已把游戏状态推回 Running（否则卡在 Menu 的暂停态，时间冻结）。");
            }
        }

        /// <summary>GameRoot 在第一个被驱动的帧调一次（幂等）</summary>
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
                // 格子归 CombatRoot 推进，本类不重复 Tick（一帧两次 DoT 会翻倍）
                _ownsGrid = false;

                Debug.Log("[Harness] 检测到 CombatRoot：格子交给它推进，本类只驱动木桩与发受击指令。");
            }

            // 木桩不是 CombatDirector 刷出来的（它是场景件或本类自建），故永远由本类逐物理帧推进
            _drivesEnemy = true;

            EnsureDummy();
        }

        /// <summary>本地装一套最小运行时：元素层 ＋ 格子层，登记地板格（只为"格上效果"这条链）</summary>
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

            var element = new TileElementReactor(ConfigModule.GetElementRules());

            _grid = new GridLogic(
                adapter.ReadGeometry(),
                ConfigModule.GetAllTileStates(),
                CreateTileState,
                element,
                _registry);

            adapter.Attach();
            _attachedAdapter = true;

            _ownsGrid = true;

            int cells = adapter.RegisterCells(_grid);

            Debug.Log($"[Harness] 本地装配最小运行时：登记地板 {cells} 格（格上效果生效的前提）。");
        }

        /// <summary>状态工厂：与 CombatRoot 同一套表驱动实现</summary>
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

        /// <summary>减速倍率与耐久的变化才报，逐帧报会把 Console 刷爆（验收要看到的正是这两行读数）</summary>
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

        /// <summary>木桩在不在；不在（首次装配 / 上一只刚碎裂）就按约定预制体补一只</summary>
        /// <remarks>不再现场拼白模：敌人长什么样是预制体的事，缺件只报错不兜底。</remarks>
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

        /// <summary>按种类 id 寻址 enemies/Enemy_{id} 并实例化；找不到就报错返回 null（不静默自愈）</summary>
        /// <remarks>失败后退避 <see cref="PrefabRetryInterval"/> 秒再试：本方法由逐物理帧的 FixedTick 调，不退避就是每秒 60 条同样的报错。</remarks>
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

        /// <summary>面板外的六个入口都对应一个按键：1 单次受击 / 2 减速 / 3 击退 / 4 致死 / 5 脚下刷泥 / 6 脚下点燃</summary>
        private void PollKeys()
        {
            Keyboard keyboard = Keyboard.current;

            if (keyboard == null) return;

            if (keyboard.digit1Key.wasPressedThisFrame) HitOnce();
            if (keyboard.digit2Key.wasPressedThisFrame) Slow();
            if (keyboard.digit3Key.wasPressedThisFrame) Knockback();
            if (keyboard.digit4Key.wasPressedThisFrame) Kill();
            if (keyboard.digit5Key.wasPressedThisFrame) CutTileUnderfoot(TileStateType.Mud, "泥浆");
            if (keyboard.digit6Key.wasPressedThisFrame) CutTileUnderfoot(TileStateType.Burn, "燃烧");
        }

        /// <summary>[1] 单次受击：验扣耐久与头顶数字</summary>
        private void HitOnce()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            Damage damage = Damage.At(Vector2.zero, dummy.Position, 1f, DamageSource.Tile);

            dummy.TakeDamage(in damage);

            Report($"单次受击 1 点 → 耐久 {dummy.Hp}（存活 {dummy.IsAlive}）");
        }

        /// <summary>[2] 减速：验变色（观感取 VisualPalette 的减速色）</summary>
        private void Slow()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            dummy.ApplySlow(0.5f, 3f);

            Report("施加减速 0.5× / 3s → 身体应转减速色");
        }

        /// <summary>[3] 击退：验冲量滑停</summary>
        private void Knockback()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            dummy.ApplyKnockback(Vector2.up * 8f);

            Report("施加击退 up × 8 → 应被推走再滑停");
        }

        /// <summary>[4] 致死：验死亡与 Shatter 碎片粒子</summary>
        private void Kill()
        {
            EnemyActor dummy = EnsureDummy();

            if (dummy == null) return;

            Damage damage = Damage.At(Vector2.zero, dummy.Position, 999f, DamageSource.Tile);

            dummy.TakeDamage(in damage);

            Report("致死 999 点 → 木桩碎裂（Shatter）；再按任意键会自动补一只");
        }

        /// <summary>[5]/[6] 脚下改地块：把木桩当前所站格切成目标状态，验"格上减速 / 格上掉血"</summary>
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

            // 整体缩放：控件坐标仍按设计值写，屏幕占位与字号一起放大（倍数按屏幕收口，见 HarnessGui）
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
            if (GUILayout.Button("[6] 脚下点燃燃烧（格上持续掉血）")) CutTileUnderfoot(TileStateType.Burn, "燃烧");

            GUILayout.Label($"最近一次: {_lastAction}");

            GUILayout.EndArea();

            GUI.matrix = saved;
        }
    }
}
#endif
