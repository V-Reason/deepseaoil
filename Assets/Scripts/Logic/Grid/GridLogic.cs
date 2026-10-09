using System;
using System.Collections.Generic;
using UnityEngine;
using cfg.dso;
using DeepseaOil.Data;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Element;
using DeepseaOil.Logic.Events;

namespace DeepseaOil.Logic.Grid
{
    /// <summary>格子系统逻辑门面，持有格子状态、调度 Tick、处理落地与状态转换、对格上目标结算效果</summary>
    /// <remarks>伤害全由格子产生（Apply），来源是状态配置行。Tick 请求双缓冲：本帧提交、下帧消费；状态转换进待处理表，Tick 循环后统一结算。表数据全部构造注入，元素合成与规则匹配在元素层，本类不碰 Tilemap/物理/Time/ConfigModule。</remarks>
    public sealed class GridLogic : ITileScheduler, ITileResolver
    {
        /// <summary>击退衰减率（1/秒），只用于格→冲量换算；与 EnemyTuning.knockbackDecay 同源，那边改了这边要跟（见 Docs/待办.md）</summary>
        private const float KnockbackDecayPerSecond = 10f;

        public GridGeometry Geometry => _geometry;

        public EnemyCellRegistry Registry => _registry;

        /// <summary>当前有状态的格数（诊断用，0=场上无泥浆之类）</summary>
        public int ActiveStateCount => _machines.Count;

        public int CellCount => _cells.Count;

        private readonly GridGeometry _geometry;
        private readonly Dictionary<TileStateType, TileStateSpec> _specs = new();
        private readonly Func<TileStateType, ITileState> _stateFactory;
        private readonly EnemyCellRegistry _registry;
        private readonly IElementReactor _element;

        /// <summary>合法格，只有登记过的格能被转换/结算；表现层从 Tilemap 枚举地板登记</summary>
        private readonly HashSet<Vector3Int> _cells = new();

        private readonly Dictionary<Vector3Int, TileStateMachine> _machines = new();

        private readonly TileTickQueue _queue = new();

        /// <summary>待生效状态转换，Tick 循环后统一结算，同格后者胜</summary>
        private readonly Dictionary<Vector3Int, TileStateType> _pending = new();
        private readonly List<KeyValuePair<Vector3Int, TileStateType>> _pendingScratch = new();

        /// <summary>结算时目标快照缓冲，受害方可能在结算里死亡并注销自己</summary>
        private readonly List<IEffectTarget> _dealScratch = new();

        /// <summary>待结算的"跨格进入"事件：物理帧登记（表现层换格时），本类 Tick 统一结算 —— 效果施加因此只有一个相位</summary>
        private readonly List<(Vector3Int Cell, IEffectTarget Target)> _pendingEnters = new();

        /// <summary>已告警过的（格, 效果号）：状态每帧重新提交清单，不设闸门会把 Console 刷爆</summary>
        private readonly HashSet<(int X, int Y, int Z, int Kind)> _warnedEffects = new();

        private float _now;
        private float _deltaTime;

        /// <remarks>
        /// stateFactory 返回 null=该 ID 没有实现；element=null 时落地不产生反应；registry=null 时自建。
        /// 效果数值不在这里注入：tile_effect 表的档位在数据层（<c>TileStateSpec</c> / <c>ElementRuleSpec</c>）已按
        /// <c>effectValuePos</c> 定值，本类只消费 <see cref="TileEffectValue"/>。传整张表进来再按效果号查档，
        /// 只会覆盖掉调用方选好的档位（燃烧第 2 档曾被钉回第 1 档 ⇒ 0.5 点 ⇒ 取整成 0 ⇒ 完全不掉血）。
        /// </remarks>
        public GridLogic(
            in GridGeometry geometry,
            IReadOnlyList<TileStateSpec> stateSpecs,
            Func<TileStateType, ITileState> stateFactory,
            IElementReactor element = null,
            EnemyCellRegistry registry = null)
        {
            _geometry = geometry;
            _stateFactory = stateFactory;
            _element = element;
            _registry = registry ?? new EnemyCellRegistry();

            if (stateSpecs != null)
            {
                for (int i = 0; i < stateSpecs.Count; i++)
                {
                    _specs[stateSpecs[i].Id] = stateSpecs[i];
                }
            }
        }

