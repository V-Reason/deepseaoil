using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Movement;
using DeepseaOil.Presentation.Adapters;
using DeepseaOil.Presentation.Effects;
using DeepseaOil.Presentation.Primitive;
using DeepseaOil.Presentation.Visual;
using UnityEngine;

namespace DeepseaOil.Presentation.Actor
{
    /// <summary>一只敌人：建刚体与视效、推进逻辑层、结算伤害与死亡，是这只敌人的组合根</summary>
    /// <remarks>受伤只有一条路：TakeDamage。脚底中心每帧登记进 EnemyCellRegistry，格子按"人站在哪一格"结算。不自己驱动：由 CombatDirector 逐只 FixedTick，自驱会让帧内顺序不可预测。挂接口前先问是否一种独立能力。</remarks>
    [DisallowMultipleComponent]
    public sealed class EnemyActor : MonoBehaviour, IDamageable, ISlowable, IKnockBackable, IStunnable, IManagedActor
    {
        private const float HpTextCharacterSize = 0.13f;

        /// <summary>头顶数字垂直偏移，0=压在圆心（锚点 MiddleCenter）</summary>
        private const float HpTextOffsetY = 0f;

        /// <summary>内置字体候选名，新→旧排；旧名在 2022.3 抛 ArgumentException 而非返回 null，故逐个 try</summary>
        private static readonly string[] BuiltinFontNames = { "LegacyRuntime.ttf", "Arial.ttf" };

        private EnemySpec _spec;
        private EnemyLogic _logic;
        private EnemyMotor _motor;

        /// <summary>视觉适配器，挂在子节点 View 上；Root 只承担物理真值与组合根</summary>
        private ActorAnimationView _animView;

        private TextMesh _hpText;
        private Transform _target;
        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private bool _registered;
        private Vector3Int _currentCell;

        /// <summary>本帧生效的减速乘数（视效读数），速度由门禁经账本落地；一份数据两个消费者</summary>
        private float _slowMultiplier = 1f;

        /// <summary>麻痹到期时刻（Time.time 口径），0=没被麻痹过；只记不改移动</summary>
        private float _stunnedUntil;

        public bool IsAlive => Stats != null && Stats.IsAlive;

        /// <remarks>取执行器物理体位置而非 transform.position，两者迟早有一帧对不上且不报错</remarks>
        public Vector2 Position => _motor != null ? _motor.Position : (Vector2)transform.position;

        public EnemyStats Stats { get; private set; }

        public int Hp => Stats != null ? Stats.Hp : 0;

        public bool IsHurt => Stats != null && Stats.IsAlive && _logic != null && _logic.IsHurt;
        public Vector2 EngineVelocity => _motor == null ? Vector2.zero : _motor.EngineVelocity;

        /// <summary>本帧生效的减速乘数（1=没被减速）：诊断面板读数用，与身体颜色读的是同一份数据</summary>
        public float SlowMultiplier => _slowMultiplier;

        /// <summary>组装一只敌人，依赖全部由参数给出；target=null 则随即滑停，registry=null 则不登记</summary>
        public void Initialize(
            Vector2 position,
            in EnemySpec spec,
            Transform target,
            Vector2 facing,
            GridLogic grid,
            EnemyCellRegistry registry,
            Transform parent)
        {
            _spec = spec;
            _target = target;
            _grid = grid;
            _registry = registry;

            Stats = new EnemyStats(spec);

            transform.position = new Vector3(position.x, position.y, 0f);

            if (parent != null) transform.SetParent(parent, true);

            BuildBody();

            _motor = gameObject.AddComponent<EnemyMotor>();

            // 建完刚体立刻固化物理参数，否则到首次读位置间的物理步用默认重力跑
            _motor.EnsureInitialized();

            _logic = new EnemyLogic(_motor, spec);

            _motor.Facing = facing;

            BuildVisuals();
            UpdateCell(force: true);
        }

        /// <summary>唯一受伤入口；格子状态转换、近战与陷阱都走这里</summary>
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
                // 只递交，下一次逻辑帧才由状态效果层变成"进入受击"；撞多远归 hurtDecay
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

        /// <remarks>由 CombatDirector 调用而非 Update：速度一个物理帧只提交一次，视效同频刷新以免一帧不同步。</remarks>
        public void FixedTick(float now, float deltaTime)
        {
            if (!Stats.IsAlive) return;

            _logic.SetTarget(_target == null ? (Vector2?)null : TargetPosition());
            _logic.Tick(now, deltaTime);

            _slowMultiplier = _logic.Status.SlowScale;

            UpdateCell(force: false);
            UpdateBodyColor();
            UpdateSortingOrder();

            // 运动学快照与移动同频提交：动画的朝向/速度与物理体必须看到同一帧的真值
            _animView?.SetMotion(EngineVelocity, _motor.Facing, EngineVelocity.magnitude);
        }

        public void ApplySlow(float speedScale, float seconds)
        {
            _logic?.Status.ApplySlow(speedScale, seconds);
        }

        /// <remarks>冲量拆成大小+方向再递交，零向量会被逻辑层当成没方向丢掉</remarks>
        public void ApplyKnockback(Vector2 impulse)
        {
            if (_logic == null) return;

            float magnitude = impulse.magnitude;

            if (magnitude <= 0f) return;

            _logic.ApplyKnockback(magnitude, impulse / magnitude);
        }

