using System;
using System.Collections.Generic;

namespace DeepseaOil.Logic.Services.Time
{
    /// <summary>基于时间槽的时间轮，不依赖 Unity，由外部 Advance(deltaTime) 推进</summary>
    /// <remarks>一个实例只对应一条时间轴；Scaled/Unscaled 由外部 TimerService 各持一个实例</remarks>
    public sealed class TimerWheel
    {
        private sealed class TimerNode
        {
            public TimerHandle Handle;

            /// <summary>Timer 当前所在的目标槽</summary>
            public int TargetSlot;

            /// <summary>跨越完整时间轮的剩余圈数</summary>
            /// <remarks>WheelDuration=10s、延迟=25s 时首次到达 TargetSlot 为 2；每经一次 TargetSlot 减 1，减到 0 的下一次才真正检查 ExpireTime</remarks>
            public int RemainingRounds;

            public bool Repeat;

            public float Interval;

            /// <summary>当前周期开始时间</summary>
            public double PeriodStartTime;

            /// <summary>当前周期结束时间</summary>
            public double ExpireTime;

            public Action Callback;

            public bool Cancelled;
        }

        private readonly List<TimerNode>[] _slots;

        private readonly Dictionary<int, TimerNode> _timers = new();

        /// <summary>每个时间槽代表多少秒</summary>
        private readonly float _slotDuration;

        /// <summary>时间槽数量</summary>
        private readonly int _slotCount;

        /// <summary>当前所在的时间槽</summary>
        private int _currentSlot;

        /// <summary>下一个时间槽的绝对时间</summary>
        private double _nextSlotTime;

        /// <summary>当前时间轴上的时间</summary>
        private double _time;

        /// <summary>下一个 Timer ID</summary>
        private int _nextId = 1;

        public float WheelDuration => _slotDuration * _slotCount;

        /// <summary>当前时间</summary>
        public double Time => _time;

        public int Count => _timers.Count;

        public TimerWheel(float slotDuration = 0.1f, int slotCount = 512)
        {
            if (slotDuration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(slotDuration));

            if (slotCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(slotCount));

            _slotDuration = slotDuration;
            _slotCount = slotCount;

            _slots = new List<TimerNode>[slotCount];

            for (int i = 0; i < slotCount; i++)
            {
                _slots[i] = new List<TimerNode>();
            }

            _currentSlot = 0;

            // 当前时间为 0，第一次切槽发生在 slotDuration。
            _nextSlotTime = _slotDuration;

            _time = 0d;
        }

        /// <summary>创建 Timer</summary>
        public TimerHandle Schedule(TimerOptions options, Action callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (options.Delay < 0f)
                throw new ArgumentOutOfRangeException(nameof(options.Delay));

            if (options.Repeat && options.Interval <= 0f)
                throw new ArgumentOutOfRangeException(nameof(options.Interval));

            int id = CreateId();

            TimerHandle handle = new(id);

            TimerNode node = new()
            {
                Handle = handle,
                TargetSlot = 0,
                RemainingRounds = 0,
                Repeat = options.Repeat,
                Interval = options.Repeat ? options.Interval : 0f,
                Callback = callback,
                Cancelled = false,
                ExpireTime = _time + options.Delay,
                PeriodStartTime = _time
            };

            _timers.Add(id, node);

            AddToWheel(node);

            return handle;
        }

        /// <summary>推进时间轮</summary>
        /// <remarks>一帧可能跨多个槽，故按 while 逐槽处理</remarks>
        public void Advance(float deltaTime)
        {
            if (deltaTime < 0f)
            {
                deltaTime = 0f;
            }

            _time += deltaTime;

            while (_time >= _nextSlotTime)
            {
                _currentSlot++;

                if (_currentSlot >= _slotCount)
                {
                    _currentSlot = 0;
                }

                ProcessSlot(_currentSlot);

                _nextSlotTime += _slotDuration;
            }
        }

        /// <summary>处理一个时间槽</summary>
        private void ProcessSlot(int slotIndex)
        {
            List<TimerNode> slot = _slots[slotIndex];

            if (slot.Count == 0)
            {
                return;
            }

            // 先清空原列表：回调里可以新建 Timer，不去改正在遍历的列表。
            TimerNode[] nodes = slot.ToArray();

            slot.Clear();

            for (int i = 0; i < nodes.Length; i++)
            {
                TimerNode node = nodes[i];

                if (node.Cancelled)
                {
                    continue;
                }

                // 未走完整圈，只是路过目标槽：不能调 AddToWheel()，它会重算 RemainingRounds。
                if (node.RemainingRounds > 0)
                {
                    node.RemainingRounds--;

                    Requeue(node);

                    continue;
                }

                // 圈数已尽才检查绝对时间，此时放回原目标槽。
                if (_time < node.ExpireTime)
                {
                    Requeue(node);

                    continue;
                }

                Execute(node);
            }
        }

