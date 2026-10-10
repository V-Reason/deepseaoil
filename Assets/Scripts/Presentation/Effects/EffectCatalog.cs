using System.Collections.Generic;

namespace DeepseaOil.Presentation.Effects
{
    internal enum EffectDriverKind
    {
        Particle = 0,

        Shatter = 1,

        Highlight = 2,
    }

    internal readonly struct EffectSpec
    {
        public readonly EffectId Id;

        /// <summary>资源 Key，默认 effects/+枚举名</summary>
        public readonly string Key;

        public readonly EffectDriverKind DriverKind;

        public readonly bool IsSingleton;

        /// <summary>池上限，超了丢弃并警告；≤0 按 1</summary>
        public readonly int MaxSize;

        /// <summary>预热数，截断到 maxSize；&lt;0 按 0</summary>
        public readonly int Prewarm;

        public EffectSpec(
            EffectId id,
            EffectDriverKind driverKind = EffectDriverKind.Particle,
            bool isSingleton = false,
            int maxSize = 16,
            int prewarm = 0,
            string key = null)
        {
            Id = id;
            Key = string.IsNullOrEmpty(key) ? EffectCatalog.KeyPrefix + id : key;
            DriverKind = driverKind;
            IsSingleton = isSingleton;
            MaxSize = maxSize > 0 ? maxSize : 1;
            Prewarm = prewarm < 0 ? 0 : prewarm;
        }

        public override string ToString()
            => $"{Id}(kind={DriverKind} key={Key} singleton={IsSingleton} max={MaxSize} prewarm={Prewarm})";
    }

    /// <summary>特效装配表，加一行=新增一个特效（+枚举一项+预制体）</summary>
    /// <remarks>未登记的 EffectId（Flash/Shake/ScreenShake）Play 打 LogError 返回 None；EffectId.None=这次不播，合法值，不查表不报错，与未登记不同。</remarks>
    internal static class EffectCatalog
    {
        internal const string KeyPrefix = "effects/";

        private static readonly EffectSpec[] Specs =
        {
            new EffectSpec(EffectId.BurstSparks,  EffectDriverKind.Particle, isSingleton: false, maxSize: 32, prewarm: 8),

            new EffectSpec(EffectId.MudSplash, EffectDriverKind.Particle, isSingleton: false, maxSize: 16, prewarm: 4),

            new EffectSpec(EffectId.Shatter,  EffectDriverKind.Shatter,  maxSize: 16, prewarm: 0),

            new EffectSpec(EffectId.Highlight, EffectDriverKind.Highlight, isSingleton: true, maxSize: 1, prewarm: 0),

            new EffectSpec(EffectId.SteamBlast,EffectDriverKind.Particle,isSingleton: false, maxSize: 16, prewarm:4)
        };

        internal static IReadOnlyList<EffectSpec> All => Specs;

        internal static bool TryGet(EffectId id, out EffectSpec spec)
        {
            for (int i = 0; i < Specs.Length; i++)
            {
                if (Specs[i].Id == id)
                {
                    spec = Specs[i];
                    return true;
                }
            }

            spec = default;
            return false;
        }
    }
}