        /// <remarks>本轮只记时长不改移动：麻痹要生效得挡住输入，Actor 侧的计时器装配线还没拉；保证不静默丢弃。</remarks>
        public void ApplyStun(float seconds)
        {
            if (seconds <= 0f) return;

            _stunnedUntil = Mathf.Max(_stunnedUntil, Time.time + seconds);
        }

        /// <summary>麻痹是否还在生效；只读、暂无消费者</summary>
        public bool IsStunned => Time.time < _stunnedUntil;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, _spec.Radius);

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, _spec.StopDistance);
        }

        private void OnDestroy()
        {
            if (_registry != null && _registered) _registry.Unregister(this);
        }

        private void BuildBody()
        {
            gameObject.AddComponent<Rigidbody2D>();

            var collider = gameObject.AddComponent<CircleCollider2D>();
            collider.radius = _spec.Radius;
        }

        /// <summary>建视觉子节点 View：Root 只留物理与逻辑，抖动/后坐力/伪高度将来只改 View 的 localPosition</summary>
        /// <remarks>HpText 仍挂在 Root 下：数字不该跟着 View 抖，且它的 y 就是物理体中心。</remarks>
        private void BuildVisuals()
        {
            var view = new GameObject("View");

            view.transform.SetParent(transform, false);

            SpriteRenderer body = view.AddComponent<SpriteRenderer>();

            PrimitiveSprites.Configure(
                body,
                PrimitiveSprites.Circle,
                ConfigModule.Visuals.enemyBodyNormal,
                RenderOrder.ActorOrder(Position.y),
                _spec.Radius * 2f);

            _animView = view.AddComponent<ActorAnimationView>();

            BuildHpText();
            UpdateHpText();
            UpdateBodyColor();
        }

        /// <summary>建耐久数字；字体取不到就不建（TextMesh 无字体会画成方块），不报错</summary>
        private void BuildHpText()
        {
            Font font = BuiltinFont();

            if (font == null) return;

            var go = new GameObject("HpText");

            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, HpTextOffsetY, 0f);

            _hpText = go.AddComponent<TextMesh>();

            _hpText.font = font;
            _hpText.fontSize = 64;
            _hpText.characterSize = HpTextCharacterSize;

            // 锚点居中配合偏移 0；LowerCenter 会让数字往上长
            _hpText.anchor = TextAnchor.MiddleCenter;
            _hpText.alignment = TextAlignment.Center;
            _hpText.color = Color.white;

            // 材质必须从字体上取，不给就是粉红方块
            var textRenderer = _hpText.GetComponent<MeshRenderer>();

            textRenderer.sharedMaterial = font.material;

            // sortingOrder 必须显式设，否则数字会和自己的身体抢先后
            textRenderer.sortingOrder = RenderOrder.ActorOverlay;
        }

        private static Font BuiltinFont()
        {
            for (int i = 0; i < BuiltinFontNames.Length; i++)
            {
                try
                {
                    Font font = Resources.GetBuiltinResource<Font>(BuiltinFontNames[i]);

                    if (font != null) return font;
                }
                catch (System.ArgumentException)
                {
                    // 这个版本不认这个名字，试下一个
                }
            }

            return null;
        }

        /// <summary>头顶耐久数字；0 时是空串（显示"0"会让人以为还有 0 点血）</summary>
        public static string HpText(int hp)
        {
            return hp > 0 ? hp.ToString() : string.Empty;
        }

        /// <summary>这一帧该不该亮；闪烁是相位而非状态。hz=频率（Hz），非法值按不闪处理。</summary>
        /// <remarks>用 Sin 而非取模：取模在 hz=0 时除零，Sin 恒为 0。</remarks>
        public static bool IsFlashOn(float time, float hz)
        {
            if (float.IsNaN(hz) || hz <= 0f) return false;

            return Mathf.Sin(time * 2f * Mathf.PI * hz) > 0f;
        }

        private void UpdateHpText()
        {
            if (_hpText == null) return;

            string text = HpText(Hp);

            if (_hpText.text == text) return;

            _hpText.text = text;
        }

        /// <remarks>闪白相位用 Time.time 而非累加（累加会随帧率漂）；EffectId.Flash 驱动未实现，故每帧刷 color。</remarks>
        private void UpdateBodyColor()
        {
            SpriteRenderer body = _animView != null ? _animView.Renderer : null;

            if (body == null) return;

            bool flashOn = IsHurt && IsFlashOn(Time.time, _spec.FlashHz);

            body.color = ConfigModule.Visuals.EnemyBodyColor(_slowMultiplier, flashOn);
        }

        /// <remarks>基准取 Root 的物理体 y 而非 View 的 transform y：View 的 localPosition 会被特效改写，拿它排序会让敌人随抖动乱插队。</remarks>
        private void UpdateSortingOrder()
        {
            SpriteRenderer body = _animView != null ? _animView.Renderer : null;

            if (body == null) return;

            body.sortingOrder = RenderOrder.ActorOrder(Position.y);
        }

        private void UpdateCell(bool force)
        {
            if (_registry == null || _grid == null) return;

            Vector3Int cell = _grid.WorldToCell(Position);

            if (!force && _registered && cell == _currentCell) return;

            _currentCell = cell;

            if (_registered) _registry.Move(this, cell);
            else
            {
                _registry.Register(cell, this);
                _registered = true;
            }
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

            EffectModule.Play(EffectId.Shatter, in ctx);

            Destroy(gameObject);
        }
    }
}