        /// <summary>登记一个合法格，同时按该格原本是什么地从 tile_state 表灌一次元素</summary>
        /// <remarks>D9 只覆盖切状态，常规格没人刷过、元素恒为空，整条反应链判据会不同。默认状态由 LoadInitialStates 给，两处都登记时后到者优先。</remarks>
        public void RegisterCell(Vector3Int cell, TileStateType initial = TileStateType.Normal)
        {
            if (!_cells.Add(cell)) return;

            SeedElement(cell, initial);
        }

        private void SeedElement(Vector3Int cell, TileStateType initial)
        {
            if (_element == null) return;

            if (_machines.ContainsKey(cell)) return;

            if (!_specs.TryGetValue(initial, out TileStateSpec spec)) return;

            _element.FlushStateElement(cell, in spec);
        }

        /// <summary>本格是否合法（存在地板），不合法时落地无效果</summary>
        public bool HasCell(Vector3Int cell)
        {
            return _cells.Contains(cell);
        }

        public Vector3Int WorldToCell(Vector2 world)
        {
            return _geometry.WorldToCell(world);
        }

        public TileStateType StateOf(Vector3Int cell)
        {
            return _machines.TryGetValue(cell, out TileStateMachine machine)
                ? machine.CurrentId
                : TileStateType.Normal;
        }

        /// <summary>按关卡数据灌入初始状态，刻意不产生伤害，无地板的格忽略不报错</summary>
        public int LoadInitialStates(IReadOnlyList<TileInitial> states)
        {
            if (states == null) return 0;

            int applied = 0;

            for (int i = 0; i < states.Count; i++)
            {
                TileInitial state = states[i];

                var cell = new Vector3Int(state.CellX, state.CellY, 0);

                if (!_cells.Contains(cell)) continue;

                RegisterCell(cell, state.StateId);

                if (state.StateId == TileStateType.Normal) continue;

                if (SwitchState(cell, state.StateId, applyEnterImpact: false)) applied++;
            }

            return applied;
        }

        public void Tick(float now, float deltaTime)
        {
            _now = now;
            _deltaTime = deltaTime;

            // 进格效果先结算：换格发生在物理帧，效果落在这里（渲染帧），且要早于本节拍的周期清单
            DrainPendingEnters();

            _queue.Swap();

            IReadOnlyList<Vector3Int> cells = _queue.Current;

            for (int i = 0; i < cells.Count; i++)
            {
                Vector3Int cell = cells[i];

                if (!_machines.TryGetValue(cell, out TileStateMachine machine)) continue;

                machine.Tick(BuildContext(cell));
            }

            DrainPendingTransitions();
        }

        /// <summary>一次球落地：合成球元素与地形元素→查反应规则→切结果状态并提交效果清单</summary>
        /// <remarks>返回 false：落在地板外、规则不给状态（None）或结果就是当前状态。没切状态时把元素改动收回去，否则兜底行（结果 None）会把合成结果留在格子上。</remarks>
        public bool OnBallHit(Vector3Int cell, in ElementValue ballElement)
        {
            if (!_cells.Contains(cell)) return false;

            if (_element == null) return false;

            TileStateType current = StateOf(cell);

            if (!_specs.TryGetValue(current, out TileStateSpec currentSpec)) return false;

            ElementReaction reaction = _element.React(cell, in ballElement, in currentSpec);

            if (reaction.Next == TileStateType.None)
            {
                _element.FlushStateElement(cell, in currentSpec);

                // 规则命中但"不改地形"（兜底行的击退这类）：地形不变，效果照样要落地。
                // 曾经这里直接 return ⇒ 兜底行的冲量被闷在判定里，"砸中了却什么都不发生"。
                ApplyEnterImpact(cell, TileStateType.None, reaction.Effects);

                return false;
            }

            bool changed = SwitchState(cell, reaction.Next, applyEnterImpact: true, reaction.Effects);

            if (!changed) _element.FlushStateElement(cell, in currentSpec);

            return changed;
        }

