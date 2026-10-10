using System.Collections.Generic;
using UnityEngine;
using DeepseaOil.Logic.Grid;

namespace DeepseaOil.Logic.Combat
{
    // 接触检测纯函数：取贴在玩家身上最近的敌人
    // 判定半径取表值 contact_radius；已死目标不算接触（同 GridLogic.Deal）
    public static class ContactProbe
    {
        // cellBuffer 复用缓冲；contactRadius 世界单位，非法值按 0
        public static bool TryFindAttacker(
            Vector3Int playerCell,
            Vector2 playerPosition,
            float contactRadius,
            EnemyCellRegistry registry,
            List<Vector3Int> cellBuffer,
            out Vector2 attacker,
            out float distance,
            out int contactDamage,
            out IEffectTarget attackerTarget)
        {
            attacker = default;
            distance = float.PositiveInfinity;
            contactDamage = 0;
            attackerTarget = null;

            if (registry == null || cellBuffer == null) return false;

            float radius = float.IsNaN(contactRadius) || contactRadius < 0f ? 0f : contactRadius;
            float radiusSqr = radius * radius;

            float bestSqr = float.MaxValue;

            // 邻居查询不含自己，中心格单独看
            bool found = ScanCell(playerCell, playerPosition, radiusSqr, registry, ref bestSqr, ref attacker, ref attackerTarget);

            GridQuery.GetNeighbors8(playerCell, cellBuffer);

            for (int i = 0; i < cellBuffer.Count; i++)
            {
                if (ScanCell(cellBuffer[i], playerPosition, radiusSqr, registry, ref bestSqr, ref attacker, ref attackerTarget)) found = true;
            }

            if (!found) return false;

            distance = Mathf.Sqrt(bestSqr);

            contactDamage = ContactDamageOf(attackerTarget);

            return true;
        }

        // 伤害只能从 IContactDamager 取，敌人不暴露整个 Actor
        private static int ContactDamageOf(IEffectTarget target)
        {
            return target is IContactDamager damager ? damager.ContactDamage : 0;
        }

        private static bool ScanCell(
            Vector3Int cell,
            Vector2 playerPosition,
            float radiusSqr,
            EnemyCellRegistry registry,
            ref float bestSqr,
            ref Vector2 attacker,
            ref IEffectTarget bestTarget)
        {
            if (!registry.TryGetIn(cell, out List<IEffectTarget> targets) || targets == null) return false;

            bool found = false;

            for (int i = 0; i < targets.Count; i++)
            {
                IEffectTarget target = targets[i];

                // 已销毁对象在接口引用上不是 null，读它抛 MissingReferenceException
                if (target is UnityEngine.Object unityObject && unityObject == null) continue;

                if (target == null) continue;
                if (!target.IsAlive) continue;

                Vector2 delta = playerPosition - target.Position;
                float sqr = delta.sqrMagnitude;

                if (sqr > radiusSqr) continue;

                if (sqr >= bestSqr) continue;

                bestSqr = sqr;
                attacker = target.Position;
                bestTarget = target;
                found = true;
            }

            return found;
        }
    }

    // 贴身伤害的来源：由敌人自己提供
    public interface IContactDamager
    {
        int ContactDamage { get; }
    }
}
