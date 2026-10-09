using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Wave;
using UnityEngine;

namespace DeepseaOil.Presentation.Actor
{
    /// <summary>战斗调度：按波次生成敌人、逐只驱动、清场</summary>
    /// <remarks>敌人组合根；世界信息全经 Initialize 注入，计时与分支交给 WaveLogic。不挂 FixedUpdate，由组合根每物理帧调 FixedTick。存活数与波次唯一权威：HUD 读的 WaveChanged 由此发布。</remarks>
    public sealed class CombatDirector : MonoBehaviour
    {
        private WaveLogic _logic;
        private EnemySpec _enemySpec;
        private Transform _player;
        private Transform _actorRoot;
        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private readonly List<EnemyActor> _enemies = new List<EnemyActor>();
        private readonly List<WaveLogic.SpawnRequest> _spawnBuffer = new List<WaveLogic.SpawnRequest>();

        private int _publishedWave = -1;
        private int _publishedAlive = -1;

        public int AliveCount { get; private set; }

        /// <summary>列表里第一只存活敌人离玩家多远；无敌人时 -1</summary>
        public float FirstAliveEnemyDistance { get; private set; } = -1f;

        public Vector2 FirstAliveEnemyVelocity { get; private set; }

        /// <summary>组装调度器</summary>
        public void Initialize(
            PlayerController player,
            in WaveSpec waveSpec,
            EnemySpec enemySpec,
            GridLogic grid,
            EnemyCellRegistry registry,
            Transform actorRoot)
        {
            if (player == null || player.Logic == null)
            {
                Debug.LogError("CombatDirector 没有玩家引用（或玩家逻辑层没装配好），敌人不会生成，已停用。", this);
                enabled = false;
                return;
            }

            _player = player.transform;
            _enemySpec = enemySpec;
            _grid = grid;
            _registry = registry;
            _actorRoot = actorRoot;
            _logic = new WaveLogic(in waveSpec);

            PublishIfChanged();
        }

        /// <summary>清空全场敌人并停当前波次：不清则玩家复活立刻被原地敌人再打一次</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                if (_enemies[i] != null) Destroy(_enemies[i].gameObject);
            }

            _enemies.Clear();

            _logic?.Reset();

            AliveCount = 0;
            FirstAliveEnemyDistance = -1f;
            FirstAliveEnemyVelocity = Vector2.zero;

            PublishIfChanged();

            // 不静默：敌人全没了必须能追溯到一次清场。
            Debug.Log("[Combat] 敌人清场，等待下一波");
        }

        /// <summary>推进一个物理帧：驱动敌人→刷读数→跑波次</summary>
        /// <remarks>暂停时 timeScale 与 deltaTime 均 0；恢复那帧不补暂停期间欠的生成量</remarks>
        public void FixedTick(float now, float deltaTime)
        {
            if (_logic == null || _player == null) return;

            ClearDestroyed();

            for (int i = 0; i < _enemies.Count; i++)
            {
                EnemyActor enemy = _enemies[i];

                if (enemy == null || !enemy.IsAlive) continue;

                enemy.FixedTick(now, deltaTime);
            }

            AliveCount = CountAlive();
            UpdateReadouts();

            _logic.Tick(now, deltaTime, AliveCount > 0, PlayerPosition(), _spawnBuffer);

            for (int i = 0; i < _spawnBuffer.Count; i++)
            {
                SpawnOne(_spawnBuffer[i]);
            }

            PublishIfChanged();
        }

        /// <summary>按请求实例化一只敌人；预制体缺件是硬错误，当场取消本次生成，不做白模兜底</summary>
        /// <remarks>寻址约定与地块贴图同构：种类 id → enemies/Enemy_{id}（见 Docs/美术装配指南.md）。</remarks>
        private void SpawnOne(in WaveLogic.SpawnRequest request)
        {
            string prefabKey = $"enemies/Enemy_{_enemySpec.Id}";

            GameObject prefab = AssetModule.IsInitialized ? AssetModule.Load<GameObject>(prefabKey) : null;

            if (prefab == null)
            {
                Debug.LogError($"[Combat] 严重阻断：未找到敌人预制体 Assets/Resources/{prefabKey}.prefab！本次生成已取消。请先在 Unity 中创建该预制体。");
                return;
            }

            GameObject go = Instantiate(prefab, request.Position, Quaternion.identity, _actorRoot);
            go.name = $"Enemy_{request.WaveIndex}_{request.Remaining}";

            EnemyActor actor = go.GetComponent<EnemyActor>();

            if (actor == null)
            {
                Debug.LogError($"[Combat] 预制体 {prefabKey} 根节点未挂载 EnemyActor 组件！生成已作废。");
                Destroy(go);
                return;
            }

            actor.Initialize(
                request.Position,
                in _enemySpec,
                _player,
                PlayerPosition() - request.Position,
                _grid,
                _registry,
                _actorRoot);

            _enemies.Add(actor);
        }

        private void ClearDestroyed()
        {
            // 倒序删：正序会跳过紧挨的下一个元素，漏删不报错、表现为列表变长。
            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                if (_enemies[i] == null) _enemies.RemoveAt(i);
            }
        }

        private int CountAlive()
        {
            int alive = 0;

            for (int i = 0; i < _enemies.Count; i++)
            {
                EnemyActor enemy = _enemies[i];

                if (enemy != null && enemy.IsAlive) alive++;
            }

            return alive;
        }

        private void UpdateReadouts()
        {
            FirstAliveEnemyDistance = -1f;
            FirstAliveEnemyVelocity = Vector2.zero;

            for (int i = 0; i < _enemies.Count; i++)
            {
                EnemyActor enemy = _enemies[i];

                if (enemy == null || !enemy.IsAlive) continue;

                FirstAliveEnemyDistance = Vector2.Distance(enemy.Position, PlayerPosition());
                FirstAliveEnemyVelocity = enemy.EngineVelocity;

                return;
            }
        }

        /// <summary>只在波次或存活数真的变了时发布，免每帧刷事件</summary>
        private void PublishIfChanged()
        {
            int wave = _logic != null ? _logic.WaveIndex : 0;

            if (wave == _publishedWave && AliveCount == _publishedAlive) return;

            _publishedWave = wave;
            _publishedAlive = AliveCount;

            EventBus<WaveChanged>.Publish(new WaveChanged(wave, AliveCount));
        }

        public void Announce()
        {
            _publishedWave = -1;
            _publishedAlive = -1;

            PublishIfChanged();
        }

        private Vector2 PlayerPosition()
        {
            Vector3 p = _player.position;

            return new Vector2(p.x, p.y);
        }
    }
}
