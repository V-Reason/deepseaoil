using cfg.dso;

namespace DeepseaOil.Data
{
    // <summary>玩家取值边界</summary>
    // <remarks>表行管血量与受</remarks>
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

        // <summary>敌人贴上圆心距</summary>
        public float ContactRadius => _row.ContactRadius;

        // <summary>水弹药上限</summary>
        public int WaterCapacity => _row.WaterCapacity;

        // <summary>开局水弹药</summary>
        public int WaterStart => _row.WaterStart;

        // <summary>土弹药上限</summary>
        public int EarthCapacity => _row.EarthCapacity;

        // <summary>开局土弹药</summary>
        public int EarthStart => _row.EarthStart;

        // <summary>生命神泉回 1</summary>
        public float LifeHealInterval => _row.LifeHealInterval;

        // <remarks>移动与冲刺参数</remarks>
        public PlayerConfig Config => _config;

        public ProjectileSpec Ball { get; }

        public bool SnapToEightDirections => _config.snapToEightDirections;

        // <summary>输入缓冲容量</summary>
        public float InputBufferSeconds => _config.inputBufferTime;

        public float DashCooldownSeconds => _config.dashCooldown;

        public float DashBufferSeconds => _config.dashBufferTime;

        // <summary>相机深度</summary>
        public float CameraPlaneDepth
            => Ball != null && Ball.Tuning != null ? Ball.Tuning.cameraPlaneDepth : 100f;

        // <summary>投掷射程上限</summary>
        public float MaxThrowDistance => Ball != null ? Ball.MaxThrowDistance : 0f;
    }
}
