using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Logic.Grid
{

    // 纯函数：缓冲由调用方给，进来先被 Clear
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

        // 顺序下/左/右/上：泛洪次序决定效果先后，别随手重排
        public static void GetNeighbors4(Vector3Int cell, List<Vector3Int> buffer)
        {
            if (buffer == null) return;

            buffer.Clear();

            buffer.Add(new Vector3Int(cell.x, cell.y - 1, cell.z));
            buffer.Add(new Vector3Int(cell.x - 1, cell.y, cell.z));
            buffer.Add(new Vector3Int(cell.x + 1, cell.y, cell.z));
            buffer.Add(new Vector3Int(cell.x, cell.y + 1, cell.z));
        }
    }
}
