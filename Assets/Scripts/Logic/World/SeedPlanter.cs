using DeepseaOil.Logic.Grid;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Logic.World
{
    // <summary>播</summary>
    public enum PlantRejection
    {
        // <summary>可</summary>
        None = 0,

        // <summary>该</summary>
        OutOfFloor = 1,

        // <summary>该</summary>
        NotEmpty = 2,

        // <summary>种</summary>
        NoSeed = 3,
    }

    // <summary>播</summary>
    // <remarks>纯</remarks>
    public static class SeedPlanter
    {
        public static PlantRejection Reject(
            GridLogic grid,
            Vector3Int cell,
            SeedType holding)
        {
            if (holding == SeedType.None) return PlantRejection.NoSeed;

            if (grid == null || !grid.HasCell(cell)) return PlantRejection.OutOfFloor;

            return grid.StateOf(cell) == TileStateType.Normal
                ? PlantRejection.None
                : PlantRejection.NotEmpty;
        }

        public static bool CanPlant(GridLogic grid, Vector3Int cell, SeedType holding)
        {
            return Reject(grid, cell, holding) == PlantRejection.None;
        }
    }
}
