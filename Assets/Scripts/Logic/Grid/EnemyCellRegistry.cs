using System.Collections.Generic;
using UnityEngine;
using DeepseaOil.Logic.Combat;

namespace DeepseaOil.Logic.Grid
{
    /// <summary>敌人 / 格上目标 → 格子的归属映射。单点判定（脚底中心），由目标自己每帧上报。</summary>
    /// <remarks>一格可以站多个目标，所以值是列表。注销是强制的：表里存引用，目标被销毁而没注销就会留下一个查得到但已不能用的条目；结算前会先看 IAlivable.IsAlive，所以漏注销的表现是"格子上一具看不见的尸体"而不是 MissingReferenceException。</remarks>
    public sealed class EnemyCellRegistry
    {
        private readonly Dictionary<Vector3Int, List<IEffectTarget>> _byCell = new();
        private readonly Dictionary<IEffectTarget, Vector3Int> _cellOf = new();

        /// <summary>登记一个目标到某格，已在别处登记时先摘掉旧登记</summary>
        public void Register(Vector3Int cell, IEffectTarget target)
        {
            if (target == null) return;

            if (_cellOf.TryGetValue(target, out Vector3Int previous))
            {
                if (previous == cell) return;   // 幂等：同一格重复登记不产生第二份

                Detach(target, previous);
            }

            if (!_byCell.TryGetValue(cell, out List<IEffectTarget> list))
            {
                list = new List<IEffectTarget>(2);
                _byCell[cell] = list;
            }

            list.Add(target);
            _cellOf[target] = cell;
        }

        /// <summary>把目标挪到新格，没登记过则等价于登记</summary>
        public void Move(IEffectTarget target, Vector3Int cell)
        {
            if (target == null) return;

            if (_cellOf.TryGetValue(target, out Vector3Int previous) && previous == cell) return;

            Register(cell, target);
        }

        /// <summary>摘掉一个目标的登记，没登记过是 no-op</summary>
        public void Unregister(IEffectTarget target)
        {
            if (target == null) return;

            if (!_cellOf.TryGetValue(target, out Vector3Int cell)) return;

            Detach(target, cell);
        }

        /// <summary>取该格目标列表，遍历它时不要调 Register / Unregister</summary>
        public bool TryGetIn(Vector3Int cell, out List<IEffectTarget> targets)
        {
            return _byCell.TryGetValue(cell, out targets);
        }

        private void Detach(IEffectTarget target, Vector3Int cell)
        {
            _cellOf.Remove(target);

            if (!_byCell.TryGetValue(cell, out List<IEffectTarget> list)) return;

            list.Remove(target);

            // 空列表要删掉：留着会让"场上还有几个格被占用"的读数偏大。
            if (list.Count == 0) _byCell.Remove(cell);
        }
    }
}
