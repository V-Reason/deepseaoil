using cfg.dso;

namespace DeepseaOil.Data
{
    // 玩家取值边界：合并 player 表行与 PlayerConfig SO
    // 表行管血量与受击，SO 管移动与冲刺，装配期折算一次
    public sealed class PlayerSpec
    {
        private readonly Player _row;
        private readonly PlayerConfig _config;

        public PlayerSpec(Player row, PlayerConfig config, ProjectileSpec ball)
        {
            _row = row;
            _config = config;
            Ball = ball;
        }

        public int Id => _row.Id;

        public string Name => _row.Name;

        public float MaxHp => _row.MaxHp;

        public float ContactDamage => _row.ContactDamage;

        public float InvulnerableDuration => _row.InvulnerableDuration;

        public float RetryDelay => _row.RetryDelay;

        public float AttackInterval => _row.AttackInterval;

        public float KnockbackImpulse => _row.KnockbackImpulse;

        public float KnockbackSpeedLimit => _row.KnockbackSpeedLimit;

        // 敌人贴上来的圆心距，世界单位，须略小于两半径之和
        public float ContactRadius => _row.ContactRadius;

        // 水弹药上限
        public int WaterCapacity => _row.WaterCapacity;

        // 开局水弹药
        public int WaterStart => _row.WaterStart;

        // 土弹药上限
        public int EarthCapacity => _row.EarthCapacity;

        // 开局土弹药
        public int EarthStart => _row.EarthStart;

        // 生命神泉回 1 点的静止间隔，秒
        public float LifeHealInterval => _row.LifeHealInterval;

        // 移动与冲刺参数：装配期折算成快照，运行期改 SO 不生效
        public PlayerConfig Config => _config;

        public ProjectileSpec Ball { get; }

        public bool SnapToEightDirections => _config.snapToEightDirections;

        // 输入缓冲容量，秒，须 ≥ 各输入窗口
        public float InputBufferSeconds => _config.inputBufferTime;

        public float DashCooldownSeconds => _config.dashCooldown;

        public float DashBufferSeconds => _config.dashBufferTime;

        // 相机深度，世界单位；取水球行 ThrowTuning，缺失 = 100
        public float CameraPlaneDepth
            => Ball != null && Ball.Tuning != null ? Ball.Tuning.cameraPlaneDepth : 100f;

        // 投掷射程上限，世界单位；取水球行，缺失 = 0 即不限
        public float MaxThrowDistance => Ball != null ? Ball.MaxThrowDistance : 0f;
    }
}
