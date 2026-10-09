using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Foundation
{
    /// <summary>池空策略，只有 CreateOrDrop 把 MaxSize 当硬上限</summary>
    public enum PoolOverflowPolicy
    {
        /// <summary>池空即创建并警告，MaxSize 只作空闲保留上限</summary>
        CreateAndWarn = 0,

        /// <summary>累计创建数&lt;MaxSize 时创建，达上限则丢弃并计数</summary>
        CreateOrDrop = 1,

        /// <summary>池空即丢弃，只吃 Prewarm 出来的与已归还的对象</summary>
        DropSilently = 2,

        /// <summary>池空即抛 InvalidOperationException</summary>
        Throw = 3,
    }

    /// <summary>池的只读快照，供调试与观测用</summary>
    public readonly struct PoolStats
    {
        public readonly int Active;

        public readonly int Idle;

        public readonly int Peak;

        public readonly int TotalCreated;

        /// <summary>累计丢弃的请求数</summary>
        public readonly int TotalDropped;

        public PoolStats(int active, int idle, int peak, int totalCreated, int totalDropped)
        {
            Active = active;
            Idle = idle;
            Peak = peak;
            TotalCreated = totalCreated;
            TotalDropped = totalDropped;
        }

        public override string ToString()
            => $"active={Active} idle={Idle} peak={Peak} created={TotalCreated} dropped={TotalDropped}";
    }

    /// <remarks>Get/TryGet 有货就弹出，空了按 PoolOverflowPolicy 处理。Prewarm 只造对象不触发回调；Clear 清空闲区并逐个 onDestroy，不动已借出对象；Dispose 幂等，之后再取抛 ObjectDisposedException。Release 归还，空闲区达 MaxSize 则不再保留、直接交 onDestroy；MaxSize 既是空闲区保留上限也是 CreateOrDrop 总量上限。仅主线程，不做锁。</remarks>
    public class Pool<T> : IDisposable where T : class
    {
        private readonly string _name;
        private readonly Func<T> _factory;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private readonly Action<T> _onDestroy;
        private readonly int _maxSize;
        private readonly PoolOverflowPolicy _overflowPolicy;

        private readonly Stack<T> _idle = new Stack<T>();

        private int _active;
        private int _peak;
        private int _totalCreated;
        private int _totalDropped;
        private bool _disposed;

        /// <summary>池名，只用于日志，空则取 typeof(T).Name</summary>
        public string Name => _name;

        public int MaxSize => _maxSize;

        public int ActiveCount => _active;

        public int IdleCount => _idle.Count;

        /// <summary>兼容旧 API，等同 IdleCount</summary>
        public int Count => _idle.Count;

        /// <remarks>factory 不能返回 null；maxSize 必须&gt;0</remarks>
        public Pool(
            Func<T> factory,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            string name = null,
            int maxSize = int.MaxValue,
            PoolOverflowPolicy overflowPolicy = PoolOverflowPolicy.CreateAndWarn,
            Action<T> onDestroy = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));

            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize), "maxSize 必须大于 0。");

            _name = string.IsNullOrEmpty(name) ? typeof(T).Name : name;
            _onGet = onGet;
            _onRelease = onRelease;
            _onDestroy = onDestroy;
            _maxSize = maxSize;
            _overflowPolicy = overflowPolicy;
        }

        /// <summary>借出对象，被丢弃时返回 false 且 obj 为 null；策略 Throw 时池空抛 InvalidOperationException</summary>
        public bool TryGet(out T obj)
        {
            ThrowIfDisposed();
            obj = null;

            if (_idle.Count > 0)
            {
                obj = _idle.Pop();
                MarkActive(obj);
                return true;
            }

            switch (_overflowPolicy)
            {
                case PoolOverflowPolicy.CreateAndWarn:
                    Debug.LogWarning($"[Pool<{_name}>] 池为空，正在现场创建对象。" +
                                     $"如果这是热路径，请考虑 Prewarm() 或改用 CreateOrDrop。");
                    obj = CreateNew();
                    MarkActive(obj);
                    return true;

                case PoolOverflowPolicy.CreateOrDrop:
                    if (_totalCreated < _maxSize)
                    {
                        obj = CreateNew();
                        MarkActive(obj);
                        return true;
                    }

                    _totalDropped++;
                    return false;

                case PoolOverflowPolicy.DropSilently:
                    _totalDropped++;
                    return false;

                default: // Throw
                    _totalDropped++;
                    throw new InvalidOperationException(
                        $"[Pool<{_name}>] 池为空且策略为 Throw（MaxSize={_maxSize}）。");

            }
        }

        /// <summary>借出对象，池空且策略为丢弃时返回 null</summary>
        public T Get() => TryGet(out var obj) ? obj : null;

        /// <summary>归还对象</summary>
        /// <remarks>obj 为 null 时只记警告并返回，不抛异常</remarks>
        public void Release(T obj)
        {
            ThrowIfDisposed();

            if (obj == null)
            {
                Debug.LogWarning($"[Pool<{_name}>] Release(null) 被忽略。");
                return;
            }

            _onRelease?.Invoke(obj);

            _active--;
            if (_active < 0)
            {
                Debug.LogError($"[Pool<{_name}>] Active 计数小于 0：Release 次数多于 Get。已复位为 0。");
                _active = 0;
            }

            if (_idle.Count >= _maxSize)
            {
                // 空闲区已满：不再保留，交给 onDestroy 销毁（GameObject 池靠这条兜住不增长）
                _onDestroy?.Invoke(obj);
                return;
            }

            _idle.Push(obj);
        }

        /// <summary>预创建 count 个对象放进空闲区，不触发 onGet/onRelease；count 为负抛异常</summary>
        public void Prewarm(int count)
        {
            ThrowIfDisposed();

            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            int create = Math.Min(count, Math.Max(0, _maxSize - _idle.Count));
            for (int i = 0; i < create; i++)
            {
                _idle.Push(CreateNew());
            }
        }

        /// <summary>清空空闲区并逐个 onDestroy，已借出对象不受影响</summary>
        public void Clear()
        {
            if (_onDestroy != null)
            {
                foreach (var obj in _idle)
                {
                    _onDestroy(obj);
                }
            }

            _idle.Clear();
        }

        public PoolStats GetStats()
            => new PoolStats(_active, _idle.Count, _peak, _totalCreated, _totalDropped);

        /// <summary>清空空闲区并标记不可用，幂等</summary>
        public void Dispose()
        {
            if (_disposed) return;

            Clear();
            _disposed = true;
        }

        private void MarkActive(T obj)
        {
            _active++;
            if (_active > _peak) _peak = _active;

            _onGet?.Invoke(obj);
        }

        private T CreateNew()
        {
            _totalCreated++;

            T obj = _factory();
            if (obj == null)
            {
                Debug.LogError($"[Pool<{_name}>] factory 返回了 null，池的计数会失真。");
            }

            return obj;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException($"[Pool<{_name}>]");
        }
    }

    /// <summary>引用类型池旧别名，仅供 AudioManager 使用</summary>
    public class PoolInClass<T> : Pool<T> where T : class
    {
        public PoolInClass(
            Func<T> factory,
            Action<T> onGet = null,
            Action<T> onRelease = null) : base(factory, onGet, onRelease)
        {
        }
    }
}
