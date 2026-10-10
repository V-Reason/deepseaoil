using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Wave;
using UnityEngine;

namespace DeepseaOil.Presentation.Actor
{
    /// <summary>战斗调度：按波次三阶段生成敌人、逐只驱动、清场</summary>
    /// <remarks>由组合根每物理帧调 FixedTick（不挂 Update）；波次与存活数的唯一权威，HUD 读的 WaveChanged 由此发布；敌人预制体缺件是硬错误，取消生成不做白模兜底。</remarks>
    public sealed class CombatDirector : MonoBehaviour
    {
        private WaveLogic _logic;
        private EnemySpec _enemySpec;
        private Transform _player;
        private Transform _actorRoot;
        private GridLogic _grid;
        private EnemyCellRegistry _registry;

        private System.Action<EnemySpec, Vector2> _onKilled;

        private readonly List<EnemyActor> _enemies = new List<EnemyActor>();
        private readonly List<WaveLogic.SpawnRequest> _spawnBuffer = new List<WaveLogic.SpawnRequest>();

        private int _publishedWave = -1;
        private int _publishedAlive = -1;

        public int AliveCount { get; private set; }

        public WaveLogic Logic => _logic;

        public float FirstAliveEnemyDistance { get; private set; } = -1f;

        public Vector2 FirstAliveEnemyVelocity { get; private set; }

        public void Initialize(
            PlayerController player,
            IReadOnlyList<WaveSpec> waves,
            EnemySpec enemySpec,
            GridLogic grid,
            EnemyCellRegistry registry,
            Transform actorRoot,
            System.Action<EnemySpec, Vector2> onKilled = null)
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
            _onKilled = onKilled;
            _logic = new WaveLogic(waves);

            PublishIfChanged();
        }

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

            _publishedWave = -1;
            _publishedAlive = -1;

            PublishIfChanged();

            Debug.Log("[Combat] 敌人清场，等待下一波");
        }

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

            if (_onKilled != null) actor.Died = OnEnemyDied;

            _enemies.Add(actor);
        }

        private void OnEnemyDied(EnemyActor enemy)
        {
            _onKilled?.Invoke(_enemySpec, enemy.Position);
        }

        private void ClearDestroyed()
        {

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
