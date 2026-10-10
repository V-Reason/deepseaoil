using UnityEngine;

namespace DeepseaOil.Logic.World
{
    // <summary>九宫格判定：以某点为心的 3×3 格范围</summary>
    // <remarks>纯函数。用格坐标差判而不是世界距离：</remarks>
    public static class NineGrid
    {
        public static bool Contains(Vector3Int center, Vector3Int cell)
        {
            return Mathf.Abs(cell.x - center.x) <= 1 && Mathf.Abs(cell.y - center.y) <= 1;
        }

        public static bool Contains(Vector3Int center, Vector2 world, in Grid.GridGeometry geometry)
        {
            if (!geometry.IsValid) return false;

            return Contains(center, geometry.WorldToCell(world));
        }
    }
}
