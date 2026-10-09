using System.Collections.Generic;
using DeepseaOil.Foundation;
using UnityEngine;

namespace DeepseaOil.Presentation.Effects.Drivers
{
    /// <summary>粒子驱动，管理一个 EffectId 对应粒子预制体的播放、回收与池化</summary>
    /// <remarks>单例型重复 Play 合并到当前实例（取最大强度、不重置计时），多实例型每次新建。回收：按估算时长计时，到点问一次 IsAlive(false)，超 +5s 强制回收。强度乘数属作者，须存下来只做相对缩放，见 _authoredSizeMul。池满走 CreateOrDrop，丢弃并节流警告，不用 DropSilently（未预热时会连第一次 Play 一起丢）。位置写世界坐标并保留预制体自带 z，缩放 = 预制体缩放 × ctx.Scale，不按 ctx.Direction 旋转。</remarks>
    public sealed class ParticleDriver : IEffectDriver
    {
        /// <summary>Intensity=0 时粒子量与大小的缩放，相对预制体作者值，1 时原样播</summary>
        private const float MinIntensityScale = 0.4f;

        /// <summary>估算时长之后最多再等多久强制回收，防粒子寿命无限占死池位</summary>
        private const float MaxLifeOverrun = 5f;

        private const float DropWarnInterval = 1f;

        private readonly string _assetKey;
        private readonly string _tag;
        private readonly bool _isSingleton;
        private readonly Transform _root;
        private readonly int _maxSize;
        private readonly int _prewarm;

        // 运行期状态
        private readonly Dictionary<int, ParticleInstance> _active = new Dictionary<int, ParticleInstance>();
        private readonly List<int> _recycleScratch = new List<int>();

        private GameObject _prefab;
        private Vector3 _prefabScale = Vector3.one;

        /// <summary>预制体上作者授权的乘数</summary>
        /// <remarks>必须存下来：直接写 = k 会把 Curve 模式下的乘数抹成 1，只做 作者值 × k。</remarks>
        private float[] _authoredSizeMul;
        private float[] _authoredRateMul;
        private bool _warnedAuthoredMismatch;
        private Pool<GameObject> _pool;
        private ParticleInstance _singleton;
        private int _nextInstanceId;
        private int _epoch = 1;

        private int _droppedSinceWarn;
        private float _nextDropWarnTime;
        private bool _warnedNoParticleSystem;
        private bool _warnedLifeOverrun;
        private bool _disposed;

        /// <summary>构造粒子效果</summary>
        public ParticleDriver(
            GameObject prefab,
            Transform root,
            bool isSingleton = false,
            int maxSize = 16,
            int prewarm = 0,
            string assetKey = null)
        {
            _root = root;
            _isSingleton = isSingleton;
            _maxSize = maxSize > 0 ? maxSize : 1;
            _prewarm = prewarm < 0 ? 0 : prewarm;
            _assetKey = assetKey;

            string name = prefab != null ? prefab.name : assetKey;
            _tag = "ParticleDriver[" + (string.IsNullOrEmpty(name) ? "?" : name) + "]";

            if (prefab != null) BuildPool(prefab);
        }

        /// <summary>从装配表建驱动，资源稍后到位</summary>
        internal ParticleDriver(in EffectSpec spec, Transform root)
            : this(null, root, spec.IsSingleton, spec.MaxSize, spec.Prewarm, spec.Key)
        {
        }

        public bool IsSingleton => _isSingleton;

        public string AssetKey => _assetKey;

        /// <summary>池建好了 = 资源到位</summary>
        public bool IsAssetReady => _pool != null;

        public int ActiveInstanceCount => _active.Count;

        public int PooledObjectCount => _pool != null ? _pool.IdleCount : 0;

        /// <summary>资源到位，拿到预制体后建池并按 prewarm 预热，幂等</summary>
        public void OnAssetLoaded(Object asset)
        {
            if (_disposed || _pool != null) return;

            var prefab = asset as GameObject;
            if (prefab == null)
            {
                Debug.LogError($"{_tag} 拿到的资源不是 GameObject（key={_assetKey}），该特效不可用。");
                return;
            }

            BuildPool(prefab);
        }

        public EffectHandle Play(EffectId id, in EffectContext ctx)
        {
            if (_disposed) return EffectHandle.None;

            if (_pool == null)
            {
                // EffectModule 会先拦资源未就位，走到这里说明装配方式不对
                Debug.LogError($"{_tag} 资源未就位就 Play 了 {id}。");
                return EffectHandle.None;
            }

            // 单例：已在播就合并到当前实例
            if (_isSingleton && _singleton != null)
            {
                if (ctx.Intensity > _singleton.Intensity) _singleton.Intensity = ctx.Intensity;
                return HandleOf(_singleton);
            }

            if (!_pool.TryGet(out GameObject go))
            {
                WarnPoolFull(id);
                return EffectHandle.None;
            }

            if (go == null)
            {
                Debug.LogError($"{_tag} 池返回了 null 对象（factory 有问题）。");
                return EffectHandle.None;
            }

            ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length == 0)
            {
                if (!_warnedNoParticleSystem)
                {
                    _warnedNoParticleSystem = true;
                    Debug.LogError($"{_tag} 预制体（含子物体）上没有 ParticleSystem，{id} 无法播放。" +
                                   $"请检查 Assets/Resources/{_assetKey}.prefab。本驱动只报一次。");
                }

                _pool.Release(go);
                return EffectHandle.None;
            }