        public void ScheduleTick(Vector3Int cell)
        {
            _queue.Schedule(cell);
        }

        /// <remarks>同格一帧内被请求多次时后者胜。</remarks>
        public void Transition(Vector3Int cell, TileStateType next)
        {
            _pending[cell] = next;
        }

        /// <summary>效果总出口：对格上目标结算一次效果，改格子自身的效果按种类转交 ApplyToCell</summary>
        /// <remarks>按目标能力分流：受伤/减速/击退/麻痹各吃各的；玩家不在归属表里（D7），泥浆不减速玩家。未实现的效果种类必须出声（WarnUnsupported），不许 default: return 吞掉。瞬时伤害/DoT/减速/击退/麻痹五条分支零分配。</remarks>
        public void Apply(Vector3Int cell, in TileEffectValue effect)
        {
            switch (effect.Kind)
            {
                case TileEffectKind.InstantDamage:
                    Deal(cell, effect.Amount);
                    return;

                case TileEffectKind.DamageOverTime:
                    // 数值与节奏都取"状态自己那一档"：effect 在数据层已按 effectValuePos 定好值（燃烧第 2 档 = 1 点/秒）。
                    // 🔴 这里曾经再查一次表并钉死第 1 档，把燃烧静默降成 0.5 —— 而 EnemyStats 按 RoundToInt 取整，
                    // 0.5 舍成 0 ⇒ 站在燃烧上一点都不掉血（实测）。
                    Deal(cell, effect.PerTick);
                    return;

                case TileEffectKind.Slow:
                    ApplySlow(cell, effect.Scale, effect.Seconds);
                    return;

                case TileEffectKind.KnockBack:
                    ApplyKnockback(cell, effect.Cells);
                    return;

                case TileEffectKind.Numbness:
                    ApplyStun(cell, effect.Seconds);
                    return;

                // 这两条是"改格子自身"的效果，本就该走 ApplyToCell；状态的效果清单只有一个提交口
                // （TableTileState.SubmitAll 与 ApplyEnterImpact 都调 Apply），故在这里按种类转交，
                // 否则它们会落进 default 被当成"未实现"逐格告警 —— 反应配了没效果的最坏形态。
                case TileEffectKind.InheritElement:
                case TileEffectKind.ClearPlants:
                    ApplyToCell(cell, in effect);
                    return;

                // 无效果是合法取值（表里没填 / 解析成 None），不是"未实现"，静默跳过。
                case TileEffectKind.None:
                    return;

                // Slide 本轮未实现（缺滑行能力接口）；Skid/Block/Fixed 连枚举位都还没有，走到这里说明有人把表号硬塞了进来。
                default:
                    WarnUnsupported(cell, effect.Kind);
                    return;
            }
        }

