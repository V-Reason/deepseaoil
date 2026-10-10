using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Logic.Grid
{

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
