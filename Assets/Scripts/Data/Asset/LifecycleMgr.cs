using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Data
{
    /// <summary>生命周期：冷却期标记 + LRU 淘汰</summary>
    /// <remarks>Tick 由 GameRoot 驱动，OnSceneSwitch 由 SceneService 在切场景前调。主线程独占；isPreloaded 永不淘汰</remarks>
    internal sealed class LifecycleMgr
    {
        private readonly CacheStore _cache;
        private readonly int _maxEntries;
        private readonly int _maxEvictPerTick;
        private int _evictedCount;

        public int EvictedCount => _evictedCount;

        public LifecycleMgr(CacheStore cache, int maxEntries, int maxEvictPerTick)
        {
            _cache = cache;
            _maxEntries = maxEntries;
            _maxEvictPerTick = maxEvictPerTick;
        }

        /// <summary>每帧推进：先标记冷却期到期，再按需淘汰</summary>
        public void Tick(float dt)
        {
            float now = Time.realtimeSinceStartup;

            foreach (var kv in _cache.AllEntries)
            {
                var entry = kv.Value;

                if (entry.refCount > 0) continue;
                if (entry.isPreloaded) continue;
                if (entry.canEvict) continue;
                if (now < entry.cooldownUntil) continue;

                entry.canEvict = true;
            }

            int overflow = _cache.Count - _maxEntries;
            if (overflow > 0)
                EvictLRU(overflow);
        }

        /// <summary>按 LRU 淘汰</summary>
        /// <remarks>只淘汰 canEvict 且非 isPreloaded</remarks>
        private void EvictLRU(int count)
        {
            List<KeyValuePair<string, CacheEntry>> candidates = null;

            foreach (var kv in _cache.AllEntries)
            {
                if (!kv.Value.canEvict) continue;
                if (kv.Value.isPreloaded) continue;

                (candidates ?? (candidates = new List<KeyValuePair<string, CacheEntry>>())).Add(kv);
            }

            if (candidates == null || candidates.Count == 0)
                return;

            candidates.Sort((a, b) => a.Value.lastAccessTime.CompareTo(b.Value.lastAccessTime));

            int toEvict = Math.Min(Math.Min(count, candidates.Count), _maxEvictPerTick);

            for (int i = 0; i < toEvict; i++)
            {
                _cache.Remove(candidates[i].Key);
                _evictedCount++;
            }

            if (toEvict > 0)
            {
                Resources.UnloadUnusedAssets();
            }
        }

        /// <summary>切场景时调用：上一场景缓存不再享受冷却期保护</summary>
        /// <remarks>isPreloaded 与 refCount &gt; 0 不动</remarks>
        public void OnSceneSwitch()
        {
            foreach (var kv in _cache.AllEntries)
            {
                var entry = kv.Value;

                if (entry.isPreloaded) continue;
                if (entry.refCount > 0) continue;

                entry.cooldownUntil = 0f;
                entry.canEvict = true;
            }
        }
    }
}