            ApplyPlacement(go, in ctx);
            ApplyIntensity(systems, ctx.Intensity);

            // 池化复用：先清掉上一轮残留粒子再播
            for (int i = 0; i < systems.Length; i++)
            {
                systems[i].Clear(false);
                systems[i].Play(false);
            }

            var inst = new ParticleInstance
            {
                Id = ++_nextInstanceId,
                Epoch = _epoch,
                Go = go,
                Systems = systems,
                Follow = ctx.Follow,
                FollowRequested = ctx.FollowRequested,
                Duration = EstimateDuration(systems),
                Intensity = ctx.Intensity,
            };

            _active[inst.Id] = inst;
            if (_isSingleton) _singleton = inst;

            return HandleOf(inst);
        }

        public void Stop(EffectHandle handle)
        {
            if (!handle.IsValid) return;

            // CleanAll 之前发出的句柄：实例早已回收
            if (handle.Generation != _epoch) return;

            if (!_active.TryGetValue(handle.Id, out ParticleInstance inst)) return;

            Recycle(inst);
        }

        public void CleanAll()
        {
            // 让此前发出的句柄全部失效
            _epoch++;

            if (_active.Count > 0)
            {
                _recycleScratch.Clear();
                foreach (KeyValuePair<int, ParticleInstance> kv in _active)
                {
                    _recycleScratch.Add(kv.Key);
                }

                for (int i = 0; i < _recycleScratch.Count; i++)
                {
                    if (_active.TryGetValue(_recycleScratch[i], out ParticleInstance inst)) Recycle(inst);
                }
            }

            _singleton = null;
        }

        public void Tick(float dt)
        {
            if (_active.Count == 0) return;

            _recycleScratch.Clear();

            foreach (KeyValuePair<int, ParticleInstance> kv in _active)
            {
                ParticleInstance inst = kv.Value;
                inst.Elapsed += dt;

                if (inst.FollowRequested)
                {
                    if (inst.Follow == null)
                    {
                        // 跟随目标被销毁（Unity 假 null）：立刻回收，否则会飘在原地
                        _recycleScratch.Add(inst.Id);
                        continue;
                    }

                    FollowTarget(inst);
                }

                if (IsFinished(inst)) _recycleScratch.Add(inst.Id);
            }

            for (int i = 0; i < _recycleScratch.Count; i++)
            {
                if (_active.TryGetValue(_recycleScratch[i], out ParticleInstance inst)) Recycle(inst);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CleanAll();

            _pool?.Dispose();
            _pool = null;
            _prefab = null;
        }

        private void BuildPool(GameObject prefab)
        {
            _prefab = prefab;
            _prefabScale = prefab.transform.localScale;

            // 趁预制体还没被写过，抓一次作者的乘数
            ParticleSystem[] authored = prefab.GetComponentsInChildren<ParticleSystem>(true);
            _authoredSizeMul = new float[authored.Length];
            _authoredRateMul = new float[authored.Length];

            for (int i = 0; i < authored.Length; i++)
            {
                _authoredSizeMul[i] = authored[i].main.startSizeMultiplier;
                _authoredRateMul[i] = authored[i].emission.rateOverTimeMultiplier;
            }

            _pool = new Pool<GameObject>(
                factory: () =>
                {
                    GameObject go = Object.Instantiate(prefab, _root);
                    go.SetActive(false);
                    return go;
                },
                onGet: go => go.SetActive(true),
                onRelease: go => go.SetActive(false),
                name: _tag,
                maxSize: _maxSize,
                overflowPolicy: PoolOverflowPolicy.CreateOrDrop,
                onDestroy: null);   // 实例都是特效根子物体，销毁根即回收

            if (_prewarm > 0) _pool.Prewarm(_prewarm);
        }

        private void ApplyPlacement(GameObject go, in EffectContext ctx)
        {
            Transform t = go.transform;
            Vector2 p = ctx.Position;

            // 保留预制体自带的 z（2D 排序看 Sorting Layer），抹成 0 是隐性坑
            t.position = new Vector3(p.x, p.y, t.position.z);
            t.localScale = _prefabScale * ctx.Scale;
        }

        private static void FollowTarget(ParticleInstance inst)
        {
            Transform t = inst.Go.transform;
            Vector3 f = inst.Follow.position;
            t.position = new Vector3(f.x, f.y, t.position.z);
        }

        /// <summary>强度映射，按作者值相对缩放</summary>
        /// <remarks>乘数属性对所有 MinMaxCurve 模式都合法，但属作者：写 = k 会抹成 1；startSize.curveMultiplier 刻意不碰，两者相乘都乘 k 会变 k²。实例系统数与预制体不一致时宁可不缩放也不写坏作者值，只报一次警告。</remarks>
        private void ApplyIntensity(ParticleSystem[] systems, float intensity)
        {
            float k = Mathf.Lerp(MinIntensityScale, 1f, Mathf.Clamp01(intensity));

            if (_authoredSizeMul == null || _authoredSizeMul.Length != systems.Length)
            {
                if (!_warnedAuthoredMismatch)
                {
                    _warnedAuthoredMismatch = true;
                    Debug.LogWarning($"{_tag} 实例的粒子系统数（{systems.Length}）与预制体（" +
                                     $"{(_authoredSizeMul == null ? 0 : _authoredSizeMul.Length)}）不一致，" +
                                     "本次不做强度缩放（保留预制体原样），以免写坏作者值。");
                }

                return;
            }

            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.MainModule main = systems[i].main;
                main.startSizeMultiplier = _authoredSizeMul[i] * k;

                ParticleSystem.EmissionModule emission = systems[i].emission;
                emission.rateOverTimeMultiplier = _authoredRateMul[i] * k;
            }
        }

