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
    /// <summary>格子系统门面：持有状态与调度</summary>
    /// <remarks>伤害全由格子产生（Apply）。Tick 请求双缓冲：本帧提交、下帧消费；状态转换进待处理表、Tick 后结算。</remarks>
    public sealed class GridLogic : ITileScheduler, ITileResolver
    {
        /// <summary>击退衰减率（1/秒）；与 EnemyTuning.knockbackDecay 同源，改了要跟</summary>
        private const float KnockbackDecayPerSecond = 10f;

        public GridGeometry Geometry => _geometry;

        public EnemyCellRegistry Registry => _registry;

        public int ActiveStateCount => _machines.Count;

        public int CellCount => _cells.Count;

        private readonly GridGeometry _geometry;
        private readonly Dictionary<TileStateType, TileStateSpec> _specs = new();
        private readonly Func<TileStateType, ITileState> _stateFactory;
        private readonly EnemyCellRegistry _registry;
        private readonly IElementReactor _element;

        /// <summary>合法格；表现层登记，未登记不参与转换</summary>
        private readonly HashSet<Vector3Int> _cells = new();

        private readonly Dictionary<Vector3Int, TileStateMachine> _machines = new();

        private readonly TileTickQueue _queue = new();

        /// <summary>待生效转换，Tick 后结算，后者胜</summary>
        private readonly Dictionary<Vector3Int, TileStateType> _pending = new();
        private readonly List<KeyValuePair<Vector3Int, TileStateType>> _pendingScratch = new();

        /// <summary>目标快照：受害方可能在结算中死亡注销</summary>
        private readonly List<IEffectTarget> _dealScratch = new();

        /// <summary>待结算"跨格进入"：物理帧登记、Tick 统一结算</summary>
        private readonly List<(Vector3Int Cell, IEffectTarget Target)> _pendingEnters = new();

        /// <summary>已告警的（格,效果号）：状态每帧重提交会刷爆 Console</summary>
        private readonly HashSet<(int X, int Y, int Z, int Kind)> _warnedEffects = new();

        private float _now;
        private float _deltaTime;

        /// <remarks>stateFactory 返回 null=未实现；element=null 不产生反应；registry=null 自建。数值不在此注入：tile_effect 档位已按 effectValuePos 定值。</remarks>
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

        /// <summary>登记合法格，并按原本的地灌一次 tile_state 元素</summary>
        /// <remarks>常规格元素恒为空，反应链判据不同；默认状态由 LoadInitialStates 给。</remarks>
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

        /// <summary>灌入关卡初始状态，不产生伤害</summary>
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

            // 进格效果先结算：换格在物理帧，须早于本节拍清单
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

        /// <summary>球落地：合成元素→查规则→切状态</summary>
        /// <remarks>返回 false：地板外、规则不给状态（None）或结果即当前状态。没切状态时元素改动要收回。</remarks>
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

                // 规则命中但不改地形（兜底行的击退）：效果照样落地。
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

        public void Transition(Vector3Int cell, TileStateType next)
        {
            _pending[cell] = next;
        }

        /// <summary>效果出口：格上目标结算；改格子的转交 ApplyToCell</summary>
        /// <remarks>玩家不在归属表里，泥浆不减速玩家；未实现种类必须 WarnUnsupported，零分配。</remarks>
        public void Apply(Vector3Int cell, in TileEffectValue effect)
        {
            switch (effect.Kind)
            {
                case TileEffectKind.InstantDamage:
                    Deal(cell, effect.Amount);
                    return;

                case TileEffectKind.DamageOverTime:
                    // 数值取状态那一档：数据层已按 effectValuePos 定值（燃烧第 2 档 = 1/秒）。
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

                // 这两条改格子自身，本该走 ApplyToCell；两处提交口都调 Apply，故在此转交。
                case TileEffectKind.InheritElement:
                case TileEffectKind.ClearPlants:
                    ApplyToCell(cell, in effect);
                    return;

                // 无效果是合法取值（表里没填），静默跳过。
                case TileEffectKind.None:
                    return;

                // Slide 未实现（缺滑行接口）；Skid/Block/Fixed 无枚举位，此处表号被硬塞。
                default:
                    WarnUnsupported(cell, effect.Kind);
                    return;
            }
        }

        /// <remarks>地形改写通道：只改 cell 自身（状态实现不许碰别的格）。本口在 SwitchState 的 OnEnter 里先跑，随后新状态初值会刷上格覆盖"继承小球属性"的结果。</remarks>
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

                    // 只清本格：表里范围是十字，跨格缺邻居命令通道。
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

                default:
                    WarnUnsupported(cell, effect.Kind);
                    return;
            }
        }

        public void SetCellElement(Vector3Int cell, in ElementValue element)
        {
            _element?.SetElement(cell, in element);
        }

        /// <remarks>未实现的块效果必须出声；按（格, 效果号）只报一次以免刷爆 Console，无分配。</remarks>
        private void WarnUnsupported(Vector3Int cell, TileEffectKind kind)
        {
            if (!_warnedEffects.Add((cell.x, cell.y, cell.z, (int)kind))) return;

            Debug.LogWarning($"[Grid] 收到未完全支持的地块效果: {kind}（位于格 {cell}），当前跳过执行。");
        }

        /// <remarks>续减速不做快照，离泥浆自然过期；没填时长=在格子上持续生效，窗口交给消费者（StatusGroup）按自己的拍扣；替换成一帧 Δt 会短于其物理帧被扣穿。</remarks>
        private void ApplySlow(Vector3Int cell, float speedScale, float seconds)
        {
            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is ISlowable target) target.ApplySlow(speedScale, seconds);
            }
        }

        /// <summary>目标跨格进入：只给该目标补该格"进格一下"</summary>
        /// <remarks>表现层换格时调；随最近一次 Tick 结算。</remarks>
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

        /// <summary>进格那一下：只给这一个目标补一次性效果</summary>
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

        /// <summary>按格数击退：冲量 = 格数 × 格边长 × 衰减率；总位移 ≈ 冲量/衰减率故乘回</summary>
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

        /// <summary>麻痹：时长与门禁由目标决定。</summary>
        private void ApplyStun(Vector3Int cell, float seconds)
        {
            if (seconds <= 0f) return;

            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is IStunnable target) target.ApplyStun(seconds);
            }
        }

        /// <summary>结算格上目标伤害（方向按格心→受害者）</summary>
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

        private static void DealOne(IEffectTarget target, Vector2 center, float amount)
        {
            if (amount <= 0f) return;

            if (target == null) return;

            if (!target.IsAlive) return;

            // 不可受伤者仍可被减速/击退，只是不吃伤害。
            if (target is not IDamageable damageable) return;

            damageable.TakeDamage(Damage.At(center, target.Position, amount, DamageSource.Tile));
        }

        /// <summary>切换某格状态，同状态 no-op；applyEnterImpact=是否给进格冲击（开局加载不给）</summary>
        /// <remarks>enterEffects=null 时用状态自带的清单。</remarks>
        public bool SwitchState(
            Vector3Int cell,
            TileStateType next,
            bool applyEnterImpact,
            IReadOnlyList<TileEffectValue> enterEffects = null)
        {
            if (!_cells.Contains(cell)) return false;

            if (next != TileStateType.Normal && !_specs.ContainsKey(next)) return false;

            if (!_machines.TryGetValue(cell, out TileStateMachine machine))
            {
                if (next == TileStateType.Normal) return false;

                machine = new TileStateMachine();

                RegisterStateFactories(machine);
            }

            bool changed = machine.SwitchTo(next, BuildContext(cell));

            if (!changed)
            {
                if (machine.Current == null) _machines.Remove(cell);

                return false;
            }

            if (machine.CurrentId == TileStateType.Normal) _machines.Remove(cell);
            else _machines[cell] = machine;

            if (_element != null && _specs.TryGetValue(next, out TileStateSpec spec))
            {
                _element.FlushStateElement(cell, in spec);
            }

            EventBus<TileStateChanged>.Publish(new TileStateChanged(cell, next));

            if (applyEnterImpact) ApplyEnterImpact(cell, next, enterEffects);

            return true;
        }

        /// <summary>提交进格冲击：规则清单与状态的"进格一下"都要落地</summary>
        /// <remarks>两者不是二选一：规则清单为空（多数 element_rule 行 effects 列空）时会吞掉状态的进格一下；非一次性效果由 Tick 拍负责。</remarks>
        private void ApplyEnterImpact(Vector3Int cell, TileStateType state, IReadOnlyList<TileEffectValue> enterEffects)
        {
            bool hasState = _specs.TryGetValue(state, out TileStateSpec spec);

            if (enterEffects == null || enterEffects.Count == 0)
            {
                if (!hasState) return;

                ApplyEffectList(cell, spec.EnterEffects, enterOnly: false);

                return;
            }

            ApplyEffectList(cell, enterEffects, enterOnly: false);

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

            // 先搬走再处理：处理中的新请求留到下一帧，连锁有上界。
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
