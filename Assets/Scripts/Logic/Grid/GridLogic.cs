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

        /// <summary>二级元素反应查询表；null 时不发生二级反应</summary>
        private readonly DuoReactionCatalog _duo;

        /// <summary>合法格；表现层登记，未登记不参与转换</summary>
        private readonly HashSet<Vector3Int> _cells = new();

        private readonly Dictionary<Vector3Int, TileStateMachine> _machines = new();

        private readonly TileTickQueue _queue = new();

        /// <summary>待生效转换，Tick 后结算，后者胜</summary>
        private readonly Dictionary<Vector3Int, TileStateType> _pending = new();
        private readonly List<KeyValuePair<Vector3Int, TileStateType>> _pendingScratch = new();

        /// <summary>目标快照：受害方可能在结算中死亡注销</summary>
        private readonly List<IEffectTarget> _dealScratch = new();

        /// <summary>径向冲击的格缓冲，复用避免热路径分配</summary>
        private readonly List<Vector3Int> _blastCells = new();

        private float _now;
        private float _deltaTime;

        /// <remarks>stateFactory 返回 null=未实现；duo=null 无二级反应；registry=null 自建。数值不在此注入：tile_state / element_rule 已是最终值。</remarks>
        public GridLogic(
            in GridGeometry geometry,
            IReadOnlyList<TileStateSpec> stateSpecs,
            Func<TileStateType, ITileState> stateFactory,
            DuoReactionCatalog duo = null,
            EnemyCellRegistry registry = null)
        {
            _geometry = geometry;
            _stateFactory = stateFactory;
            _duo = duo;
            _registry = registry ?? new EnemyCellRegistry();

            if (stateSpecs != null)
            {
                for (int i = 0; i < stateSpecs.Count; i++)
                {
                    _specs[stateSpecs[i].Id] = stateSpecs[i];
                }
            }
        }

        /// <summary>登记合法格；initial 参数当前不读，地貌由 SwitchTileState 改</summary>
        public void RegisterCell(Vector3Int cell, TileStateType initial = TileStateType.Normal)
        {
            _cells.Add(cell);
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

        /// <summary>该格是否作为网格连锁的导通体；未登记或表里没有该行都返回 false</summary>
        public bool IsConductor(Vector3Int cell)
        {
            TileStateType state = StateOf(cell);

            return _specs.TryGetValue(state, out TileStateSpec spec) && spec.IsConductor;
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

                if (state.StateId == TileStateType.Normal) continue;

                if (SwitchTileState(cell, state.StateId, 0f)) applied++;
            }

            return applied;
        }

        public void Tick(float now, float deltaTime)
        {
            _now = now;
            _deltaTime = deltaTime;

            // 火海蔓延前沿先推：它的时间基准与本节拍一致
            TileChainReactor.Tick(this, now, deltaTime);

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

        /// <summary>球落地：纯函数裁决 → 原子提交地貌 → 当帧结算冲击 → 按需触发连锁</summary>
        /// <remarks>返回 false：地板外、或地貌与冲击都没发生的空过。首跳伤害在此当帧打出，绝不等待下一次 Tick。</remarks>
        public bool OnBallHit(Vector3Int cell, BallType ball)
        {
            if (!_cells.Contains(cell)) return false;

            TileStateType current = StateOf(cell);

            ReactionOutcome outcome = ReactionResolver.Resolve(current, ball);

            bool changed = outcome.NextTile != current
                && SwitchTileState(cell, outcome.NextTile, 0f);

            bool impacted = ApplyImpact(cell, in outcome);

            if (changed || impacted)
            {
                ScheduleTick(cell);

                // 二级元素反应只在"地貌真的变了"之后判：否则每次投球都要扫一遍四邻
                if (changed) TileChainReactor.TriggerDuo(this, cell, _duo);

                return true;
            }

            return false;
        }

        /// <summary>落地冲击的唯一出口：伤害 → 击退 → 麻痹 → 连锁，顺序固定</summary>
        public bool ApplyImpact(Vector3Int cell, in ReactionOutcome outcome)
        {
            bool any = false;

            if (outcome.InstantDamage > 0)
            {
                DealCell(cell, outcome.InstantDamage);
                any = true;
            }

            if (outcome.KnockbackCells > 0f)
            {
                ApplyKnockback(cell, outcome.KnockbackCells);
                any = true;
            }

            if (outcome.StunSeconds > 0f)
            {
                ApplyStunCell(cell, outcome.StunSeconds);
                any = true;
            }

            if (outcome.TriggerChain)
            {
                ScheduleTick(cell);

                TileChainReactor.Trigger(this, cell, outcome.NextTile);

                any = true;
            }

            return any;
        }

        public void ScheduleTick(Vector3Int cell)
        {
            _queue.Schedule(cell);
        }

        public void Transition(Vector3Int cell, TileStateType next)
        {
            _pending[cell] = next;
        }

        /// <summary>对格上目标造成一次伤害</summary>
        public void DealCell(Vector3Int cell, float amount)
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

        /// <summary>对格上目标续一次减速；状态实现按帧调它，seconds 就是本帧窗口</summary>
        /// 续减速不做快照，离开泥浆自然过期；替换成一次性大窗口会短于物理帧被扣穿
        public void ApplySlowCell(Vector3Int cell, float speedScale, float seconds)
        {
            if (speedScale >= 1f) return;

            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is ISlowable target) target.ApplySlow(speedScale, seconds);
            }
        }

        /// <summary>对格上目标施加麻痹</summary>
        public void ApplyStunCell(Vector3Int cell, float seconds)
        {
            if (seconds <= 0f) return;

            if (!_registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] is IStunnable target) target.ApplyStun(seconds);
            }
        }

        /// <summary>连锁电击：伤害 + 麻痹一次结算，供 TileChainReactor 逐格调用</summary>
        public void ApplyInstantShock(Vector3Int cell, int damage, float stunSeconds)
        {
            DealCell(cell, damage);

            ApplyStunCell(cell, stunSeconds);
        }

        /// <summary>径向范围冲击：对半径内每格的伙伴结算一次伤害与击退，用于二级反应的波及</summary>
        /// <remarks>半径按格心欧氏距离判，含中心格；一次反应内同一格只结算一次。</remarks>
        public void ApplyBlast(Vector3Int center, float radiusCells, int damage, float knockbackCells)
        {
            if (radiusCells < 0f) radiusCells = 0f;

            int reach = Mathf.CeilToInt(radiusCells);
            float radiusSqr = radiusCells * radiusCells;

            Vector2 centerPoint = _geometry.CellCenter(center);

            _blastCells.Clear();

            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    var cell = new Vector3Int(center.x + dx, center.y + dy, center.z);

                    if (!_cells.Contains(cell)) continue;

                    if (dx != 0 || dy != 0)
                    {
                        Vector2 delta = _geometry.CellCenter(cell) - centerPoint;

                        if (delta.sqrMagnitude > radiusSqr) continue;
                    }

                    _blastCells.Add(cell);
                }
            }

            for (int i = 0; i < _blastCells.Count; i++)
            {
                Vector3Int cell = _blastCells[i];

                if (damage > 0) DealCell(cell, damage);

                if (knockbackCells > 0f) ApplyKnockback(cell, knockbackCells);
            }
        }

        /// <summary>切换某格状态；同状态 no-op，未登记的格与无表行的状态一律拒绝</summary>
        /// <remarks>durationOverride &gt; 0 时覆盖表里的 duration（二级反应的结果存续用它）；不改贴图以外任何东西，贴图由 EventBus 订阅者刷。</remarks>
        public bool SwitchTileState(Vector3Int cell, TileStateType next, float durationOverride = 0f)
        {
            if (!_cells.Contains(cell)) return false;

            if (next != TileStateType.Normal && !_specs.ContainsKey(next)) return false;

            if (!_machines.TryGetValue(cell, out TileStateMachine machine))
            {
                if (next == TileStateType.Normal) return false;

                machine = new TileStateMachine();

                RegisterStateFactories(machine);
            }

            bool changed = machine.SwitchTo(next, BuildContext(cell), durationOverride);

            if (!changed)
            {
                if (machine.Current == null) _machines.Remove(cell);

                return false;
            }

            if (machine.CurrentId == TileStateType.Normal) _machines.Remove(cell);
            else _machines[cell] = machine;

            EventBus<TileStateChanged>.Publish(new TileStateChanged(cell, next));

            return true;
        }

        /// <summary>取某状态的包装件，诊断与测试用</summary>
        public bool TryGetSpec(TileStateType state, out TileStateSpec spec)
        {
            return _specs.TryGetValue(state, out spec);
        }

        /// <summary>目标跨格进入：只给该目标补该格"进格一下"</summary>
        /// <remarks>进入地格的一次性代价按"伤害 + 击退"两类算，由状态的 DoT 与减速承担持续部分。</remarks>
        public void OnActorEnterCell(Vector3Int cell, IEffectTarget target)
        {
            if (target == null || target.IsAlive == false) return;

            if (!_cells.Contains(cell)) return;

            if (!_specs.TryGetValue(StateOf(cell), out TileStateSpec spec)) return;

            if (spec.DotDamage <= 0) return;

            // DoT 的首跳放在这：走进去那一下要立刻痛，不能等满一秒
            DealOne(target, _geometry.CellCenter(cell), spec.DotDamage);
        }

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

        private static void DealOne(IEffectTarget target, Vector2 center, float amount)
        {
            if (amount <= 0f) return;

            if (target == null) return;

            if (!target.IsAlive) return;

            // 不可受伤者仍可被减速/击退，只是不吃伤害。
            if (target is not IDamageable damageable) return;

            damageable.TakeDamage(Damage.At(center, target.Position, amount, DamageSource.Tile));
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

                SwitchTileState(pair.Key, pair.Value);
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