        /// <summary>地形改写通道：只改 cell 自身（D4：状态实现不许碰别的格）；温湿度继承与清除植物都走这里</summary>
        /// <remarks>注意与 D9 的顺序：切状态时本口在 SwitchState 的 OnEnter 里先跑，随后 SwitchState 会把新状态表的元素初值刷到格上（FlushStateElement），因此"继承小球属性"的结果会被状态初值覆盖。要让继承真的留在格上，需要把进格效果挪到 flush 之后 —— 那是格→元素链的口径变更，不在本轮范围内。</remarks>
        public void ApplyToCell(Vector3Int cell, in TileEffectValue effect)
        {
            if (_element == null) return;

            switch (effect.Kind)
            {
                case TileEffectKind.InheritElement:
                {
                    ElementValue current = _element.GetElement(cell);

                    _element.SetElement(cell, new ElementValue(
                        current.Type,
                        current.Tags,
                        Mathf.RoundToInt(current.Temperature * effect.TemperatureRatio),
                        Mathf.RoundToInt(current.Wet * effect.WetRatio),
                        Mathf.RoundToInt(current.Conductivity * effect.ConductivityRatio)));

                    return;
                }

                case TileEffectKind.ClearPlants:
                {
                    ElementValue current = _element.GetElement(cell);

                    // 只清本格：表里范围是十字，跨格需要"对邻居下命令"的通道（D4）。
                    _element.SetElement(cell, new ElementValue(
                        current.Type,
                        current.Tags & ~ElementTag.Plant,
                        current.Temperature,
                        current.Wet,
                        current.Conductivity));

                    return;
                }

                case TileEffectKind.None:
                    return;

                // 伤害/减速/击退这类效果该走 Apply（对格上目标），走到本格口说明调用方选错了口。
                default:
                    WarnUnsupported(cell, effect.Kind);
                    return;
            }
        }

        public void SetCellElement(Vector3Int cell, in ElementValue element)
        {
            _element?.SetElement(cell, in element);
        }

        /// <summary>未实现 / 未完全支持的地块效果：必须出声，严禁 default: return 静默吞掉</summary>
        /// <remarks>按（格, 效果号）只报一次：同一格的状态每帧都会重新提交清单，逐帧报会把 Console 刷爆，而报过一次已足够定位配置事故。字符串只在首次命中时构造，Supported 效果不受影响（那些分支直接 return，无分配）。</remarks>
        private void WarnUnsupported(Vector3Int cell, TileEffectKind kind)
        {
            if (!_warnedEffects.Add((cell.x, cell.y, cell.z, (int)kind))) return;

            Debug.LogWarning($"[Grid] 收到未完全支持的地块效果: {kind}（位于格 {cell}），当前跳过执行。");
        }

