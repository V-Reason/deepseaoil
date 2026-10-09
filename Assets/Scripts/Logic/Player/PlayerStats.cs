using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Events;
using DeepseaOil.Data;
using UnityEngine;

namespace DeepseaOil.Logic.Player
{
    /// <summary>玩家账本：血量+无敌帧+水球计数</summary>
    public sealed class PlayerStats : IAlivable
    {
        private readonly PlayerSpec _spec;

        private float _current;

        private float _invulnerableUntil = float.NegativeInfinity;

        public PlayerStats(PlayerSpec spec)
        {
            _spec = spec;
            _current = spec.MaxHp;
        }

        public PlayerSpec Spec => _spec;

        public int WaterBallCount { get; private set; }

        public bool IsAlive => _current > 0f;

        /// <remarks>必须写成 !(now &lt; invulnerableUntil)：否则 NaN 时永久无敌且看不出来</remarks>
        public static bool CanTakeDamage(float now, float invulnerableUntil)
        {
            return !(now < invulnerableUntil);
        }

        /// <remarks>被无敌帧挡掉时不扣血也不写无敌；接触伤害贴住会重复结算，靠无敌帧挡</remarks>
        public bool ApplyDamage(float amount, float now)
        {
            if (amount <= 0f) return false;

            if (!CanTakeDamage(now, _invulnerableUntil)) return false;

            _current = Mathf.Max(0f, _current - amount);

            _invulnerableUntil = now + _spec.InvulnerableDuration;

            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));

            return true;
        }

        public void ResetToFull()
        {
            _current = _spec.MaxHp;
            _invulnerableUntil = float.NegativeInfinity;

            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));
        }

        /// <summary>加水球，非正数 no-op</summary>
        public void AddWaterBall(int amount = 1)
        {
            if (amount <= 0) return;

            WaterBallCount += amount;

            EventBus<WaterBallCountChanged>.Publish(new WaterBallCountChanged(WaterBallCount));
        }

        /// <summary>消耗水球，不够时不改状态</summary>
        public bool TryConsumeWater(int amount = 1)
        {
            if (amount <= 0) return false;

            if (WaterBallCount < amount) return false;

            WaterBallCount -= amount;

            EventBus<WaterBallCountChanged>.Publish(new WaterBallCountChanged(WaterBallCount));

            return true;
        }

        public void Announce()
        {
            EventBus<WaterBallCountChanged>.Publish(new WaterBallCountChanged(WaterBallCount));

            EventBus<PlayerHealthChanged>.Publish(new PlayerHealthChanged(_current, _spec.MaxHp));
        }
    }
}
