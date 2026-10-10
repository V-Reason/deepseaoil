using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Movement;
using DeepseaOil.Presentation.Adapters;
using DeepseaOil.Presentation.Effects;
using DeepseaOil.Presentation.Visual;
using UnityEngine;

namespace DeepseaOil.Presentation.Actor
{
    public enum EnemyAIType
    {
        Chase = 0,
        Stand = 1,
    }

    /// 一只敌人的组合根：抓预制体上的执行器与视效、推进逻辑层、结算伤害与死亡
    [DisallowMultipleComponent]
    public sealed class EnemyActor : MonoBehaviour, IDamageable, ISlowable, IKnockBackable, IStunnable, IManagedActor, IContactDamager
    {
        [Header("死亡反馈")]
        [Tooltip("死亡时播放的特效；由预制体配置")]
        [SerializeField] private EffectId deathEffect = EffectId.Shatter;

        [Header("AI 与打靶")]
        [Tooltip("AI行为模式：Chase 正常追人，Stand 原地站桩木桩；只对场景木桩生效，波次怪恒为 Chase")]
        [SerializeField] private EnemyAIType aiType = EnemyAIType.Chase;

        [Tooltip("打靶复活延迟（秒）：<=0 正常死亡销毁，>0 原地复活供无限打靶测试；只对场景木桩生效")]
        [SerializeField] private float respawnDelay = 0f;

        private Vector2 _spawnPosition;

        private Coroutine _respawnRoutine;

        private Collider2D _collider;

        public bool IsInitialized => Stats != null;

        /// 权属：true = 场景木桩（吃 Inspector 打靶配置，清场归位）
        public bool IsScenePlaced { get; set; }

        [Header("临时调试件")]
        [Tooltip("头顶耐久数字（预制体上的 Hp_txt 节点）。留空则自取子节点；这是可视化调试件，正式表现应做进预制体美术")]
        [SerializeField] private TextMesh hpText = default;

        private EnemySpec _spec;
        private EnemyLogic _logic;
        private EnemyMotor _motor;

        /// 视觉适配器（挂在子节点 View 上）
        private ActorAnimationView _animView;

        /// 身体渲染器（颜色与排序的唯一写入口），来自 View 的 SpriteRenderer
        private SpriteRenderer _body;

        private Transform _target;
        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private bool _registered;
        private Vector3Int _currentCell;

        /// 格子系统未接线只报一次：逐帧报会刷爆 Console，不报则是"踩地块毫无反应"的静默缺陷
        private bool _gridWiringWarned;

        private float _slowMultiplier = 1f;

        /// <summary>贴身伤害，取自 enemy 表；ContactProbe 经 IContactDamager 读它</summary>
        public int ContactDamage => _spec != null ? _spec.ContactDamage : 0;

        /// <summary>死亡回调，只触发一次；战利品与读数刷新由 CombatDirector 接</summary>
        public System.Action<EnemyActor> Died;

        private bool _deathFired;

        /// 麻痹到期时刻（Time.time 口径），0=没被麻痹过；只记不改移动
        private float _stunnedUntil;

        public bool IsAlive => Stats != null && Stats.IsAlive;

        /// 取执行器物理体位置而非 transform.position，两者迟早有一帧对不上且不报错
        public Vector2 Position => _motor != null ? _motor.Position : (Vector2)transform.position;

        public EnemyStats Stats { get; private set; }

        public int Hp => Stats != null ? Stats.Hp : 0;

        public bool IsHurt => Stats != null && Stats.IsAlive && _logic != null && _logic.IsHurt;
        public Vector2 EngineVelocity => _motor == null ? Vector2.zero : _motor.EngineVelocity;

        /// 本帧生效的减速乘数（1=没被减速）：诊断面板读数，与身体颜色读同一份数据
        public float SlowMultiplier => _slowMultiplier;

        private void Awake()
        {
            BindPrefabParts();
        }

        /// 抓预制体上的执行器与视效，一律不新增组件
        /// 补空不覆盖：Awake 与 Initialize 都会调，编辑器装配路径不跑 Awake
        private void BindPrefabParts()
        {
            if (_motor == null) _motor = GetComponent<EnemyMotor>();

            if (_animView == null) _animView = GetComponentInChildren<ActorAnimationView>(true);

            if (_body == null)
            {
                _body = _animView != null && _animView.Renderer != null
                    ? _animView.Renderer
                    : GetComponentInChildren<SpriteRenderer>(true);
            }

            if (hpText == null) hpText = GetComponentInChildren<TextMesh>(true);

            if (_collider == null) _collider = GetComponent<Collider2D>();
        }

