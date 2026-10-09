using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Data
{
    /// <summary>资源模块：Data 层资源查询与生命周期入口，仅主线程调用</summary>
    /// <remarks>Init=GameRoot.Awake；Tick=GameRoot step② 每帧；OnSceneSwitch=LoadScene 前；Dispose=GameRoot.OnDestroy。不订阅事件，观测走 DataMetrics 拉模型</remarks>
    public static class AssetModule
    {
        private const float COOLDOWN_SECONDS    = 60f;
        private const int   MAX_CACHE_ENTRIES   = 100;
        private const int   MAX_CONCURRENT_LOAD = 4;
        private const int   MAX_EVICT_PER_TICK  = 8;

        private static AssetRegistry  _registry;
        private static CacheStore     _cache;
        private static RefCounter     _refCounter;
        private static LoadScheduler  _scheduler;
        private static LifecycleMgr   _lifecycle;
        private static FailureHandler _failure;

        private static readonly Dictionary<string, List<Action<UnityEngine.Object>>> _pendingLoads =
            new Dictionary<string, List<Action<UnityEngine.Object>>>();

        private static int _cacheHits;
        private static int _cacheMisses;

        private static bool _initialized;

        public static bool IsInitialized => _initialized;

        internal static CacheStore Cache => _cache;
        internal static LoadScheduler Scheduler => _scheduler;
        internal static LifecycleMgr Lifecycle => _lifecycle;
        internal static FailureHandler Failure => _failure;
        internal static int CacheHits => _cacheHits;
        internal static int CacheMisses => _cacheMisses;

        /// <summary>初始化，不创建 Unity 资源</summary>
        public static void Init()
        {
            if (_initialized)
                throw new InvalidOperationException("[Asset] AssetModule.Init called twice");

            _registry   = new AssetRegistry();
            _cache      = new CacheStore();
            _refCounter = new RefCounter(_cache, COOLDOWN_SECONDS);
            _scheduler  = new LoadScheduler(_registry, MAX_CONCURRENT_LOAD);
            _lifecycle  = new LifecycleMgr(_cache, MAX_CACHE_ENTRIES, MAX_EVICT_PER_TICK);
            _failure    = new FailureHandler();

            _pendingLoads.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
            _initialized = true;
        }

        /// <summary>每帧推进 Scheduler 与 Lifecycle，未 Init 时 no-op</summary>
        public static void Tick(float dt)
        {
            if (!_initialized) return;

            _scheduler.Tick(dt);
            _lifecycle.Tick(dt);
        }

        /// <summary>切场景前调用，不清挂起请求，不动 refCount>0 与 isPreloaded 条目</summary>
        public static void OnSceneSwitch()
        {
            if (!_initialized) return;

            _lifecycle.OnSceneSwitch();
        }

        public static void Dispose()
        {
            if (!_initialized) return;

            _scheduler.Clear();
            _cache.Clear();
            _pendingLoads.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
            _initialized = false;
        }

        /// <summary>异步加载资源，跨层可调；命中缓存即 refCount++ 返回已完成句柄；同 Key 并发请求合并为一个 IO</summary>
        /// <remarks>成功顺序 Put→Retain→Complete；失败重试 2 次后返回降级资源（可能 null）；句柄须成对 Release</remarks>
        public static AsyncHandle<T> LoadAsync<T>(string key) where T : UnityEngine.Object
        {
            if (!_initialized)
                throw new InvalidOperationException("[Asset] LoadAsync before Init");

            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[Asset] LoadAsync with empty key");
                return AsyncHandle<T>.Completed(null);
            }

            if (_cache.TryGet<T>(key, out var entry))
            {
                _cacheHits++;
                _cache.Touch(key);
                _refCounter.Retain(key);
                return AsyncHandle<T>.Completed(entry.asset as T);
            }

            _cacheMisses++;

            if (_pendingLoads.TryGetValue(key, out var waiters))
            {
                var merged = AsyncHandle<T>.Create();
                waiters.Add(asset => merged.Complete(asset as T));
                return merged;
            }

            var handle = AsyncHandle<T>.Create();
            var list = new List<Action<UnityEngine.Object>> { asset => handle.Complete(asset as T) };
            _pendingLoads[key] = list;

            _scheduler.Enqueue(new LoadRequest
            {
                key = key,
                type = typeof(T),
                onDone = asset =>
                {
                    _cache.Put(key, asset, isPreloaded: false);
                    _refCounter.Retain(key);
                    DispatchPending(key, asset);
                },
                onFail = reason =>
                {
                    _failure.RecordFailure(key, reason);
                    DispatchPending(key, _failure.GetFallback<T>());
                },
            });

            return handle;
        }

        /// <summary>同步加载，阻塞主线程，只用于体量很小的资源；与 LoadAsync 共用缓存与引用计数；失败不重试，返回降级资源（可能 null）</summary>
        public static T Load<T>(string key) where T : UnityEngine.Object
        {
            if (!_initialized)
                throw new InvalidOperationException("[Asset] Load before Init");

            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[Asset] Load with empty key");
                return null;
            }

            if (_cache.TryGet<T>(key, out var entry))
            {
                _cacheHits++;
                _cache.Touch(key);
                _refCounter.Retain(key);
                return entry.asset as T;
            }

            _cacheMisses++;

            var asset = Resources.Load<T>(_registry.ResolvePath(key));
            if (asset == null)
            {
                _failure.RecordFailure(key, "sync load returned null");
                return _failure.GetFallback<T>();
            }

            _cache.Put(key, asset, isPreloaded: false);
            _refCounter.Retain(key);
            return asset;
        }

        /// <summary>不触发加载，不改引用计数，命中时更新访问时间</summary>
        public static bool TryGet<T>(string key, out T asset) where T : UnityEngine.Object
        {
            asset = null;

            if (!_initialized || string.IsNullOrEmpty(key))
                return false;

            if (_cache.TryGet<T>(key, out var entry))
            {
                _cache.Touch(key);
                asset = entry.asset as T;
                return true;
            }

            return false;
        }

        /// <summary>未知 Key/重复释放记警告；refCount 归零进冷却期，不立即卸载</summary>
        public static void Release(string key)
        {
            if (!_initialized || string.IsNullOrEmpty(key))
                return;

            if (!_refCounter.Release(key))
                Debug.LogWarning($"[Asset] Release unknown or over-released key: {key}");
        }

        /// <summary>预加载并标记常驻，永不淘汰、不计引用计数；类型用 typeof(UnityEngine.Object)。待验证：传基类时 LoadAsync 类型过滤是否生效（见 Docs/待办.md）</summary>
        public static void Preload(string key)
        {
            if (!_initialized || string.IsNullOrEmpty(key))
                return;

            if (_cache.TryGetEntry(key, out var existing))
            {
                existing.isPreloaded = true;
                existing.canEvict = false;
                existing.cooldownUntil = 0f;
                return;
            }

            if (_pendingLoads.TryGetValue(key, out var waiters))
            {
                waiters.Add(asset =>
                {
                    if (_cache.TryGetEntry(key, out var e))
                    {
                        e.isPreloaded = true;
                        e.canEvict = false;
                    }
                });
                return;
            }

            var list = new List<Action<UnityEngine.Object>>
            {
                asset => _cache.Put(key, asset, isPreloaded: true)
            };
            _pendingLoads[key] = list;

            _scheduler.Enqueue(new LoadRequest
            {
                key = key,
                type = typeof(UnityEngine.Object),
                onDone = asset => DispatchPending(key, asset),
                onFail = reason =>
                {
                    _failure.RecordFailure(key, reason);
                    _pendingLoads.Remove(key);
                },
            });
        }

        /// <summary>降级资源由业务注册，Data 层不创建</summary>
        public static void RegisterFallback<T>(T fallback) where T : UnityEngine.Object
        {
            if (!_initialized)
                throw new InvalidOperationException("[Asset] RegisterFallback before Init");

            _failure.RegisterFallback(fallback);
        }

        private static void DispatchPending(string key, UnityEngine.Object asset)
        {
            if (!_pendingLoads.TryGetValue(key, out var waiters))
                return;

            _pendingLoads.Remove(key);

            for (int i = 0; i < waiters.Count; i++)
                waiters[i]?.Invoke(asset);
        }
    }
}
