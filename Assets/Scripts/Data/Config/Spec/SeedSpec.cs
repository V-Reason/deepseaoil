using cfg.dso;

namespace DeepseaOil.Data
{
    // <summary>种子基建配置：</summary>
    // <remarks>icon_</remarks>
    public sealed class SeedSpec
    {
        private readonly Seed _row;

        public SeedSpec(Seed row)
        {
            _row = row;
        }

        public SeedType Id => _row.Id;

        public string Name => _row.Name;

        // <summary>播种后生成的设</summary>
        public TileStateType SpawnTile => _row.SpawnTile;

        public string IconKey => _row.IconKey;
    }
}
