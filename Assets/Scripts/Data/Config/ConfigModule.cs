using System;
using System.Collections.Generic;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>数值配置模块，Data 层唯一取值入口：调用方只用 GetXxx 与包装件，数据来自表还是 SO 不外露</summary>
    public static class ConfigModule
    {
        private const string PlayerConfigKey = "config/PlayerConfig";

        /// <summary>关键表主键默认值</summary>
        public static class Ids
        {
            public const int Player = 1;

            public const int Enemy = 1;

            public const int Wave = 1;
        }

        private static TablesHolder _holder;
        private static bool _ready;
        private static bool _bound;

        private static PlayerConfig _playerConfig;
        private static ThrowTuning _throwTuning;
        private static DropTuning _dropTuning;
        private static VisualPalette _visuals;
        private static EnemyTuning _enemyTuning;

        /// <summary>元素反应规则，装配期折成 (原格, 球种) 哈希表；此后只读</summary>
        private static IReadOnlyList<ElementRuleSpec> _elementRules;

        /// <summary>二级元素反应查询表，装配期建一次；此后只读</summary>
        private static DuoReactionCatalog _duoReactions;

        /// <summary>种子基建配置，种子号→包装件；装配期装一次，此后只读</summary>
        private static Dictionary<SeedType, SeedSpec> _seeds;

        public static bool IsReady => _ready;

        public static bool AreAssetsBound => _bound;

        /// <summary>初始化第一段只读表：必须早于 AssetModule.Init，重复调用抛异常，jsonRoot 是 Luban 导出的 JSON 目录</summary>
        public static void Init(string jsonRoot)
        {
            if (_ready)
                throw new InvalidOperationException("[Config] ConfigModule.Init called twice");

            if (string.IsNullOrEmpty(jsonRoot))
                throw new ConfigLoadException("[Config] jsonRoot is null or empty");

            if (!System.IO.Directory.Exists(jsonRoot))
                throw new ConfigLoadException($"[Config] jsonRoot not found: {jsonRoot}");

            try
            {
                _holder = new TablesHolder(jsonRoot);
            }
            catch (Exception e)
            {
                // 一切异常统一包成 ConfigLoadException：让 GameRoot 能区分配置问题与代码问题。
                throw new ConfigLoadException($"[Config] load failed: {e.Message}", e);
            }

            if (!StartupValidator.Validate(_holder))
            {
                _holder = null;
                throw new ConfigLoadException("[Config] startup validation failed");
            }

            _ready = true;
            Debug.Log($"[Config] initialized, tables loaded from: {jsonRoot}");
        }

        public static void InitFromStreamingAssets()
        {
            Init(System.IO.Path.Combine(Application.streamingAssetsPath, "Luban"));
        }

        /// <summary>绑定 SO 资产第二段：必须在 AssetModule.Init 之后，重复调用是 no-op</summary>
        /// <remarks>资产缺失炸在启动期：PlayerConfig 缺失是硬错误（否则玩家速度为 0），三份观感调参缺失只报警告并走字段默认值</remarks>
        public static void BindAssets()
        {
            EnsureReady();

            if (_bound) return;

            if (!AssetModule.IsInitialized)
            {
                Debug.LogError(
                    "[Config] BindAssets 在 AssetModule.Init 之前被调用：SO 读不到，已跳过。" +
                    "调用顺序必须是 ConfigModule.Init → AssetModule.Init → ConfigModule.BindAssets。");
                return;
            }

            _playerConfig = AssetModule.Load<PlayerConfig>(PlayerConfigKey);

            if (_playerConfig == null)
            {
                Debug.LogError(
                    $"[Config] 取不到 {PlayerConfigKey}（期望 Assets/Resources/{PlayerConfigKey}.asset）：" +
                    "玩家移动参数会全部是字段默认值（字段级默认值不是策划填的那一套）。");
            }

            _throwTuning = ThrowTuning.LoadOrDefault();
            _dropTuning = DropTuning.LoadOrDefault();
            _visuals = VisualPalette.LoadOrDefault();

            // enemy 表已不存这些值：缺了没有字段默认值可退，会静默改变手感，故当场抛
            _enemyTuning = AssetModule.Load<EnemyTuning>(EnemyTuning.ResourceKey);

            if (_enemyTuning == null)
            {
                throw new ConfigLoadException(
                    $"[Config] 取不到 {EnemyTuning.ResourceKey}（期望 Assets/Resources/{EnemyTuning.ResourceKey}.asset）：" +
                    "敌人的判定半径 / 加速度 / 击退衰减 / 停止距离 / 脱战距离全在这份 SO 里，表里已经没有这些值可退。");
            }

            _bound = true;

            // 启动期走一遍：表里少一行应在这里炸，而不是等第一次投掷
            ProjectileSpec water = GetBall(BallType.Water);

            if (water == null)
                throw new ConfigLoadException($"[Config] projectile 表里没有球种 {BallType.Water}，投掷链无法工作");

            _ = GetPlayer();
            _ = GetEnemy();
            _ = GetWave();
            _ = GetDrop();

            // 元素层与种子表也在启动期走一遍：少一行、少一条规则在这里炸，而不是等第一次投掷
            _ = GetElementRules();
            _ = GetDuoReactions();
            _ = GetAllSeeds();

            // 精英怪是预留行：表里有就必须折得出来（多敌种刷怪接上前不能静默缺数据）
            _ = GetEnemy(2);
        }

        // 玩法数值查询（包装件：表行 ＋ SO）。边界：id 不存在时 Luban 的 Get 抛异常这里不 catch —— 配置事故应在启动期炸出来

        /// <summary>读一个球种（表行＋投掷调参），表中不存在时返回 null</summary>
        public static ProjectileSpec GetBall(BallType type)
        {
            EnsureAssets();

            if (!_holder.Tables.TbProjectile.DataMap.TryGetValue(type, out Projectile row)) return null;

            return new ProjectileSpec(row, _throwTuning);
        }

        public static IReadOnlyList<ProjectileSpec> GetAllBalls()
        {
            EnsureAssets();

            IReadOnlyList<Projectile> rows = _holder.Tables.TbProjectile.DataList;

            var result = new List<ProjectileSpec>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                result.Add(new ProjectileSpec(rows[i], _throwTuning));
            }

            return result;
        }

        /// <summary>读一个格子状态，表里没有这一行时抛异常：配置事故应在启动期炸出来</summary>
        public static TileStateSpec GetTileState(TileStateType id)
        {
            EnsureAssets();

            return new TileStateSpec(_holder.Tables.TbTileState.Get(id));
        }

        /// <summary>读一个格子状态，表里没有这一行时返回 null 不抛异常：给按 ID 造状态的工厂判断该 ID 有没有实现</summary>
        public static TileStateSpec TryGetTileState(TileStateType id)
        {
            EnsureAssets();

            TileState row = _holder.Tables.TbTileState.GetOrDefault(id);

            return row != null ? new TileStateSpec(row) : null;
        }

        public static IReadOnlyList<TileStateSpec> GetAllTileStates()
        {
            EnsureAssets();

            IReadOnlyList<TileState> rows = _holder.Tables.TbTileState.DataList;

            var result = new List<TileStateSpec>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                result.Add(new TileStateSpec(rows[i]));
            }

            return result;
        }

        /// <summary>全部元素反应规则（element_rule 表）；顺序无语义，查询由 ReactionResolver 的哈希表负责</summary>
        public static IReadOnlyList<ElementRuleSpec> GetElementRules()
        {
            EnsureAssets();

            if (_elementRules != null) return _elementRules;

            IReadOnlyList<ElementRule> rows = _holder.Tables.TbElementRule.DataList;

            var rules = new List<ElementRuleSpec>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                rules.Add(new ElementRuleSpec(rows[i]));
            }

            _elementRules = rules;

            return _elementRules;
        }

        /// <summary>二级元素跨界反应查询表；装配期建一次此后只读，调表后须重启</summary>
        public static DuoReactionCatalog GetDuoReactions()
        {
            EnsureAssets();

            if (_duoReactions != null) return _duoReactions;

            IReadOnlyList<ElementDuoReaction> rows = _holder.Tables.TbElementDuoReaction.DataList;

            var rules = new List<DuoReactionSpec>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                rules.Add(new DuoReactionSpec(rows[i]));
            }

            _duoReactions = new DuoReactionCatalog(rules);

            return _duoReactions;
        }

        /// <summary>读一个种子，表里没有这一行时返回 null 不抛异常</summary>
        public static SeedSpec GetSeed(SeedType id)
        {
            EnsureAssets();

            EnsureSeeds();

            return _seeds.TryGetValue(id, out SeedSpec spec) ? spec : null;
        }

        /// <summary>全部种子配置；返回新列表，装配期用一次即可</summary>
        public static IReadOnlyList<SeedSpec> GetAllSeeds()
        {
            EnsureAssets();

            EnsureSeeds();

            var result = new List<SeedSpec>(_seeds.Count);

            foreach (KeyValuePair<SeedType, SeedSpec> pair in _seeds)
            {
                result.Add(pair.Value);
            }

            return result;
        }

        /// <summary>读一个敌人种类：耐久来自 enemy 表，运动学与半径来自 EnemyTuning</summary>
        public static EnemySpec GetEnemy(int id = Ids.Enemy)
        {
            EnsureAssets();

            return new EnemySpec(_holder.Tables.TbEnemy.Get(id), _enemyTuning);
        }

        /// <summary>观感颜色表（SO），观感取值的唯一权威入口</summary>
        /// <remarks>永不返回 null：丢资产时给字段默认值实例＋Warning；球种色/敌人四态色/瞄准高亮两态色/贴地阴影色都从这里出去</remarks>
        public static VisualPalette Visuals
        {
            get
            {
                EnsureAssets();

                return _visuals;
            }
        }

        /// <summary>读玩家数值，"玩家"取值的唯一入口：移动参数（SO）与水球射程都从这里出去</summary>
        public static PlayerSpec GetPlayer(int id = Ids.Player)
        {
            EnsureAssets();

            return new PlayerSpec(
                _holder.Tables.TbPlayer.Get(id),
                _playerConfig,
                GetBall(BallType.Water));
        }

        public static WaveSpec GetWave(int id = Ids.Wave)
        {
            EnsureAssets();

            return new WaveSpec(_holder.Tables.TbWave.Get(id));
        }

        /// <summary>全部波次配置，按表内顺序；WaveLogic 逐波推进，跑完循环回第 1 行</summary>
        public static IReadOnlyList<WaveSpec> GetWaves()
        {
            EnsureAssets();

            IReadOnlyList<Wave> rows = _holder.Tables.TbWave.DataList;

            var result = new List<WaveSpec>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                result.Add(new WaveSpec(rows[i]));
            }

            return result;
        }

        /// <summary>全部关卡初始格子状态，返回生成行供一次性遍历；表当前 0 行，不要因表空删链</summary>
        public static IReadOnlyList<TileInitial> GetTileInitials()
        {
            EnsureAssets();

            return _holder.Tables.TbTileInitial.DataList;
        }

        /// <summary>取一种掉落物的数值；种类没有独立数值行，全部共用 DropTuning，差异只在颜色与载荷</summary>
        public static DropSpec GetDrop(DropType type = DropType.Water)
        {
            EnsureAssets();

            return new DropSpec(_dropTuning, type);
        }

        // 逃生舱：特殊情况直接访问原始 Tables
        // 边界：只读；调用方不得跨帧持有该引用；新增消费必须登记在本注释里

        /// <summary>原始生成表（cfg.Tables），只给诊断用，正常取值一律走上面的 GetXxx</summary>
        /// <remarks>当前无登记破例。新增破例必须先登记在这里</remarks>
        public static cfg.Tables Tables
        {
            get
            {
                EnsureReady();

                return _holder.Tables;
            }
        }

        private static void EnsureReady()
        {
            if (!_ready)
                throw new InvalidOperationException("[Config] accessed before Init");
        }

        /// <summary>种子配置缓存，首次访问装一次</summary>
        private static void EnsureSeeds()
        {
            if (_seeds != null) return;

            IReadOnlyList<Seed> rows = _holder.Tables.TbSeed.DataList;

            _seeds = new Dictionary<SeedType, SeedSpec>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                _seeds[rows[i].Id] = new SeedSpec(rows[i]);
            }
        }

        /// <remarks>刻意分开报错：只报"没 Init"会把"忘了调 BindAssets"掩盖成同一现象，而两种装配错误的修法完全不同</remarks>
        private static void EnsureAssets()
        {
            EnsureReady();

            if (!_bound)
            {
                throw new InvalidOperationException(
                    "[Config] 玩法数值在 BindAssets 之前被读取：调用顺序必须是 " +
                    "ConfigModule.Init → AssetModule.Init → ConfigModule.BindAssets");
            }
        }
    }

    /// <summary>配置加载异常，独立定义让 GameRoot 能区分配置问题（重新导表）与代码问题</summary>
    public class ConfigLoadException : Exception
    {
        public ConfigLoadException(string message) : base(message) { }
        public ConfigLoadException(string message, Exception inner) : base(message, inner) { }
    }
}
