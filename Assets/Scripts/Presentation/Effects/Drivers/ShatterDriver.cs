using System.Collections.Generic;
using DeepseaOil.Foundation;
using DeepseaOil.Presentation.Primitive;
using DeepseaOil.Presentation.Visual;
using UnityEngine;

namespace DeepseaOil.Presentation.Effects.Drivers
{
    /// <summary>敌人碎裂驱动，耐久归零时飞出 3 块碎片，沿 ctx.Direction 扇形散开，程序生成不需要资源</summary>
    /// <remarks>碎片形状确定性算出、不读随机数：三块总落在同样的相对位置，便于重现。一次碎裂占 PieceCount 个对象，归还时逐个还。</remarks>
    public sealed class ShatterDriver : IEffectDriver
    {
        private const int PieceCount = 3;

        private const float PieceRadiusMeters = 0.13f;

        private const float PieceSpeed = 3.5f;

        private const float SpreadDegrees = 140f;

        private const float DefaultDuration = 0.35f;

        private const float MaxDuration = 5f;

        /// <summary>单帧最多回收几个碎裂（防尖峰）</summary>
        private const int MaxRecyclePerTick = 16;

        private sealed class Shard
        {
            public GameObject Go;
            public Vector2 Velocity;
        }

        private sealed class Burst
        {
            public int Id;
            public int Epoch;
            public readonly List<Shard> Shards = new List<Shard>(PieceCount);
            public float Elapsed;
            public float Duration;
        }

        private readonly Transform _root;
        private readonly int _maxSize;
        private readonly Pool<GameObject> _piecePool;
        private readonly List<Burst> _active = new List<Burst>();
        private readonly List<Burst> _recycleScratch = new List<Burst>();

        private int _nextId;
        private int _epoch;
        private bool _disposed;

        /// <summary>构造碎裂效果</summary>
        internal ShatterDriver(in EffectSpec spec, Transform root) : this(root, spec.MaxSize)
        {
        }

        public ShatterDriver(Transform root, int maxSize = 16)
        {
            _root = root;
            _maxSize = maxSize > 0 ? maxSize : 1;

            _piecePool = new Pool<GameObject>(
                factory: CreatePieceObject,
                onGet: go => go.SetActive(true),
                onRelease: go => go.SetActive(false),
                name: "ShatterPiece",
                maxSize: _maxSize * PieceCount,
                overflowPolicy: PoolOverflowPolicy.CreateOrDrop,
                onDestroy: null);
        }

        public bool IsSingleton => false;

        public string AssetKey => string.Empty;

        public bool IsAssetReady => true;

        public int ActiveInstanceCount => _active.Count;

        public int PooledObjectCount => _piecePool != null ? _piecePool.IdleCount : 0;

        public void OnAssetLoaded(Object asset)
        {
        }

        public EffectHandle Play(EffectId id, in EffectContext ctx)
        {
            if (_disposed) return EffectHandle.None;

            var burst = new Burst
            {
                Id = ++_nextId,
                Epoch = _epoch,
                Elapsed = 0f,
                Duration = Sanitize(ctx.Duration),
            };

            Vector2 forward = ctx.Direction;

            float baseAngle = Mathf.Atan2(forward.y, forward.x);

            float spread = SpreadDegrees;
            int count = Mathf.Max(1, PieceCount);

            Color color = ctx.Tint;
            float scale = Mathf.Max(ctx.Scale, 1e-3f);

            for (int i = 0; i < count; i++)
            {
                if (!_piecePool.TryGet(out GameObject go) || go == null)
                {
                    // 池满：已借出的碎片照常飞完，本次少几块，不抛异常
                    Debug.LogWarning($"[Effect] EnemyShatter 碎片池已满（上限 {_maxSize * PieceCount}），本次少飞 {count - i} 块。");
                    break;
                }

                float offset = count == 1 ? 0f : -spread * 0.5f + spread * i / (count - 1);

                float radians = baseAngle + offset * Mathf.Deg2Rad;

                var direction = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));

                var renderer = go.GetComponent<SpriteRenderer>();

                if (renderer != null)
                {
                    // 用正圆而非贴地形状：碎片飞在空中，压扁会像躺在地上
                    PrimitiveSprites.Configure(
                        renderer,
                        PrimitiveSprites.Circle,
                        color,
                        RenderOrder.ShatterPiece,
                        PieceRadiusMeters * 2f * scale);
                }

                go.transform.position = new Vector3(ctx.Position.x, ctx.Position.y, 0f);

                burst.Shards.Add(new Shard { Go = go, Velocity = direction * PieceSpeed });
            }

            _active.Add(burst);

            return new EffectHandle(burst.Id, burst.Epoch, this);
        }

        public void Stop(EffectHandle handle)
        {
            if (!handle.IsValid) return;
            if (handle.Generation != _epoch) return;

            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Id != handle.Id) continue;

                Recycle(_active[i]);
                _active.RemoveAt(i);
                return;
            }
        }

        public void CleanAll()
        {
            _epoch++;

            for (int i = 0; i < _active.Count; i++)
            {
                Recycle(_active[i]);
            }

            _active.Clear();
        }

        public void Tick(float dt)
        {
            if (_disposed || _active.Count == 0) return;

            _recycleScratch.Clear();

            for (int i = 0; i < _active.Count; i++)
            {
                Burst burst = _active[i];

                burst.Elapsed += dt;

                if (burst.Elapsed >= burst.Duration)
                {
                    _recycleScratch.Add(burst);
                    continue;
                }

                MoveShards(burst, dt);
            }

            if (_recycleScratch.Count == 0) return;

            for (int i = 0; i < _recycleScratch.Count && i < MaxRecyclePerTick; i++)
            {
                Burst burst = _recycleScratch[i];

                Recycle(burst);
                _active.Remove(burst);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            CleanAll();

            _piecePool.Dispose();
        }

        /// <summary>碎片直线飞行，恒速不衰减</summary>
        private static void MoveShards(Burst burst, float dt)
        {
            for (int i = 0; i < burst.Shards.Count; i++)
            {
                Shard shard = burst.Shards[i];

                if (shard.Go == null) continue;

                Vector3 p = shard.Go.transform.position;

                shard.Go.transform.position = new Vector3(
                    p.x + shard.Velocity.x * dt,
                    p.y + shard.Velocity.y * dt,
                    p.z);
            }
        }

        private void Recycle(Burst burst)
        {
            for (int i = 0; i < burst.Shards.Count; i++)
            {
                GameObject go = burst.Shards[i].Go;

                if (go == null) continue;

                _piecePool.Release(go);
            }

            burst.Shards.Clear();
        }

        private static float Sanitize(float duration)
        {
            if (float.IsNaN(duration) || duration <= 0f) return DefaultDuration;

            return Mathf.Min(duration, MaxDuration);
        }

        private GameObject CreatePieceObject()
        {
            var go = new GameObject("ShatterPiece");

            go.layer = RenderOrder.OverlayLayer;

            if (_root != null) go.transform.SetParent(_root, false);

            go.AddComponent<SpriteRenderer>();
            go.SetActive(false);

            return go;
        }
    }
}
