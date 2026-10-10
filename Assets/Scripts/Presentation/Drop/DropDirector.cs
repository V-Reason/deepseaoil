using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Drop;
using UnityEngine;

namespace DeepseaOil.Presentation.Drop
{
    // 掉落物调度器：造/持/驱/清，不判断掉落规则
    // 产出方只拿窄接口 IDropSpawner，由组合根驱动
    public sealed class DropDirector : IDropSpawner
    {
        private readonly List<DropActor> _drops = new List<DropActor>();

        private readonly Dictionary<DropType, DropSpec> _definitions = new Dictionary<DropType, DropSpec>();

        private Transform _root;
        private Transform _player;

        public int AliveCount => _drops.Count;

        public bool IsReady => _player != null;

        // root=null 建在场景根下；player=null 停用
        public void Attach(Transform root, Transform player)
        {
            _root = root;
            _player = player;

            _definitions.Clear();

            _definitions[DropType.Water] = ConfigModule.GetDrop(DropType.Water);
            _definitions[DropType.Earth] = ConfigModule.GetDrop(DropType.Earth);
            _definitions[DropType.Seed] = ConfigModule.GetDrop(DropType.Seed);
        }

        // 只有没接线是静默拒绝，其余都记日志
        public bool TrySpawn(in DropSpawnRequest request)
        {
            if (_player == null) return false;

            if (!_definitions.TryGetValue(request.Type, out DropSpec definition))
            {
                Debug.LogError($"[Drop] 没有 {request.Type} 的取值定义，这次产出被丢弃。");
                return false;
            }

            DropActor actor = CreateActor(request.Type);

            if (actor == null) return false;

            if (_root != null) actor.transform.SetParent(_root, true);

            actor.Initialize(in request, request.Type, definition, _player);

            _drops.Add(actor);

            return true;
        }

        public void Tick(float deltaTime)
        {
            // 倒序：正序删除会跳过下一个元素
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DropActor drop = _drops[i];

                if (drop == null)
                {
                    _drops.RemoveAt(i);
                    continue;
                }

                drop.Tick(deltaTime);

                if (drop.IsAlive) continue;

                drop.Dispose();
                _drops.RemoveAt(i);
            }
        }

        public void ClearAll()
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                if (_drops[i] != null) _drops[i].Dispose();
            }

            _drops.Clear();
        }

        private static DropActor CreateActor(DropType type)
        {
            var go = new GameObject($"Drop_{type}");

            switch (type)
            {
                case DropType.Water:
                case DropType.Earth:
                case DropType.Seed:
                    // 三种掉落物运动学一致，只有颜色与载荷不同（载荷由 OnDropCollected 裁决）
                    // 加一种掉落物 = DropType 成员 + 这一行 + CreateActor 一行
                    return go.AddComponent<WaterBallDrop>();

                default:
                    Debug.LogError($"[Drop] {type} 没有对应的掉落物实体，请在 CreateActor 里补一行。");
                    UnityEngine.Object.Destroy(go);
                    return null;
            }
        }
    }
}