        /// <summary>估算一次播放总时长（秒），各系统 duration + startLifetime 的最大值</summary>
        /// <remarks>任一系统是循环型就返回 +∞，靠 Stop / CleanAll / 跟随丢失回收</remarks>
        private static float EstimateDuration(ParticleSystem[] systems)
        {
            float max = 0f;

            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.MainModule main = systems[i].main;

                if (main.loop) return float.PositiveInfinity;

                float lifetime;
                switch (main.startLifetime.mode)
                {
                    case ParticleSystemCurveMode.Constant:
                        lifetime = main.startLifetime.constant;
                        break;

                    case ParticleSystemCurveMode.TwoConstants:
                        lifetime = main.startLifetime.constantMax;
                        break;

                    default:
                        // Curve / TwoCurves：curveMultiplier 是乘数不是峰值，可能偏小，到期后还会问一次 IsAlive，不会提前掐断
                        lifetime = main.startLifetime.curveMultiplier;
                        break;
                }

                float total = main.duration + Mathf.Max(0f, lifetime);
                if (total > max) max = total;
            }

            return max;
        }

        private bool IsFinished(ParticleInstance inst)
        {
            if (inst.Go == null || inst.Systems == null) return true;

            // 还没到估算时长就不问引擎（IsAlive 遍历粒子）
            if (inst.Elapsed < inst.Duration) return false;

            if (inst.Elapsed < inst.Duration + MaxLifeOverrun && HasLiveParticles(inst)) return false;

            if (!_warnedLifeOverrun && inst.Elapsed >= inst.Duration + MaxLifeOverrun)
            {
                _warnedLifeOverrun = true;
                Debug.LogWarning($"{_tag} 有实例超过估算时长 {MaxLifeOverrun}s 仍未结束，已强制回收。" +
                                 "常见原因：粒子的 Start Lifetime 设成了无限。");
            }

            return true;
        }

        /// <summary>实例里是否还有活粒子，用 IsAlive(false) 逐个问（不递归，避免重复计数）</summary>
        private static bool HasLiveParticles(ParticleInstance inst)
        {
            ParticleSystem[] systems = inst.Systems;

            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps != null && ps.IsAlive(false)) return true;
            }

            return false;
        }

        private void Recycle(ParticleInstance inst)
        {
            _active.Remove(inst.Id);

            if (ReferenceEquals(_singleton, inst)) _singleton = null;

            if (inst.Systems != null)
            {
                // 归还前停干净，否则复用时能看到上一轮的残留粒子
                for (int i = 0; i < inst.Systems.Length; i++)
                {
                    ParticleSystem ps = inst.Systems[i];
                    if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                inst.Systems = null;
            }

            if (inst.Go != null && _pool != null)
            {
                _pool.Release(inst.Go);
            }
        }

        private EffectHandle HandleOf(ParticleInstance inst) => new EffectHandle(inst.Id, inst.Epoch, this);

        private void WarnPoolFull(EffectId id)
        {
            _droppedSinceWarn++;

            float now = Time.unscaledTime;
            if (now < _nextDropWarnTime) return;

            _nextDropWarnTime = now + DropWarnInterval;
            Debug.LogWarning($"{_tag} 池已满（上限 {_maxSize}），丢弃 {id} 的播放请求。" +
                             $"距上次警告累计丢弃 {_droppedSinceWarn} 次——调大 EffectCatalog 里这一行的 maxSize 即可。");
            _droppedSinceWarn = 0;
        }

        /// <summary>一次播放的账本，用类而非结构体以便在字典里被 Tick 原地修改</summary>
        private sealed class ParticleInstance
        {
            public int Id;
            public int Epoch;
            public GameObject Go;
            public ParticleSystem[] Systems;
            public Transform Follow;
            public bool FollowRequested;
            public float Elapsed;
            public float Duration;
            public float Intensity;
        }
    }
}
