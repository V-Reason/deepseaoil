// ---------------------------------------------------------------------------
// 测试夹具 · 共享底座
//
// 【为什么单独一个文件】
//   `cfg.dso.*` 的生成行**只有 `JSONNode` 构造**（字段 `readonly`，没有参数构造、没有可续写的
//   第二个构造点），而收口后的取值口径是"包装中间类持有生成行引用"。于是"造一行"这件事在测试里
//   绕不开拼 JSON —— 把它收在这一个文件里，schema 耦合就只有一个落点（改表时改这里）。
//
// 【为什么需要 MotorProbe】
//   审查已定"把 Motor 开放出来、让 Logic 退化为组合件"：速度账本与控制律现在住在
//   `ActorLedger`（Logic 层）。本探针把它接到一个纯 C# 的速度字段上，于是
//   **测试里验的账本与线上跑的是同一份代码**，而 EditMode 不需要造 Rigidbody2D。
//
// 【本目录的硬约束】Assets/Tests/Runtime/Editor/ 的末级 `Editor` 保留为目录约定；
//   真正决定平台的是 DeepseaOil.Tests.EditMode.asmdef 的 includePlatforms: [Editor]。
// ---------------------------------------------------------------------------

using DeepseaOil.Data;
using DeepseaOil.Foundation;
using DeepseaOil.Logic.Movement;
using Luban.SimpleJSON;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Tests
{
    /// <summary>
    /// 生成行的工厂：用 JSON 字面量造 <c>cfg.dso.*</c> 行。
    /// </summary>
    /// <remarks>
    /// <b>每个键都必须出现。</b>缺键时 Luban 的 <c>JSONObject</c> 索引器返回一个惰性占位，
    /// 它的 <c>IsNumber</c> / <c>IsString</c> 都是 <c>false</c> ⇒ 行的构造会抛
    /// <c>SerializationException</c>（不是留 <c>null</c>）。枚举键写**数字**，不是成员名。
    /// </remarks>
    internal static class RowFactory
    {
        /// <summary>一行水球：元素四件 = 纯水（type 0 / temp 0 / wet 3 / cond 1 / tags 0）。</summary>
        /// <remarks>
        /// 抛物线与投掷距离（flight_duration / max_height / max_*_throw_distance）已从表移交
        /// <c>ThrowTuning</c> SO，表里不再有这四列 —— 用它们请给 <c>ProjectileSpec</c> 传真的调参实例。
        /// 落地切成什么状态由 <c>element_rule</c> 与地形元素合成后决定，<b>不再有 <c>tile_state</c> 列</b>（旧链路已作废）。
        /// </remarks>
        public static Projectile WaterRow()
            => new Projectile(JSON.Parse(
                "{\"id\":0,\"name\":\"纯水\"," +
                "\"type\":0,\"temp\":0,\"wet\":3,\"conductive\":1,\"tags\":0}"));

        /// <summary>一行敌人：显示名"测试敌人"、追击满速 3.6、耐久 3。半径 / 加速度 / 击退衰减 / 停止距离 / 脱战距离已搬进 <c>EnemyTuning</c> 调参 SO，表里不再有这些列。</summary>
        public static Enemy EnemyRow()
            => new Enemy(JSON.Parse("{\"id\":1,\"name\":\"测试敌人\",\"max_speed\":3.6,\"hp\":3}"));

        /// <summary>一行玩家：血量 3、接触伤害 1、无敌 0.8、重试 1.2、攻击间隔 0.5、击退 12/12、接触半径 1。</summary>
        public static Player PlayerRow()
            => new Player(JSON.Parse(
                "{\"id\":1,\"name\":\"玩家\",\"max_hp\":3,\"contact_damage\":1," +
                "\"invulnerable_duration\":0.8,\"retry_delay\":1.2,\"attack_interval\":0.5," +
                "\"knockback_impulse\":12,\"knockback_speed_limit\":12,\"contact_radius\":1}"));

        /// <summary>一行波次：4 只一波、间隔 0.25、开局等 1.5、清完等 2.5、出生半径 5。</summary>
        public static Wave WaveRow()
            => new Wave(JSON.Parse(
                "{\"id\":1,\"name\":\"默认\",\"enemies_per_wave\":4,\"spawn_interval\":0.25," +
                "\"initial_delay\":1.5,\"respawn_delay\":2.5,\"spawn_radius\":5}"));

        /// <summary>一行关卡初始格（状态 1 = 空地）。</summary>
        public static TileInitial TileInitialRow(int cellX, int cellY, int stateId)
            => new TileInitial(JSON.Parse(
                $"{{\"id\":1,\"cell_x\":{cellX},\"cell_y\":{cellY},\"state_id\":{stateId}}}"));
    }

    /// <summary>
    /// 不碰引擎的移动执行器探针：速度只存在一个字段里，账本与控制律复用生产实现。
    /// </summary>
    /// <remarks>
    /// <b>为什么它必须实现整个 <see cref="IActorMotor"/></b>：审查已定状态层与移动层注入的是执行器，
    /// 而账本住在执行器上 —— 于是"塞一个假执行器"就顺带把账本送进测试，
    /// 这正是控制律可被 EditMode 直接测的原因（<see cref="ActorLedger"/> 是 Logic 层的纯数学）。
    /// </remarks>
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

        /// <summary>引擎侧速度（<c>IMovementMotor.Velocity</c>：回读口）。</summary>
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

        /// <summary>角色共用运动参数（只写不读：状态机读的是 <see cref="Motion"/>）。</summary>
        public CharacterConfig Config => _ledger.Config;

        /// <summary>运动标量（<c>IStateHost</c> 的取数口；执行器折算的那一份）。</summary>
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
