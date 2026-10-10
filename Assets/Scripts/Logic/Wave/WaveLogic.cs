using System.Collections.Generic;
using UnityEngine;
using DeepseaOil.Data;
using DeepseaOil.Logic.Events;
using cfg.dso;

namespace DeepseaOil.Logic.Wave
{

    public enum WavePhase
    {

        Prep = 0,

        Battle = 1,

        Settle = 2,
    }

    // 波次状态机：只算何时刷/刷几只/刷在哪，不建物体；存活数由驱动方喂入，本类不认识敌人
    public sealed class WaveLogic
    {
        public readonly struct SpawnRequest
        {
            public readonly Vector2 Position;

            public readonly int WaveIndex;

            public readonly int Remaining;

            public SpawnRequest(Vector2 position, int waveIndex, int remaining)
            {
                Position = position;
                WaveIndex = waveIndex;
                Remaining = remaining;
            }
        }

        private const float MinSpawnInterval = 0.05f;

        private readonly IReadOnlyList<WaveSpec> _waves;

        private int _cursor;

        private int _waveIndex = 1;

        private int _remaining;

        private float _timer;

        private WavePhase _phase = WavePhase.Prep;

        private bool _phaseEntered;

        private SeedType _pendingSeed = SeedType.None;

        private float _spawnTimer;

        public int WaveIndex => _waveIndex;

        public int Remaining => _remaining;

        public WavePhase Phase => _phase;

        public float PhaseRemaining => _timer > 0f ? _timer : 0f;

        public bool IsSpawning => _phase == WavePhase.Battle && _remaining > 0;

        public SeedType PendingSeedGrant => _pendingSeed;

        public string CurrentWaveName => Current != null ? Current.Name : string.Empty;

        public int WaveCount => _waves?.Count ?? 0;

        public WaveLogic(IReadOnlyList<WaveSpec> waves)
        {
            _waves = waves;
        }

        private WaveSpec Current
            => _waves == null || _waves.Count == 0 ? null : _waves[_cursor % _waves.Count];

        public void Reset()
        {
            _cursor = 0;
            _waveIndex = 1;
            _remaining = 0;
            _timer = 0f;
            _phase = WavePhase.Prep;
            _phaseEntered = false;
            _pendingSeed = SeedType.None;
        }

        public SeedType ConsumeSeedGrant()
        {
            SeedType seed = _pendingSeed;

            _pendingSeed = SeedType.None;

            return seed;
        }

        // now 与 dt 单位秒；dt 暂停时为 0，节拍自然冻结；anyEnemyAlive 由驱动方数
        public void Tick(
            float now,
            float dt,
            bool anyEnemyAlive,
            Vector2 playerPosition,
            List<SpawnRequest> output)
        {
            if (output == null) return;

            output.Clear();

            WaveSpec spec = Current;

            if (spec == null) return;

            if (!_phaseEntered)
            {
                EnterPhase(WavePhase.Prep);
                _timer = DurationOf(_phase, spec, anyEnemyAlive);
            }

            _timer -= dt;

            while (_timer <= 0f)
            {
                if (!AdvancePhase(spec, anyEnemyAlive)) return;

                _timer += DurationOf(_phase, spec, anyEnemyAlive);
            }

            if (_phase != WavePhase.Battle) return;

            SpawnDue(now, playerPosition, spec, dt, output);
        }

        private bool AdvancePhase(WaveSpec spec, bool anyEnemyAlive)
        {
            switch (_phase)
            {
                case WavePhase.Prep:
                    EnterPhase(WavePhase.Battle);
                    _remaining = SanitizeCount(spec.EnemiesPerWave);
                    return true;

                case WavePhase.Battle:
                    EnterPhase(WavePhase.Settle);
                    return true;

                default:

                    if (anyEnemyAlive) return false;

                    _cursor++;
                    _waveIndex++;

                    EnterPhase(WavePhase.Prep);
                    return true;
            }
        }

        private float DurationOf(WavePhase phase, WaveSpec spec, bool anyEnemyAlive)
        {
            switch (phase)
            {
                case WavePhase.Prep: return Sanitize(spec.PrepTime);
                case WavePhase.Battle: return Sanitize(spec.BattleTime);
                default:

                    return anyEnemyAlive ? 0f : Sanitize(spec.SettleTime);
            }
        }

        private void EnterPhase(WavePhase phase)
        {
            _phase = phase;
            _phaseEntered = true;

            if (phase == WavePhase.Prep)
            {
                WaveSpec prepSpec = Current;

                _pendingSeed = prepSpec != null ? prepSpec.GrantSeed : SeedType.None;

                _spawnTimer = 0f;
            }

            // 每个阶段都要播：HUD 的阶段文案与倒计时靠它刷新
            PublishPhase();
        }

        private void SpawnDue(
            float now,
            Vector2 playerPosition,
            WaveSpec spec,
            float dt,
            List<SpawnRequest> output)
        {
            float interval = spec.SpawnInterval > MinSpawnInterval ? spec.SpawnInterval : MinSpawnInterval;

            _spawnTimer -= dt;

            if (_spawnTimer > 0f) return;

            if (_remaining <= 0) return;

            _spawnTimer = interval;

            _remaining--;

            output.Add(new SpawnRequest(
                SpawnPosition(now, playerPosition, spec.SpawnRadius),
                _waveIndex,
                _remaining));
        }

        // 出生点：以玩家为圆心按波次错开角度；不判地形，越界由刚体撞墙兜住
        private Vector2 SpawnPosition(float now, Vector2 playerPosition, float radius)
        {
            float baseAngle = now * 0.7f + _waveIndex * 1.3f;

            float radians = baseAngle + _remaining * Mathf.PI * 0.5f;

            float r = radius > 0f ? radius : 1f;

            var offset = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * r;

            return playerPosition + offset;
        }

        private static float Sanitize(float seconds)
        {
            return float.IsNaN(seconds) || seconds < 0f ? 0f : seconds;
        }

        private static int SanitizeCount(int count)
        {
            return count < 0 ? 0 : count;
        }

        private void PublishPhase()
        {
            EventBus<WavePhaseChanged>.Publish(new WavePhaseChanged(
                _waveIndex,
                _phase,
                Current?.GrantSeed ?? SeedType.None,
                PhaseRemaining));
        }
    }
}
