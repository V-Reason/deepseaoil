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
    /// 一只敌人的组合根：抓预制体上的执行器与视效、推进逻辑层、结算伤害与死亡
    /// 身体、碰撞体半径、渲染件、死亡特效全由预制体决定（enemies/Enemy_{id}.prefab）：本类不 AddComponent、不建头顶数字，缺件只报错不兜底；受伤只有一条路 TakeDamage；脚底中心每帧登记进 EnemyCellRegistry，格子按"人站在哪一格"结算；不自己驱动，由 CombatDirector 逐只 FixedTick
    [DisallowMultipleComponent]
    public sealed class EnemyActor : MonoBehaviour, IDamageable, ISlowable, IKnockBackable, IStunnable, IManagedActor
    {
        [Header("死亡反馈")]
        [Tooltip("死亡时播放的特效；由预制体配置")]
        [SerializeField] private EffectId deathEffect = EffectId.Shatter;

        [Header("临时调试件")]
        [Tooltip("头顶耐久数字（预制体上的 Hp_txt 节点）。留空则自取子节点；这是可视化调试件，正式表现应做进预制体美术")]
        [SerializeField] private TextMesh hpText = default;

        private EnemySpec _spec;
        private EnemyLogic _logic;
        private EnemyMotor _motor;

        /// 视觉适配器（挂在子节点 View 上）；Root 只承担物理真值与组合根
        private ActorAnimationView _animView;

        /// 身体渲染器（颜色与排序的唯一写入口），来自预制体 View 上的 SpriteRenderer
        private SpriteRenderer _body;

        private Transform _target;
        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private bool _registered;
        private Vector3Int _currentCell;

        /// 格子系统未接线只报一次：逐帧报会把 Console 刷爆，不报就是"踩地块毫无反应"的静默缺陷
        private bool _gridWiringWarned;

        /// 本帧生效的减速乘数（视效读数），速度由门禁经账本落地；一份数据两个消费者
        private float _slowMultiplier = 1f;

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

            // 临时调试件：预制体上的头顶数字
            if (hpText == null) hpText = GetComponentInChildren<TextMesh>(true);
        }

        /// 组装一只敌人，依赖全由参数给出；target=null 则滑停，registry=null 则不登记
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

            _logic = new EnemyLogic(_motor, spec);

            _motor.Facing = facing;

            UpdateHpText();

            UpdateCell(force: true);
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
        /// 数字挂在 Root 下，朝左时 Root 的 localScale.x 为负，文字会跟着镜像；调试可读即可
        private void UpdateHpText()
        {
            if (hpText == null) return;

            string text = Hp > 0 ? Hp.ToString() : string.Empty;

            if (hpText.text == text) return;

            hpText.text = text;
        }

        /// 由 CombatDirector 调用而非 Update：速度一个物理帧只提交一次，视效同频刷新
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

        /// 冲量拆成大小+方向再递交，零向量会被逻辑层当成没方向丢掉
        public void ApplyKnockback(Vector2 impulse)
        {
            if (_logic == null) return;

            float magnitude = impulse.magnitude;

            if (magnitude <= 0f) return;

            _logic.ApplyKnockback(magnitude, impulse / magnitude);
        }

        /// 只记时长不改移动：麻痹生效需挡输入，Actor 侧计时器装配线未拉；不静默丢弃
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

        /// 这一帧该不该亮；闪烁是相位而非状态。hz=频率（Hz），非法值按不闪处理
        /// 用 Sin 而非取模：取模在 hz=0 时除零，Sin 恒为 0
        public static bool IsFlashOn(float time, float hz)
        {
            if (float.IsNaN(hz) || hz <= 0f) return false;

            return Mathf.Sin(time * 2f * Mathf.PI * hz) > 0f;
        }

        /// 闪白相位用 Time.time 而非累加；EffectId.Flash 驱动未实现，故每帧刷 color
        private void UpdateBodyColor()
        {
            if (_body == null) return;

            bool flashOn = IsHurt && IsFlashOn(Time.time, _spec.FlashHz);

            _body.color = ConfigModule.Visuals.EnemyBodyColor(_slowMultiplier, flashOn);
        }

        /// 基准取 Root 物理体 y 而非 View 的 transform y：View 的 localPosition 会被特效改写，拿它排序会让敌人随抖动乱插队
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

            // 跨格（含首次登记）报一次进格：进格那一下的瞬时伤害/击退由格子系统补给这一个目标，
            // 持续与周期效果仍由格状态自己的节拍提交
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

            EffectContext ctx = EffectContext.At(Position, hitDirection);
            ctx.Tint = ConfigModule.Visuals.enemyBodyNormal;

            // 播哪个特效由预制体说了算，本类不写死
            EffectModule.Play(deathEffect, in ctx);

            Destroy(gameObject);
        }
    }
}
