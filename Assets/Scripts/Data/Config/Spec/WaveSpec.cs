using cfg.dso;

namespace DeepseaOil.Data
{

    public sealed class WaveSpec
    {
        private readonly Wave _row;

        public WaveSpec(Wave row)
        {
            _row = row;
        }

        public int Id => _row.Id;

        public string Name => _row.Name;

        public float PrepTime => _row.PrepTime;

        public float BattleTime => _row.BattleTime;

        public float SettleTime => _row.SettleTime;

        public int EnemiesPerWave => _row.EnemiesPerWave;

        public float SpawnInterval => _row.SpawnInterval;

        public float SpawnRadius => _row.SpawnRadius;

        public SeedType GrantSeed => _row.GrantSeed;
    }
}