        /// 组装一只敌人，依赖全由参数给出；target=null 则滑停，registry=null 则不登记
        /// IsScenePlaced 为真才吃 Inspector 打靶配置与复活；权属由 CombatRoot 的接管入口置位
        /// 预制体缺 EnemyMotor 是本类唯一当场作废生成的装配错误：留着只会在后续帧炸成空引用
        public void Initialize(
            Vector2 position,
            in EnemySpec spec,
            Transform target,
            Vector2 facing,
            GridLogic grid,
            EnemyCellRegistry registry,
            Transform parent)
        {
            BindPrefabParts();

            if (_motor == null)
            {
                Debug.LogError(
                    $"[Enemy] 预制体根节点没有 EnemyMotor（它 [RequireComponent(Rigidbody2D)]）：{name} 生成已作废。" +
                    "请检查 Assets/Resources/enemies/Enemy_*.prefab 的 Root 组件。",
                    this);

                Destroy(gameObject);

                return;
            }

            _spec = spec;
            _target = target;
            _grid = grid;
            _registry = registry;

            Stats = new EnemyStats(spec);

            transform.position = new Vector3(position.x, position.y, 0f);

            if (parent != null) transform.SetParent(parent, true);

            // 建完刚体立刻固化物理参数，否则首次读位置前的物理步用预制体旧值跑
            _motor.EnsureInitialized();

            _spawnPosition = position;

            IEnemyBrain brain = IsScenePlaced && aiType == EnemyAIType.Stand
                ? EnemyBrainFactory.CreateStand()
                : EnemyBrainFactory.Create(spec);

            _logic = new EnemyLogic(_motor, spec, brain);

            _motor.Facing = facing;

            UpdateHpText();

            UpdateCell(force: true);

            if (IsScenePlaced)
            {
                Debug.Log($"[Enemy] 场景木桩 {name} 已接管：enemy 行 {spec.Id}，AI {aiType}，复活 {respawnDelay} 秒", this);
            }
        }

        /// Play 模式下拖入的怪由此报到：靠 Unity 原生生命周期，不轮询场景树
        private void Start()
        {
            if (!Application.isPlaying || IsInitialized) return;

            CombatRoot combat = CombatRoot.Current;

            if (combat != null) combat.AdoptSceneEnemy(this);
        }

        /// 唯一受伤入口；格子状态转换、近战与陷阱都走这里
        public void TakeDamage(in Damage damage)
        {
            if (Stats == null || !Stats.IsAlive) return;

            if (damage.HasDamage)
            {
                // 耐久是整数、伤害是浮点数，四舍五入到整数
                Stats.ApplyDamage(damage.Amount);
            }

            if (damage.HasKnockback && _logic != null)
            {
                // 只递交，下个逻辑帧才变成"进入受击"；撞多远归 hurtDecay
                _logic.ApplyKnockback(damage.Impulse, damage.Direction);
            }

            if (!Stats.IsAlive)
            {
                _animView?.TriggerDie();

                Die(damage.Direction);
                return;
            }

            _animView?.TriggerHurt();

            UpdateHpText();
        }

        /// 头顶耐久数字（临时调试件）；只在数值变了才写，0 显示空串
        /// 数字挂 Root 下，朝左时 localScale.x 为负，文字会跟着镜像；调试可读即可
        private void UpdateHpText()
        {
            if (hpText == null) return;

            string text = Hp > 0 ? Hp.ToString() : string.Empty;

            if (hpText.text == text) return;

            hpText.text = text;
        }

        /// 由 CombatRoot 每物理帧调：速度一帧只提交一次，视效同频刷新
        public void FixedTick(float now, float deltaTime)
        {
            if (!Stats.IsAlive) return;

            _logic.SetTarget(_target == null ? (Vector2?)null : TargetPosition());
            _logic.Tick(now, deltaTime);

            _slowMultiplier = _logic.Status.SlowScale;

            UpdateCell(force: false);
            UpdateBodyColor();
            UpdateSortingOrder();

            // 运动学快照与移动同频提交：动画与物理体必须看到同一帧真值
            _animView?.SetMotion(EngineVelocity, _motor.Facing, EngineVelocity.magnitude);
        }

        public void ApplySlow(float speedScale, float seconds)
        {
            _logic?.Status.ApplySlow(speedScale, seconds);
        }

        /// 冲量拆成大小+方向再递交，零向量会被逻辑层丢掉
        public void ApplyKnockback(Vector2 impulse)
        {
            if (_logic == null) return;

            float magnitude = impulse.magnitude;

            if (magnitude <= 0f) return;

            _logic.ApplyKnockback(magnitude, impulse / magnitude);
        }

        /// 只记时长不改移动：麻痹生效需挡输入；不静默丢弃
        public void ApplyStun(float seconds)
        {
            if (seconds <= 0f) return;

            _stunnedUntil = Mathf.Max(_stunnedUntil, Time.time + seconds);
        }

