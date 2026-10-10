using UnityEngine;

namespace DeepseaOil.Data
{
    // 只装数值，行为归掉落物实体
    public sealed class DropSpec
    {
        private readonly DropTuning _tuning;
        private readonly DropType _type;

        public DropSpec(DropTuning tuning, DropType type)
        {
            _tuning = tuning;
            _type = type;
        }

        public DropType Type => _type;

        // 调色板来源 ConfigModule.Visuals
        public Color Color => ConfigModule.Visuals.DropColor(_type);

        // 抛出到落点时长，秒
        public float FlightDuration => _tuning.flightDuration;

        // 弧高，世界单位
        public float ArcHeight => _tuning.arcHeight;

        // 落点→玩家速度，世界单位/秒
        public float HomingSpeed => _tuning.homingSpeed;

        public float ReachDistance => _tuning.reachDistance;

        public int Amount => _tuning.amount;

        // 视觉直径，世界单位
        public float BodyDiameter => _tuning.bodyDiameter;

        public float TriggerRadius => _tuning.triggerRadius;
    }
}