        private void Execute(TimerNode node)
        {
            if (node.Cancelled)
            {
                return;
            }

            // 单次 Timer 在回调前就从管理表移除，回调内 Exists(handle) 得 false。
            if (!node.Repeat)
            {
                _timers.Remove(node.Handle.Id);
            }

            Action callback = node.Callback;

            try
            {
                callback?.Invoke();
            }
            finally
            {
                if (!node.Repeat)
                {
                    node.Callback = null;
                }
            }

            if (node.Cancelled)
            {
                return;
            }

            // 循环 Timer：回调内取消的话，Cancelled 会挡住重新入轮。
            if (node.Repeat && _timers.ContainsKey(node.Handle.Id))
            {
                node.PeriodStartTime = node.ExpireTime;
                node.ExpireTime += node.Interval;

                // 一帧跨过多个周期时不补执行，直接把下次执行时间推到当前时间之后。
                if (node.ExpireTime <= _time)
                {
                    node.PeriodStartTime = _time;
                    node.ExpireTime = _time + node.Interval;
                }

                AddToWheel(node);
            }
        }

        /// <summary>首次入轮或循环 Timer 开新周期时，按 ExpireTime 重算 TargetSlot 与 RemainingRounds</summary>
        private void AddToWheel(TimerNode node)
        {
            double remaining = node.ExpireTime - _time;

            if (remaining < 0d)
            {
                remaining = 0d;
            }

            // 向上取整，至少等一个槽（remaining=0.01、slotDuration=0.1 → ticks=1）。
            int ticks = (int)Math.Ceiling(remaining / _slotDuration);

            if (ticks < 1)
            {
                ticks = 1;
            }

            node.TargetSlot = (_currentSlot + ticks) % _slotCount;

            // 需要跨越的完整圈数（slotCount=512、ticks=600 → 1）。
            node.RemainingRounds = (ticks - 1) / _slotCount;

            _slots[node.TargetSlot].Add(node);
        }

        /// <summary>只按已算好的目标槽放回，不重算 RemainingRounds</summary>
        private void Requeue(TimerNode node)
        {
            _slots[node.TargetSlot].Add(node);
        }

        /// <summary>清除所有 Timer 并重置时间轮</summary>
        public void Clear()
        {
            _timers.Clear();

            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].Clear();
            }

            _currentSlot = 0;
            _nextSlotTime = _slotDuration;
            _time = 0d;
            _nextId = 1;
        }


        /// <summary>生成新的 Timer ID</summary>
        private int CreateId()
        {
            int id = _nextId++;

            if (_nextId == int.MaxValue)
            {
                _nextId = 1;
            }

            return id;
        }

        /// <summary>取消 Timer</summary>
        public bool Cancel(TimerHandle handle)
        {
            if (!handle.IsValid)
            {
                return false;
            }

            if (!_timers.TryGetValue(handle.Id, out TimerNode node))
            {
                return false;
            }

            node.Cancelled = true;

            _timers.Remove(handle.Id);

            return true;
        }

        /// <summary>判断 Timer 是否存在</summary>
        public bool Exists(TimerHandle handle)
        {
            return handle.IsValid &&
                   _timers.ContainsKey(handle.Id);
        }

        public bool TryGetInfo(TimerHandle handle, out TimerInfo info)
        {
            if (!_timers.TryGetValue(handle.Id, out TimerNode node))
            {
                info = default;
                return false;
            }

            double elapsed = _time - node.PeriodStartTime;

            double remaining = node.ExpireTime - _time;

            if (remaining < 0)
                remaining = 0;

            double duration = node.ExpireTime - node.PeriodStartTime;

            float progress = duration <= 0 ? 1f : (float)(elapsed / duration);

            progress = Math.Clamp(progress, 0f, 1f);

            info = new TimerInfo(
                duration: (float)duration,
                elapsedTime: (float)Math.Max(0, elapsed),
                remainingTime: (float)remaining,
                progress: progress,
                isRepeating: node.Repeat);

            return true;
        }
    }
}