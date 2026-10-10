// ---------------------------------------------------------------------------
// 测试夹具 · 共享底座
//
// 【为什么单独一个文件】
//   `cfg.dso.*` 的生成行**只有 `JSONNode` 构造**（字段 `readonly`，没有参数构造、没有可续写的
//   第二个构造点），而收口后的取值口径是"包装中间类持有生成行引用"。于是"造一行"这件事在测试里
//   绕不开拼 JSON —— 把它收在这一个文件里，schema 耦合就只有一个落点（改表时改这里）。
//
// 【本目录的硬约束】Assets/Tests/Runtime/Editor/ 的末级 `Editor` 保留为目录约定；
//   真正决定平台的是 DeepseaOil.Tests.EditMode.asmdef 的 includePlatforms: [Editor]。
// ---------------------------------------------------------------------------

using DeepseaOil.Data;
using DeepseaOil.Foundation;
using DeepseaOil.Logic.Movement;
using Luban.SimpleJSON;
using NUnit.Framework;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Tests
{
    /// <summary>生成行的工厂：用 JSON 字面量造 cfg.dso.* 行</summary>
    /// <remarks>每个键都必须出现。缺键时 Luban 的 JSONObject 索引器返回一个惰性占位，它的 IsNumber / IsString 都是 false ⇒ 行的构造会抛 SerializationException（不是留 null）。枚举键写数字，不是成员名。</remarks>
    internal static class RowFactory
    {
        /// <summary>一行水球：id 本身就是球种（BallType），配显示名，只有这两列。</summary>
        /// <remarks>抛物线与投掷距离（flight_duration / max_height / max_*_throw_distance）已从表移交 ThrowTuning SO，用它们请给 ProjectileSpec 传真的调参实例。落地切成什么状态由 element_rule 决定。</remarks>
        public static Projectile WaterRow()
            => new Projectile(JSON.Parse("{\"id\":0,\"name\":\"纯水\"}"));

        /// <summary>一行敌人：显示名与数值见下；半径 / 加速度 / 击退衰减 / 停止距离 / 脱战距离已搬进 EnemyTuning 调参 SO。</summary>
        public static Enemy EnemyRow(int id = 1, int hp = 3, int contactDamage = 1)
            => new Enemy(JSON.Parse(
                $"{{\"id\":{id},\"name\":\"测试敌人\",\"max_speed\":3.6,\"hp\":{hp},\"contact_damage\":{contactDamage}}}"));

        /// <summary>一行玩家：字段与默认值见下，受击 / 投掷 / 回血三条链用到的列全在。</summary>
        public static Player PlayerRow()
            => new Player(JSON.Parse(
                "{\"id\":1,\"name\":\"玩家\",\"max_hp\":3,\"contact_damage\":1," +
                "\"invulnerable_duration\":0.8,\"retry_delay\":1.2,\"attack_interval\":0.35," +
                "\"knockback_impulse\":12,\"knockback_speed_limit\":12,\"contact_radius\":1," +
                "\"water_capacity\":5,\"water_start\":5,\"earth_capacity\":5,\"earth_start\":5," +
                "\"life_heal_interval\":3}"));

        /// <summary>一行波次：三阶段时长与出生参数见下，配给火种子。</summary>
        public static Wave WaveRow(int id = 1, int enemies = 6, int grantSeed = 1)
            => new Wave(JSON.Parse(
                $"{{\"id\":{id},\"name\":\"测试波次\",\"prep_time\":10,\"battle_time\":30,\"settle_time\":8," +
                $"\"enemies_per_wave\":{enemies},\"spawn_interval\":0.4,\"spawn_radius\":5,\"grant_seed\":{grantSeed}}}"));

        /// <summary>一行地块状态；默认是一个"永久、不减速、无伤害、不导通"的空地。</summary>
        public static TileState TileStateRow(
            int id,
            float duration = -1f,
            float slowRate = 1f,
            int dotDamage = 0,
            bool isObstacle = false,
            bool isConductor = false,
            string name = "测试地块")
            => new TileState(JSON.Parse(
                $"{{\"id\":{id},\"name\":\"{name}\",\"duration\":{duration},\"slow_rate\":{slowRate}," +
                $"\"dot_damage\":{dotDamage},\"is_obstacle\":{(isObstacle ? "true" : "false")}," +
                $"\"is_conductor\":{(isConductor ? "true" : "false")}}}"));

        /// <summary>一行元素反应规则：原格地貌 + 球种 → 结果地貌与落地冲击；radius 默认 1 格（只打本格）。</summary>
        public static ElementRule ElementRuleRow(
            int id,
            int sourceTile,
            int ballType,
            int resultTile,
            int damage = 0,
            float knockback = 0f,
            float stun = 0f,
            bool triggerChain = false,
            float radius = 1f)
            => new ElementRule(JSON.Parse(
                $"{{\"id\":{id},\"source_tile\":{sourceTile},\"ball_type\":{ballType}," +
                $"\"result_tile\":{resultTile},\"impact_damage\":{damage},\"impact_knockback\":{knockback}," +
                $"\"impact_stun\":{stun},\"impact_radius\":{radius}," +
                $"\"trigger_chain\":{(triggerChain ? "true" : "false")}}}"));

        /// <summary>一行二级元素反应：两个发生器地貌 → 激发的产物与波及。</summary>
        public static ElementDuoReaction DuoRow(
            int id,
            int elemA,
            int elemB,
            int resultTile,
            int damage = 0,
            float knockback = 0f,
            float duration = 3f,
            float radius = 1f,
            bool triggerChain = true)
            => new ElementDuoReaction(JSON.Parse(
                $"{{\"id\":{id},\"elem_a\":{elemA},\"elem_b\":{elemB},\"result_tile\":{resultTile}," +
                $"\"impact_damage\":{damage},\"impact_knockback\":{knockback}," +
                $"\"result_duration\":{duration},\"effect_radius\":{radius}," +
                $"\"trigger_chain\":{(triggerChain ? "true" : "false")}}}"));

        /// <summary>一行种子基建配置。</summary>
        public static Seed SeedRow(int id, int spawnTile, string name = "测试种子", string iconKey = "Icons/Seed_Test")
            => new Seed(JSON.Parse(
                $"{{\"id\":{id},\"name\":\"{name}\",\"spawn_tile\":{spawnTile},\"icon_key\":\"{iconKey}\"}}"));

        /// <summary>一行关卡初始格（状态 1 = 空地）。</summary>
        public static TileInitial TileInitialRow(int cellX, int cellY, int stateId)
            => new TileInitial(JSON.Parse(
                $"{{\"id\":1,\"cell_x\":{cellX},\"cell_y\":{cellY},\"state_id\":{stateId}}}"));

        // 下面两个是**包装件**工厂：求解器与二级反应查询表只吃 Spec，不吃生成行

        /// <summary>一条元素反应规则（已包装）。</summary>
        public static ElementRuleSpec ElementRuleSpecOf(
            int id,
            int sourceTile,
            int ballType,
            int resultTile,
            int damage = 0,
            float knockback = 0f,
            float stun = 0f,
            bool triggerChain = false,
            float radius = 1f)
            => new ElementRuleSpec(
                ElementRuleRow(id, sourceTile, ballType, resultTile, damage, knockback, stun, triggerChain, radius));

        /// <summary>一条二级元素反应（已包装）。</summary>
        public static DuoReactionSpec DuoSpecOf(
            int id,
            int elemA,
            int elemB,
            int resultTile,
            int damage = 0,
            float knockback = 0f,
            float duration = 3f,
            float radius = 1f,
            bool triggerChain = true)
            => new DuoReactionSpec(
                DuoRow(id, elemA, elemB, resultTile, damage, knockback, duration, radius, triggerChain));
    }

    /// <summary>夹具自检：表加了列而这里没跟上时先红，而不是让一堆无关用例一起抛 Luban 反序列化异常</summary>
    public class 夹具自检Tests
    {
        [Test]
        public void 每个夹具行都合当前表结构()
        {
            Assert.DoesNotThrow(() => RowFactory.WaterRow(), "projectile 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.EnemyRow(), "enemy 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.PlayerRow(), "player 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.WaveRow(), "wave 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.TileStateRow(1), "tile_state 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.ElementRuleRow(1, 1, 0, 2), "element_rule 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.DuoRow(1, 2, 3, 8), "element_duo_reaction 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.SeedRow(1, 15), "seed 行没跟上生成 schema");
            Assert.DoesNotThrow(() => RowFactory.TileInitialRow(0, 0, 1), "tile_initial 行没跟上生成 schema");
        }
    }

    /// <summary>不碰引擎的移动执行器探针：速度只存在一个字段里，账本与控制律复用生产实现</summary>
    /// <remarks>必须实现整个 IActorMotor：状态层与移动层注入的是执行器，而账本住在执行器上 —— 于是"塞一个假执行器"就顺带把账本送进测试，这正是控制律能被 EditMode 直接测的原因（ActorLedger 是 Logic 层的纯数学）。</remarks>
    internal class MotorProbe : IActorMotor
    {
        private Vector2 _velocity;
        private readonly ActorLedger _ledger;

        public MotorProbe()
        {
            _ledger = new ActorLedger(
                readVelocity: () => _velocity,
                writeVelocity: v => _velocity = v);
        }

        /// <summary>引擎侧速度（= 假物理体里的值）。</summary>
        public Vector2 EngineVelocity
        {
            get => _velocity;
            set => _velocity = value;
        }

        /// <summary>引擎侧速度（IMovementMotor.Velocity：回读口）。</summary>
        public Vector2 Velocity => _velocity;

        public Vector2 Position { get; set; }

        public Vector2 Facing { get; set; } = Vector2.right;

        /// <summary>被写入引擎的次数（诊断用；不要用它当"行为"的判据）。</summary>
        public int MoveCallCount { get; private set; }

        public void Move(Vector2 velocity)
        {
            // 模拟"物理步已结算"：下一帧帧首读到的就是这个值。
            _velocity = velocity;
            MoveCallCount++;
        }

        public void SetPosition(Vector2 position) => Position = position;

        // ── IActorLedger：全部转发给真账本 ──

        /// <summary>角色共用运动参数（只写不读：状态机读的是 Motion）。</summary>
        public CharacterConfig Config => _ledger.Config;

        /// <summary>运动标量（IStateHost 的取数口；执行器折算的那一份）。</summary>
        public MotionParams Motion => _ledger.Motion;

        public Vector2 FrameStartVelocity => _ledger.FrameStartVelocity;

        public Vector2 SubmittedDelta => _ledger.SubmittedDelta;

        Vector2 IActorLedger.Velocity => _ledger.Velocity;

        public float SpeedScale
        {
            get => _ledger.SpeedScale;
            set => _ledger.SpeedScale = value;
        }

        public void Configure(CharacterConfig config) => _ledger.Configure(config);

        public void BeginStep(float now, float deltaTime) => _ledger.BeginStep(now, deltaTime);

        public void Commit() => _ledger.Commit();

        public void AddImpulse(Vector2 deltaVelocity) => _ledger.AddImpulse(deltaVelocity);

        public void AddForce(Vector2 acceleration) => _ledger.AddForce(acceleration);

        public void SetVelocity(Vector2 velocity) => _ledger.SetVelocity(velocity);

        // ── 控制律 ──

        public void SnapVelocity(Vector2 velocity)
        {
            _ledger.SnapVelocity(velocity, v => FaceTowards(new Vector2(v.x, 0f)));
        }

        public void FaceTowards(Vector2 direction)
        {
            if (direction.sqrMagnitude <= 0f) return;

            Facing = direction;
        }

        public void MoveTowards(Vector2 direction, float speed)
        {
            FaceTowards(direction);
            _ledger.MoveTowards(direction, speed);
        }

        public void BrakeTowards() => _ledger.BrakeTowards();

        public void StopMove()
        {
            _ledger.StopMove();
            FaceTowards(Facing);   // 急停不改朝向
        }

        public void MoveDirection(Vector2 direction, float speed)
        {
            FaceTowards(direction);
            _ledger.MoveDirection(direction, speed);
        }
    }
}
