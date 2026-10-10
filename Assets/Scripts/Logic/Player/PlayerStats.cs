using DeepseaOil.Data;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Events;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Logic.Player
{
    // <summary>玩</summary>
    // <remarks>种</remarks>
    public sealed class PlayerStats : IAlivable
    {
        private readonly PlayerSpec _spec;

        private float _current;

        private float _invulnerableUntil = float.NegativeInfinity;

        public PlayerStats(PlayerSpec spec)
        {
            _spec = spec;
            _current = spec.MaxHp;
            Water = Clamp(spec.WaterStart, spec.WaterCapacity);
            Earth = Clamp(spec.EarthStart, spec.EarthCapacity);
            Seed = SeedType.None;
        }

        public PlayerSpec Spec => _spec;

        public int Water { get; private set; }

        public int Earth { get; private set; }

        // <summary>当</summary>
        public SeedType Seed { get; private set; }

        public int WaterCapacity => _spec.WaterCapacity;

        public int EarthCapacity => _spec.EarthCapacity;

        public bool IsAlive => _current > 0f;

        public float Current => _current;

        // <remarks>必</remarks>
        public static bool CanTakeDamage(float now, float invulnerableUntil)
        {
            return !(now < invulnerableUntil);
        }

        public bool ApplyDamage(float amount, float now)
        {
            if (amount <= 0f) return false;

            if (!CanTakeDamage(now, _invulnerableUntil)) return false;

            _current = Mathf.Max(0f, _current - amount);

            _invulnerableUntil = now + _spec.InvulnerableDuration;

            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));

            return true;
        }

        // <summary>回</summary>
        public bool TryHeal(float amount = 1f)
        {
            if (amount <= 0f) return false;

            if (_current >= _spec.MaxHp) return false;

            _current = Mathf.Min(_spec.MaxHp, _current + amount);

            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));

            return true;
        }

        public void ResetToFull()
        {
            _current = _spec.MaxHp;
            _invulnerableUntil = float.NegativeInfinity;

            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));
        }

        public int AmountOf(ResourceKind kind)
        {
            return kind == ResourceKind.Water ? Water : Earth;
        }

        public int CapacityOf(ResourceKind kind)
        {
            return kind == ResourceKind.Water ? WaterCapacity : EarthCapacity;
        }

        // <summary>消</summary>
        public bool TryConsume(ResourceKind kind, int amount = 1)
        {
            if (amount <= 0) return false;

            if (AmountOf(kind) < amount) return false;

            if (kind == ResourceKind.Water) Water -= amount;
            else Earth -= amount;

            PublishAmmo();

            return true;
        }

        // <summary>补</summary>
        public bool Refill(ResourceKind kind)
        {
            int capacity = CapacityOf(kind);

            if (AmountOf(kind) >= capacity) return false;

            if (kind == ResourceKind.Water) Water = capacity;
            else Earth = capacity;

            PublishAmmo();

            return true;
        }

        // <summary>发</summary>
        public bool GrantSeed(SeedType seed)
        {
            if (seed == SeedType.None) return false;

            Seed = seed;

            PublishAmmo();

            return true;
        }

        // <summary>消</summary>
        public bool TryConsumeSeed()
        {
            if (Seed == SeedType.None) return false;

            Seed = SeedType.None;

            PublishAmmo();

            return true;
        }

        public void Announce()
        {
            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));

            PublishAmmo();
        }

        private void PublishAmmo()
        {
            EventBus<PlayerAmmoChanged>.Publish(
                new PlayerAmmoChanged(Water, WaterCapacity, Earth, EarthCapacity, Seed));
        }

        private static int Clamp(int value, int capacity)
        {
            if (capacity < 0) capacity = 0;

            if (value < 0) return 0;

            return value > capacity ? capacity : value;
        }
    }
}
