using System.Collections.Generic;
using DeepseaOil.Data;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Logic.Grid
{
    // <summary>网格连锁引擎：受控 BFS 泛洪，严禁递归</summary>
    // <remarks>纯 C#：时间与 Δt 由 GridLogic 注入，不读 Time</remarks>
    public static class TileChainReactor
    {
        // <summary>单次连锁最多波及的格数：超限即截断并只告警一次</summary>
        public const int MaxChainSteps = 32;

        /// <summary>燎原火海每步蔓延的间隔（秒）</summary>
        public const float FlameSpreadStep = 0.2f;

        /// <summary>连锁电击的伤害与麻痹，全网同帧一致</summary>
        private const int ShockDamage = 1;

        private const float ShockStunSeconds = 1.5f;

        /// <summary>火海每格的灼烧伤害</summary>
        private const int FlameDamage = 2;

        /// <summary>二级反应的波及半径下限，格；表里填得更大时以表为准</summary>
        private const float MinBlastRadius = 1.5f;

        private static readonly Queue<Vector3Int> _queue = new Queue<Vector3Int>(64);

        private static readonly HashSet<Vector3Int> _visited = new HashSet<Vector3Int>();

        private static readonly List<Vector3Int> _neighbors = new List<Vector3Int>(8);

        private static readonly List<Vector3Int> _dueFront = new List<Vector3Int>(16);

        // <summary>火海蔓延前沿：(格, 下次蔓延时刻)；</summary>
        private static readonly List<(Vector3Int Cell, float At)> _flameFront = new List<(Vector3Int, float)>(16);

        // <summary>本轮连锁是否已报过超限告警（只报一次）</summary>
        private static bool _warnedOverflow;

        private static float _now;

        /// <summary>清空全部跨帧状态，随格子复位</summary>
        public static void Clear()
        {
            _queue.Clear();
            _visited.Clear();
            _flameFront.Clear();
            _dueFront.Clear();
            _now = 0f;
            _warnedOverflow = false;
        }

        // <summary>推进火海蔓延前沿；</summary>
        public static void Tick(GridLogic grid, float now, float deltaTime)
        {
            _now = now;

            if (_flameFront.Count == 0 || grid == null) return;

            _dueFront.Clear();

            for (int i = 0; i < _flameFront.Count; i++)
            {
                if (_flameFront[i].At <= now) _dueFront.Add(_flameFront[i].Cell);
            }

            for (int i = 0; i < _dueFront.Count; i++)
            {
                Vector3Int cell = _dueFront[i];

                RemoveFront(cell);

                SpreadFlameStep(grid, cell);
            }
        }

        /// <summary>一次落地的连锁入口</summary>
        public static void Trigger(GridLogic grid, Vector3Int origin, TileStateType result)
        {
            if (grid == null || !grid.HasCell(origin)) return;

            if (result == TileStateType.FlameField || IsFlammable(result))
            {
                StartFlame(grid, origin, result);
                return;
            }

            Propagate(grid, origin);
        }

        // <summary>二级元素反应入口：两个发生器地貌被导通体连通时触发</summary>
        // <remarks>发起格必须真的属于元素发生器地貌，否则不触发</remarks>
        public static void TriggerDuo(GridLogic grid, Vector3Int origin, DuoReactionCatalog catalog)
        {
            if (grid == null || catalog == null || !grid.HasCell(origin)) return;

            TileStateType self = grid.StateOf(origin);

            GridQuery.GetNeighbors4(origin, _neighbors);

            for (int i = 0; i < _neighbors.Count; i++)
            {
                Vector3Int other = _neighbors[i];

                if (!grid.HasCell(other)) continue;

                if (!catalog.TryGet(self, grid.StateOf(other), out DuoReactionSpec duo)) continue;

                CommitDuo(grid, origin, duo);

                return;
            }
        }

        // <summary>连锁导电：沿 is_conductor 网格泛洪，全网同帧结算</summary>
        private static void Propagate(GridLogic grid, Vector3Int origin)
        {
            // 起点自己也必须是导体：否则「水砸火池生成蒸汽」会凭空电一下受击格
            if (!grid.IsConductor(origin)) return;

            _queue.Clear();
            _visited.Clear();

            _queue.Enqueue(origin);
            _visited.Add(origin);

            while (_queue.Count > 0)
            {
                if (_visited.Count > MaxChainSteps)
                {
                    WarnOverflowOnce(origin);
                    break;
                }

                Vector3Int current = _queue.Dequeue();

                grid.ApplyInstantShock(current, ShockDamage, ShockStunSeconds);

                GridQuery.GetNeighbors4(current, _neighbors);

                for (int i = 0; i < _neighbors.Count; i++)
                {
                    Vector3Int next = _neighbors[i];

                    if (_visited.Contains(next)) continue;

                    if (!grid.HasCell(next)) continue;

                    // 只有"导通体"能把电传下去：水、泥浆
                    if (!grid.IsConductor(next)) continue;

                    _visited.Add(next);

                    _queue.Enqueue(next);
                }
            }
        }

        // <summary>燎原爆燃：点燃本格并挂上前沿</summary>
        private static void StartFlame(GridLogic grid, Vector3Int origin, TileStateType state)
        {
            if (IsFlammable(state)) grid.SwitchTileState(origin, TileStateType.FlameField);

            AddFront(grid, origin);
        }

        // <summary>一个蔓延步：灼烧本格，并把相邻可燃格烧成火海</summary>
        private static void SpreadFlameStep(GridLogic grid, Vector3Int cell)
        {
            if (!grid.HasCell(cell)) return;

            grid.ApplyInstantShock(cell, FlameDamage, 0f);

            GridQuery.GetNeighbors4(cell, _neighbors);

            for (int i = 0; i < _neighbors.Count; i++)
            {
                Vector3Int next = _neighbors[i];

                if (!grid.HasCell(next)) continue;

                if (!IsFlammable(grid.StateOf(next))) continue;

                // 烧穿：荆棘/植物区变成火海，火海到期后由状态自己回 Normal
                grid.SwitchTileState(next, TileStateType.FlameField);

                AddFront(grid, next);
            }
        }

        private static void AddFront(GridLogic grid, Vector3Int cell)
        {
            grid.ScheduleTick(cell);

            for (int i = 0; i < _flameFront.Count; i++)
            {
                if (_flameFront[i].Cell == cell) return;
            }

            _flameFront.Add((cell, _now + FlameSpreadStep));
        }

        private static void RemoveFront(Vector3Int cell)
        {
            for (int i = _flameFront.Count - 1; i >= 0; i--)
            {
                if (_flameFront[i].Cell != cell) continue;

                _flameFront.RemoveAt(i);
            }
        }

        // <summary>提交一次二级反应：发起格改地貌 + 广域冲击</summary>
        // 产物必定落在发起格：TryGet 命中即证明发起格是这一对发生器之一，没有"中间那格"的可能
        private static void CommitDuo(GridLogic grid, Vector3Int origin, DuoReactionSpec duo)
        {
            if (grid.StateOf(origin) != duo.ResultTile)
            {
                grid.SwitchTileState(origin, duo.ResultTile, duo.ResultDuration);
            }

            float radius = duo.EffectRadius > MinBlastRadius ? duo.EffectRadius : MinBlastRadius;

            grid.ApplyBlast(origin, radius, duo.ImpactDamage, duo.ImpactKnockback);

            if (!duo.TriggerChain) return;

            TileStateType result = grid.StateOf(origin);

            if (result == TileStateType.FlameField || IsFlammable(result))
            {
                StartFlame(grid, origin, result);
                return;
            }

            Propagate(grid, origin);
        }

        /// <summary>可燃面：能被火海点燃并烧穿的地貌</summary>
        public static bool IsFlammable(TileStateType state)
        {
            return state == TileStateType.BasicPlant || state == TileStateType.ChargedThorn;
        }

        private static void WarnOverflowOnce(Vector3Int origin)
        {
            if (_warnedOverflow) return;

            _warnedOverflow = true;

            Debug.LogWarning(
                $"[Chain] 连锁从 {origin} 起扩散超过 {MaxChainSteps} 格，已截断。" +
                "若这是有意的大网，请上调 TileChainReactor.MaxChainSteps。");
        }
    }
}
