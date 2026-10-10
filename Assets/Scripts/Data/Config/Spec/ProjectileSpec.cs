using cfg.dso;
using UnityEngine;

namespace DeepseaOil.Data
{
    // projectile 表行 + ThrowTuning SO；
    public sealed class ProjectileSpec
    {
        private const float FallbackFlightDuration = 0.6f;

        private const float FallbackMaxHeight = 2f;

        private const float FallbackMaxThrowDistance = 5f;

        private const float FallbackMinThrowDistance = 0.4f;

        private const float FallbackBallRadius = 0.22f;

        private readonly Projectile _row;
        private readonly ThrowTuning _tuning;

        public ProjectileSpec(Projectile row, ThrowTuning tuning)
        {
            _row = row;
            _tuning = tuning;
        }

        public BallType Type => _row.Id;

        public string Name => _row.Name;

        // 抛物线/距离列退役：ThrowTuning SO 承接

        /// ThrowTuning.flightDuration，秒
        public float FlightDuration => _tuning != null ? Positive(_tuning.flightDuration, FallbackFlightDuration) : FallbackFlightDuration;

        /// ThrowTuning.maxHeight，世界单位
        public float MaxHeight => _tuning != null ? NonNegative(_tuning.maxHeight, FallbackMaxHeight) : FallbackMaxHeight;

        /// ThrowTuning.maxThrowDistance，世界单位；下落时长的距离上限
        public float MaxThrowDistance => _tuning != null ? Positive(_tuning.maxThrowDistance, FallbackMaxThrowDistance) : FallbackMaxThrowDistance;

        // ThrowTuning.minThrowDistance；
        public float MinThrowDistance
        {
            get
            {
                float min = _tuning != null ? Positive(_tuning.minThrowDistance, FallbackMinThrowDistance) : FallbackMinThrowDistance;
                float max = MaxThrowDistance;

                return min < max ? min : max * 0.5f;
            }
        }

        // <summary>这颗球消耗哪种弹药；</summary>
        // <remarks>落地反应只看 Type（BallType）</remarks>
        public bool TryGetResource(out ResourceKind kind)
        {
            switch (Type)
            {
                case BallType.Water:
                    kind = ResourceKind.Water;
                    return true;

                case BallType.Earth:
                    kind = ResourceKind.Earth;
                    return true;

                default:
                    kind = ResourceKind.Water;
                    return false;
            }
        }

        public ThrowTuning Tuning => _tuning;

        /// 取 ConfigModule.Visuals
        public Color BallColor => ConfigModule.Visuals.BallColor(Type);

        public Color ShadowColor => ConfigModule.Visuals.shadow;

        public float BallRadius => _tuning != null ? _tuning.ballRadiusMeters : FallbackBallRadius;

        private static float Positive(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;
        }

        private static float NonNegative(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? fallback : value;
        }
    }
}
