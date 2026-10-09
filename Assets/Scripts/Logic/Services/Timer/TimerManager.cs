using DeepseaOil.Logic.Services.Time;
using DeepseaOil.Logic.Service;
using System;
using UnityEngine;

namespace DeepseaOil.Logic.Services
{
    /// <summary>全局计时器（时间轮），麻痹/冷却/倒计时这类"持续到某时刻"的东西排这里</summary>
    /// <remarks>非单例：参数走构造，生命周期归组合根。两个时间轴：Schedule* 走缩放时间（暂停即停），ScheduleUnscaled* 走未缩放时间（暂停照走），口径由驱动方给。不要用它做格子上的持续伤害：格子本身每帧 Tick 天然有钟</remarks>
    public sealed class TimerManager : IService
    {
        private const float MAX_DELTA_TIME = 0.1f;

        private readonly float _slotDuration;
        private readonly int _slotCount;

        private TimerWheel _scaledWheel;
        private TimerWheel _unscaledWheel;

        /// <summary>slotDuration=每槽秒数，slotCount=槽数（0.1×512=51.2秒一圈）</summary>
        public TimerManager(float slotDuration = 0.1f, int slotCount = 512)
        {
            _slotDuration = slotDuration;
            _slotCount = slotCount;
        }

        public void Init()
        {
            _scaledWheel = new TimerWheel(_slotDuration, _slotCount);

            _unscaledWheel = new TimerWheel(_slotDuration, _slotCount);
        }

        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (_scaledWheel == null) return;

            // 仅当 deltaTime 也跳才告警：只有 unscaled 跳的是域重载/资源导入/暂停恢复，逐次告警纯噪音且刷 Console
            if (unscaledDeltaTime > 0.5f && deltaTime > 0.5f)
            {
                Debug.LogWarning(
                    $"[Timer] LARGE DELTA! " +
                    $"delta={deltaTime:F3}, " +
                    $"unscaled={unscaledDeltaTime:F3}");
            }
            deltaTime = Math.Min(deltaTime, MAX_DELTA_TIME);
            unscaledDeltaTime = Math.Min(unscaledDeltaTime, MAX_DELTA_TIME);

            _scaledWheel.Advance(deltaTime);
            _unscaledWheel.Advance(unscaledDeltaTime);
        }

        public TimerHandle Schedule(float delay, Action callback)
        {
            return _scaledWheel.Schedule(TimerOptions.Once(delay), callback);
        }

        public TimerHandle ScheduleUnscaled(float delay, Action callback)
        {
            return _unscaledWheel.Schedule(
                TimerOptions.Once(delay),
                callback);
        }

        public TimerHandle ScheduleRepeating(float interval, Action callback)
        {
            return _scaledWheel.Schedule(
                TimerOptions.Loop(interval), callback);
        }

        public TimerHandle ScheduleRepeatingUnscaled(float interval, Action callback)
        {
            return _unscaledWheel.Schedule(
                TimerOptions.Loop(interval), callback);
        }

        /// <remarks>已知边界：两条轮各自从1开始发 id，句柄不带轮标记——两轮撞同一 id 时会先取消 scaled 那条（当前规模碰不到）</remarks>
        public bool Cancel(TimerHandle handle)
        {
            if (_scaledWheel == null) return false;

            return _scaledWheel.Cancel(handle) || _unscaledWheel.Cancel(handle);
        }

        public void Clear()
        {
            _scaledWheel?.Clear();
            _unscaledWheel?.Clear();
        }

        // IService 收尾口
        public void Dispose()
        {
            Clear();
        }

        public bool TryGetInfo(TimerHandle handle, out TimerInfo info)
        {
            if (_scaledWheel != null && _scaledWheel.TryGetInfo(handle, out info))
                return true;

            if (_unscaledWheel != null && _unscaledWheel.TryGetInfo(handle, out info))
                return true;

            info = default;
            return false;
        }

        public int Count => (_scaledWheel?.Count ?? 0) + (_unscaledWheel?.Count ?? 0);
    }
}
