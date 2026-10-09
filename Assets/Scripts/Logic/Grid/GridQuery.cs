using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Logic.Grid
{
    /// <summary>格子空间查询：邻接，静态纯函数；缓冲由调用方给，先被 Clear</summary>
    public static class GridQuery
    {
        public static void GetNeighbors8(Vector3Int cell, List<Vector3Int> buffer)
        {
            if (buffer == null) return;

            buffer.Clear();

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    buffer.Add(new Vector3Int(cell.x + dx, cell.y + dy, cell.z));
                }
            }
        }
    }
}
