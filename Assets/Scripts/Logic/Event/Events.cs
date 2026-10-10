using DeepseaOil.Logic.Service;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DeepseaOil.Data;
using cfg.dso;

namespace DeepseaOil.Logic.Events
{
    // Intent
    public readonly struct RequestPause { }

    public readonly struct RequestResume { }

    public readonly struct RequestChangeScene
    {
        public readonly string sceneName;
        public RequestChangeScene(string n) { sceneName = n; }
    }

    // 请求 HUD 重新播报一次当前值：面板异步加载晚一帧，而事实事件只在变化时发布
    public readonly struct RequestHudRefresh { }

    public readonly struct GamePaused { }

    public readonly struct GameResumed { }

    // 某一格状态变了，表现层据此换贴图
    public readonly struct TileStateChanged
    {
        public readonly Vector3Int Cell;

        public readonly TileStateType State;

        public TileStateChanged(Vector3Int cell, TileStateType state)
        {
            Cell = cell;
            State = state;
        }
    }

    // 瞄准变了的事实，发布方去重；世界侧接不接受由裁决回执决定，不进本事件
    public readonly struct AimChanged
    {
        public readonly bool HasAim;

        public readonly Vector3Int Cell;

        public readonly bool Available;

        public AimChanged(bool hasAim, Vector3Int cell, bool available)
        {
            HasAim = hasAim;
            Cell = cell;
            Available = available;
        }
    }

    // 掉落物被领取了的事实，数量在载荷里不在订阅方
    public readonly struct DropCollected
    {
        public readonly DropType Type;

        public readonly int Amount;

        public DropCollected(DropType type, int amount)
        {
            Type = type;
            Amount = amount;
        }
    }

    /// <summary>玩家二元弹药与战备种子变了</summary>
    public readonly struct PlayerAmmoChanged
    {
        public readonly int Water;

        public readonly int WaterCapacity;

        public readonly int Earth;

        public readonly int EarthCapacity;

        public readonly SeedType Seed;

        public PlayerAmmoChanged(int water, int waterCapacity, int earth, int earthCapacity, SeedType seed)
        {
            Water = water;
            WaterCapacity = waterCapacity;
            Earth = earth;
            EarthCapacity = earthCapacity;
            Seed = seed;
        }
    }

    public readonly struct PlayerHealthChanged
    {
        public readonly float Current;

        public readonly float Max;

        public PlayerHealthChanged(float current, float max)
        {
            Current = current;
            Max = max;
        }
    }

    // 波次或存活数变了；WaveIndex 从 1 起
    public readonly struct WaveChanged
    {
        public readonly int WaveIndex;

        public readonly int Alive;

        public WaveChanged(int waveIndex, int alive)
        {
            WaveIndex = waveIndex;
            Alive = alive;
        }
    }

    /// <summary>波次进入新阶段；备战期带配给种子与倒计时</summary>
    public readonly struct WavePhaseChanged
    {
        public readonly int WaveIndex;

        public readonly DeepseaOil.Logic.Wave.WavePhase Phase;

        public readonly SeedType GrantSeed;

        /// <summary>本阶段剩余秒数</summary>
        public readonly float SecondsLeft;

        public WavePhaseChanged(
            int waveIndex,
            DeepseaOil.Logic.Wave.WavePhase phase,
            SeedType grantSeed,
            float secondsLeft)
        {
            WaveIndex = waveIndex;
            Phase = phase;
            GrantSeed = grantSeed;
            SecondsLeft = secondsLeft;
        }
    }
}