        /// <summary>续一次减速修饰，不做快照；离开泥浆⇒不再续命⇒修饰自然过期。</summary>
        /// <remarks>
        /// 🔴 这里**不再**把 seconds&lt;=0 替换成一帧 Δt：表里没填时长就是"只要在格子上就持续生效"，
        /// 窗口交给消费者（StatusGroup）按自己的拍决定。生产者这一侧替换等于把窗口钉成渲染帧 0.0167s，
        /// 而消费者按物理帧 0.02s 扣 —— 短于一拍就被扣穿，表现成"贴着走也不减速"。暂停帧 Δt=0 也不凭空续命。
        /// </remarks>
        private void ApplySlow(Vector3Int cell, float speedScale, float seconds)
        {
            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is ISlowable target) target.ApplySlow(speedScale, seconds);
            }
        }

        /// <summary>目标跨格进入某格：把该格当前状态的"进格一下"效果补给这一个目标，不波及同格其他人</summary>
        /// <remarks>表现层在目标换格时调；登记后随最近一次 Tick 结算，同一格多人各进各算。</remarks>
        public void OnActorEnterCell(Vector3Int cell, IEffectTarget target)
        {
            if (target == null) return;

            _pendingEnters.Add((cell, target));
        }

        private void DrainPendingEnters()
        {
            if (_pendingEnters.Count == 0) return;

            for (int i = 0; i < _pendingEnters.Count; i++)
            {
                (Vector3Int cell, IEffectTarget target) = _pendingEnters[i];

                ApplyEnterEffectsToOne(cell, target);
            }

            _pendingEnters.Clear();
        }

        /// <summary>进格那一下：只补给这一个目标，且只补"一次性"的效果（持续/周期效果由状态节拍负责）</summary>
        private void ApplyEnterEffectsToOne(Vector3Int cell, IEffectTarget target)
        {
            if (target == null || !target.IsAlive) return;

            if (!_cells.Contains(cell)) return;

            if (!_machines.TryGetValue(cell, out TileStateMachine machine)) return;

            if (!_specs.TryGetValue(machine.CurrentId, out TileStateSpec spec)) return;

            Vector2 center = _geometry.CellCenter(cell);

            for (int i = 0; i < spec.EnterEffects.Count; i++)
            {
                TileEffectValue effect = spec.EnterEffects[i];

                if (!effect.IsEnterOnly) continue;

                switch (effect.Kind)
                {
                    case TileEffectKind.InstantDamage:
                        DealOne(target, center, effect.Amount);
                        break;

                    case TileEffectKind.KnockBack:
                        KnockOne(target, center, effect.Cells);
                        break;
                }
            }
        }

        /// <summary>按格数击退：冲量 = 格数 × 格边长 × 击退衰减率</summary>
        /// <remarks>value1 单位是格，ApplyKnockback 收速度（单位/秒）；总位移 ≈ 冲量/衰减率，故乘回衰减率。换算在执行者。</remarks>
        private void ApplyKnockback(Vector3Int cell, float cells)
        {
            if (cells <= 0f) return;

            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            Vector2 center = _geometry.CellCenter(cell);

            for (int i = 0; i < targets.Count; i++)
            {
                KnockOne(targets[i], center, cells);
            }
        }

        /// <summary>对单个目标按格数击退（方向按格心→目标算）</summary>
        private void KnockOne(IEffectTarget target, Vector2 center, float cells)
        {
            if (cells <= 0f || target == null || !target.IsAlive) return;

            if (target is not IKnockBackable knockbackable) return;

            float magnitude = cells * Mathf.Max(_geometry.CellSize, 0f) * KnockbackDecayPerSecond;

            if (magnitude <= 0f) return;

            Vector2 delta = target.Position - center;

            // 格心方向为零：与 Damage.At 同纪律，给"上"。
            Vector2 direction = delta.sqrMagnitude > 1e-6f ? delta.normalized : Vector2.up;

            knockbackable.ApplyKnockback(direction * magnitude);
        }

        /// <summary>麻痹：时长交给目标，是否进门禁由目标决定（本轮只到"提交"层）。</summary>
        private void ApplyStun(Vector3Int cell, float seconds)
        {
            if (seconds <= 0f) return;

            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is IStunnable target) target.ApplyStun(seconds);
            }
        }

        /// <summary>对格上目标结算一次伤害（方向按格心→受害者各算一份）</summary>
        private void Deal(Vector3Int cell, float amount)
        {
            if (amount <= 0f) return;

            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            _dealScratch.Clear();
            _dealScratch.AddRange(targets);

            Vector2 center = _geometry.CellCenter(cell);

            for (int i = 0; i < _dealScratch.Count; i++)
            {
                DealOne(_dealScratch[i], center, amount);
            }
        }

        /// <summary>对单个目标结算一次伤害（方向按格心→受害者算）</summary>
        private static void DealOne(IEffectTarget target, Vector2 center, float amount)
        {
            if (amount <= 0f) return;

            if (target == null) return;

            if (!target.IsAlive) return;

            // 不能受伤的目标照样能被减速与击退，只是不吃伤害。
            if (target is not IDamageable damageable) return;

            damageable.TakeDamage(Damage.At(center, target.Position, amount, DamageSource.Tile));
        }

        /// <summary>切换某格状态，同状态时 no-op（不重入、不重置计时）；applyEnterImpact=是否给进格冲击：球落地与状态自发起给，开局加载绝不给</summary>
        /// <remarks>enterEffects=null 用状态自带的 spec.EnterEffects。</remarks>
        public bool SwitchState(
            Vector3Int cell,
            TileStateType next,
            bool applyEnterImpact,
            IReadOnlyList<TileEffectValue> enterEffects = null)
        {
            if (!_cells.Contains(cell)) return false;

            // 配置里没有这一行就不算转换。
            if (next != TileStateType.Normal && !_specs.ContainsKey(next)) return false;

            if (!_machines.TryGetValue(cell, out TileStateMachine machine))
            {
                // 常规→常规不建状态机。
                if (next == TileStateType.Normal) return false;

                machine = new TileStateMachine();

                RegisterStateFactories(machine);
            }

            bool changed = machine.SwitchTo(next, BuildContext(cell));

            if (!changed)
            {
                // 空状态机要摘掉。
                if (machine.Current == null) _machines.Remove(cell);

                return false;
            }

            if (machine.CurrentId == TileStateType.Normal) _machines.Remove(cell);
            else _machines[cell] = machine;

            // D9：切状态时把该状态元素四件刷到格子上作初值。
            if (_element != null && _specs.TryGetValue(next, out TileStateSpec spec))
            {
                _element.FlushStateElement(cell, in spec);
            }

            EventBus<TileStateChanged>.Publish(new TileStateChanged(cell, next));

            if (applyEnterImpact) ApplyEnterImpact(cell, next, enterEffects);

            return true;
        }

        /// <summary>提交进格冲击：规则给的清单（落地那一下的劲）与状态自带的"进格一下"都要落地</summary>
        /// <remarks>
        /// 🔴 两者不是二选一。曾经写成"给了规则清单就用它，否则用状态清单"：规则清单**为空**（多数 `element_rule` 行的
        /// effects 列是空的）时，状态自己的进格效果被整段吞掉 —— 冰沙的"瞬时伤害"就是这么丢的（它只在周期清单里被跳过，
        /// 见 TableTileState.SubmitAll 的 IsEnterOnly 分支，于是谁都不打）。
        /// 状态清单里的非一次性效果（减速/持续伤害）仍由 Tick 节拍负责，这里只补一次性那部分。
        /// </remarks>
        private void ApplyEnterImpact(Vector3Int cell, TileStateType state, IReadOnlyList<TileEffectValue> enterEffects)
        {
            bool hasState = _specs.TryGetValue(state, out TileStateSpec spec);

            if (enterEffects == null || enterEffects.Count == 0)
            {
                // 没人给清单：状态自带清单全量落地一次（原行为）
                if (!hasState) return;

                ApplyEffectList(cell, spec.EnterEffects, enterOnly: false);

                return;
            }

            ApplyEffectList(cell, enterEffects, enterOnly: false);

            // 规则清单存在时，状态自己的"进格一下"仍要补上（它在周期清单里被跳过，没人补就永远不打）
            if (hasState) ApplyEffectList(cell, spec.EnterEffects, enterOnly: true);
        }

        private void ApplyEffectList(Vector3Int cell, IReadOnlyList<TileEffectValue> effects, bool enterOnly)
        {
            if (effects == null) return;

            for (int i = 0; i < effects.Count; i++)
            {
                TileEffectValue effect = effects[i];

                if (effect.Kind == TileEffectKind.None) continue;

                if (enterOnly && !effect.IsEnterOnly) continue;

                Apply(cell, in effect);
            }
        }

        private void DrainPendingTransitions()
        {
            if (_pending.Count == 0) return;

            // 先搬走再处理：处理中发起的新请求留到下一帧，连锁有确定上界。
            _pendingScratch.Clear();

            foreach (KeyValuePair<Vector3Int, TileStateType> pair in _pending)
            {
                _pendingScratch.Add(pair);
            }

            _pending.Clear();

            for (int i = 0; i < _pendingScratch.Count; i++)
            {
                KeyValuePair<Vector3Int, TileStateType> pair = _pendingScratch[i];

                SwitchState(pair.Key, pair.Value, applyEnterImpact: true);
            }

            _pendingScratch.Clear();
        }

        private void RegisterStateFactories(TileStateMachine machine)
        {
            if (_stateFactory == null) return;

            // 只有配置里出现过的 ID 才注册工厂。
            foreach (KeyValuePair<TileStateType, TileStateSpec> pair in _specs)
            {
                TileStateType id = pair.Key;

                machine.Register(id, () => _stateFactory(id));
            }
        }

        private TileContext BuildContext(Vector3Int cell)
        {
            return new TileContext(cell, _now, _deltaTime, this, this);
        }
    }
}
