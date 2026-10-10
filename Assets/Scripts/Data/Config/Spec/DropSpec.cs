using UnityEngine;

namespace DeepseaOil.Data
{
    // <remarks>只装数值</remarks>
    public sealed class DropSpec
    {
        private readonly DropTuning _tuning;
        private readonly DropType _type;

        public DropSpec(DropTuning tuning, DropType type)
        {
            _tuning = tuning;
            _type = type;
        }

        // <summary>该掉落物的种类；</summary>
        public DropType Type => _type;

        // <remarks>调色板来源 Confi</remarks>
        public Color Color => ConfigModule.Visuals.DropColor(_type);

        // <summary>抛出到落点时长</summary>
        public float FlightDuration => _tuning.flightDuration;

        // <summary>弧高</summary>
        public float ArcHeight => _tuning.arcHeight;

        // <summary>落点→玩家速度</summary>
        public float HomingSpeed => _tuning.homingSpeed;

        public float ReachDistance => _tuning.reachDistance;

        public int Amount => _tuning.amount;

        // <summary>视觉直径</summary>
        public float BodyDiameter => _tuning.bodyDiameter;

        public float TriggerRadius => _tuning.triggerRadius;
    }
}