        /// 麻痹是否还在生效；只读、暂无消费者
        public bool IsStunned => Time.time < _stunnedUntil;

        private void OnDrawGizmosSelected()
        {
            if (_spec == null) return;

            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, _spec.Radius);

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, _spec.StopDistance);
        }

        private void OnDestroy()
        {
            if (_registry != null && _registered) _registry.Unregister(this);
        }

        /// 闪烁是相位而非状态；hz=频率（Hz），非法值不闪
        /// 用 Sin 而非取模：取模在 hz=0 时除零，Sin 恒为 0
        public static bool IsFlashOn(float time, float hz)
        {
            if (float.IsNaN(hz) || hz <= 0f) return false;

            return Mathf.Sin(time * 2f * Mathf.PI * hz) > 0f;
        }

        /// 闪白相位用 Time.time 而非累加，故每帧刷 color
        private void UpdateBodyColor()
        {
            if (_body == null) return;

            bool flashOn = IsHurt && IsFlashOn(Time.time, _spec.FlashHz);

            _body.color = ConfigModule.Visuals.EnemyBodyColor(_slowMultiplier, flashOn);
        }

        /// 基准取物理体 y 而非 View：View 的 localPosition 会被特效改写，拿它排序会让敌人随抖动乱插队
        private void UpdateSortingOrder()
        {
            if (_body == null) return;

            _body.sortingOrder = RenderOrder.ActorOrder(Position.y);
        }

        private void UpdateCell(bool force)
        {
            if (_registry == null || _grid == null)
            {
                WarnMissingGridOnce();

                return;
            }

            Vector3Int cell = _grid.WorldToCell(Position);

            if (!force && _registered && cell == _currentCell) return;

            _currentCell = cell;

            if (_registered) _registry.Move(this, cell);
            else
            {
                _registry.Register(cell, this);
                _registered = true;
            }

            // 跨格报一次进格：进格的瞬时伤害/击退由格子系统补给这一个目标，持续效果由格状态自己的节拍提交
            _grid.OnActorEnterCell(cell, this);
        }

        private void WarnMissingGridOnce()
        {
            if (_gridWiringWarned) return;

            _gridWiringWarned = true;

            Debug.LogWarning(
                $"[Enemy] {name} 的格子系统没接线（grid / registry 有一个是 null）：这只敌人踩地块不会减速、不会掉血。" +
                "两条生成路径都该注入它们（CombatDirector / CombatDummyHarness）。",
                this);
        }

        private Vector2 TargetPosition()
        {
            Vector3 p = _target.position;

            return new Vector2(p.x, p.y);
        }

        private void Die(Vector2 hitDirection)
        {
            if (_motor != null) _motor.Move(Vector2.zero);

            // 死亡即刻摘掉归属，否则这一格继续"有目标"且结算方拿到正在销毁的对象
            if (_registry != null && _registered)
            {
                _registry.Unregister(this);
                _registered = false;
            }

            // 先发事实再销毁：回调方要用位置（战利品落在死亡那一刻的地方）
            if (!_deathFired)
            {
                _deathFired = true;

                Died?.Invoke(this);
            }

            EffectContext ctx = EffectContext.At(Position, hitDirection);
            ctx.Tint = ConfigModule.Visuals.enemyBodyNormal;

            // 播哪个特效由预制体说了算，本类不写死
            EffectModule.Play(deathEffect, in ctx);

            if (IsScenePlaced && respawnDelay > 0f)
            {
                _respawnRoutine = StartCoroutine(CoRespawn());
                return;
            }

            Destroy(gameObject);
        }

        /// 满血归位：打靶复活与清场复位共用；未接管过的对象（无 spec）不动
        public void ResetToSpawn()
        {
            if (_spec == null) return;

            if (_respawnRoutine != null)
            {
                StopCoroutine(_respawnRoutine);

                _respawnRoutine = null;
            }

            transform.position = new Vector3(_spawnPosition.x, _spawnPosition.y, 0f);

            if (_motor != null)
            {
                _motor.SetPosition(_spawnPosition);
                _motor.Move(Vector2.zero);
            }

            Stats = new EnemyStats(_spec);
            _deathFired = false;

            if (_collider != null) _collider.enabled = true;
            if (_body != null) _body.enabled = true;

            UpdateHpText();
            UpdateCell(force: true);
            _animView?.ResetToDefault();
        }

        private System.Collections.IEnumerator CoRespawn()
        {
            if (_body != null) _body.enabled = false;
            if (hpText != null) hpText.text = string.Empty;
            if (_collider != null) _collider.enabled = false;

            yield return new WaitForSeconds(respawnDelay);

            _respawnRoutine = null;

            ResetToSpawn();
        }
    }
}
