using System.Collections.Generic;
using UnityEngine;
using DeepseaOil.Logic.Grid;

namespace DeepseaOil.Logic.Combat
{
    // 接触检测纯函数：取贴在玩家身上的最近者；伤害值由调用方取自 PlayerSpec.ContactDamage。
    // 按九宫格查，3×3 覆盖半径1（格边长1）；判定半径取表值 contact_radius，比例变了要同步改。
    // 已死目标不算接触（同 GridLogic.Deal）。
    public static class ContactProbe
    {
        // cellBuffer 复用缓冲（先清空）；contactRadius 世界单位，非法值按 0。
        public static bool TryFindAttacker(
            Vector3Int playerCell,
            Vector2 playerPosition,
            float contactRadius,
            EnemyCellRegistry registry,
            List<Vector3Int> cellBuffer,
            out Vector2 attacker,
            out float distance)
        {
            attacker = default;
            distance = float.PositiveInfinity;

            if (registry == null || cellBuffer == null) return false;

            float radius = float.IsNaN(contactRadius) || contactRadius < 0f ? 0f : contactRadius;
            float radiusSqr = radius * radius;

            bool found = false;
            float bestSqr = float.MaxValue;

            // 邻居查询不含自己，中心格单独看
            found = ScanCell(playerCell, playerPosition, radiusSqr, registry, ref bestSqr, ref attacker);

            GridQuery.GetNeighbors8(playerCell, cellBuffer);

            for (int i = 0; i < cellBuffer.Count; i++)
            {
                if (ScanCell(cellBuffer[i], playerPosition, radiusSqr, registry, ref bestSqr, ref attacker)) found = true;
            }

            if (!found) return false;

            distance = Mathf.Sqrt(bestSqr);

            return true;
        }

        private static bool ScanCell(
            Vector3Int cell,
            Vector2 playerPosition,
            float radiusSqr,
            EnemyCellRegistry registry,
            ref float bestSqr,
            ref Vector2 attacker)
        {
            if (!registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets == null) return false;

            bool found = false;

            for (int i = 0; i < targets.Count; i++)
            {
                IEffectTarget target = targets[i];

                // 已销毁的 Unity 对象在接口引用上不是 null，直接读会抛 MissingReferenceException。
                if (target is UnityEngine.Object unityObject && unityObject == null) continue;

                if (target == null) continue;
                if (!target.IsAlive) continue;

                Vector2 delta = playerPosition - target.Position;
                float sqr = delta.sqrMagnitude;

                if (sqr > radiusSqr) continue;

                if (sqr >= bestSqr) continue;

                bestSqr = sqr;
                attacker = target.Position;
                found = true;
            }

            return found;
        }
    }
}
