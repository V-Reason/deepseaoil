// ---------------------------------------------------------------------------
// 俯视角移动 · 运行期测试
//
// 【为什么在这里】Assets/Tests/Runtime/Editor/ —— 与 Data层Tests.cs 同机制：
//   被 DeepseaOil.Tests.EditMode.asmdef 覆盖，被测的各层由它的 references 显式引用。
//
// 【留什么】判据只有一条：这条用例守的是不是「改错了不报错、只表现为手感/观感不对」。
//   M1  零输入当帧停        —— 残留速度 = 滑行
//   M2  斜向不快 √2 倍      —— 俯视角最经典的 bug（喂**未归一化**的 (1,1)，让实现自己去归一化）
//   M3  反向无过渡          —— 有加速度就是惯性，与"零惯性"直接冲突
//   M4  零输入保持朝向      —— 站住时精灵自己翻面
//   M5  朝向只翻水平符号    —— 上下移动不该把精灵颠倒
//   M6  冲刺沿朝向 8 向     —— 冲刺方向 = 最近朝向（不再是固定 x 轴）
//   M7  冲刺走输入缓冲窗口  —— 框架件（InputBuffer）没被改坏
//   M10 边界钳位与未接线退化—— 钳位失效 = 走出地图；无条件 Clamp = 把玩家钉死在地图原点
//   M12 刚体速度真的写入    —— 端口实现的唯一职责
//   M13 抢占失败不改状态    —— Configure 必须跑在消费成功之后
//   M14 首帧同样参与抢占    —— 首帧不许是"无抢占"特权帧
//   M17 8 向吸附是纯函数    —— **唯一**覆盖 PlayerController 那条吸附/归一化通路的用例
//   M21 受击期间输入不接管  —— 受击帧的速度由门禁决定
//   M22 敌人执行器固化物理  —— 连续碰撞检测只有敌人需要
//
// 【加速度的坑】ActorLedger 的 Motion.MoveAcceleration 是 Configure 时一次性折算的快照
//   （CharacterConfig → MotionParams），**装配之后再改 SO 不生效**。
//   于是"改完配置再断言账本立刻跟上"那类用例断的是一个不存在的契约；要测惯性，
//   必须在构造 PlayerLogic 之前就把配置改好。
//
// 【覆盖边界，写在明处】除 M5 / M12 / M22 外，本文件测的都是 Logic 层：
//   它直接构造 PlayerLogic ＋ 假执行器，**不经过 PlayerController**。
//   于是"宿主把输入装配错了"这一类缺陷（未归一化、缓冲推了原始快照、边界没接线）
//   只有 M17 覆盖 —— 那正是"测试全绿但缺陷仍在"的成因。
//   PlayerMotor 已不含 groundCheck/wallCheck/groundMask：俯视角的阻挡由刚体碰撞解算，
//   逻辑层不需要"是否站地/是否贴墙"。**场景搭建与接线检验没有自动化**：
//   本文件绿了只代表 Logic 层对，不代表场景接对了。
//
// 跑法：Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// ---------------------------------------------------------------------------

using DeepseaOil.Data;
using DeepseaOil.Logic;
using DeepseaOil.Logic.Input;
using DeepseaOil.Logic.Movement;
using DeepseaOil.Logic.Player;
using DeepseaOil.Presentation;
using DeepseaOil.Presentation.Adapters;
using NUnit.Framework;
using UnityEngine;

namespace DeepseaOil.Tests
{
    public class 移动Tests
    {
        private PlayerConfig _config;
        private PlayerSpec _spec;
        private InputBuffer _buffer;
        private MotorProbe _motor;
        private PlayerLogic _logic;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<PlayerConfig>();
            _config.moveSpeed = 8f;
            _config.dashSpeed = 20f;
            _config.dashDuration = 0.2f;
            _config.dashCooldown = 1.5f;
            _config.dashBufferTime = 0.12f;
            _config.inputBufferTime = 0.12f;
            _config.snapToEightDirections = true;
            _config.extraForceScale = 0f;

            // 零惯性基准：本文件里绝大多数用例钉的是"当帧到位 / 松手当帧停"那套语义，
            // 而加速度现在是配置项（玩家资产填 60 = 有惯性）。要测惯性本身请在自己的用例里
            // 显式改这两个值 —— 但注意账本的快照在 PlayerLogic 构造时就固定了（见文件头 M19 的说明）。
            _config.moveAcceleration = 0f;
            _config.turnDecayRate = 0f;

            _spec = new PlayerSpec(RowFactory.PlayerRow(), _config, new ProjectileSpec(RowFactory.WaterRow(), null));

            _buffer = new InputBuffer(
                Mathf.Max(_config.inputBufferTime, _config.dashBufferTime),
                Mathf.RoundToInt(1f / 0.02f));

            _motor = new MotorProbe();
            _logic = new PlayerLogic(_motor, _spec, _buffer);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_config);
        }

        /// <summary>推进一个逻辑帧：帧首把假执行器速度归零，模拟"每帧被物理重新结算"（真实链路里 FixedTick 帧首读的是引擎速度；假执行器若保留上一帧的值，零提交帧会读到旧速度，断言就失真了）。</summary>
        private void Tick(Vector2 move, float now, bool dashPressed = false, bool resetVelocity = true)
        {
            if (resetVelocity) _motor.EngineVelocity = Vector2.zero;

            var snapshot = new InputSnapshot(move, dashPressed, false);
            var world = new WorldInfo(move, default(BoundsArea));

            _buffer.Push(in snapshot, now);
            _logic.FixedTick(new LogicContext(now, 0.02f, in world, in snapshot));
        }

        [Test]
        public void M1_零输入当帧停()
        {
            Tick(Vector2.zero, 0f);

            Assert.AreEqual(Vector2.zero, _motor.EngineVelocity, "零输入必须当帧停住，不得残留速度");
            Assert.AreEqual(MovementStateTag.Idle, _logic.MoveGroup.Current);
        }

        [Test]
        public void M2_斜向速度等于直向速度()
        {
            // 喂**未归一化**的 (1,1)：键盘同时按右与上就是这个值。
            // 归一化是实现的义务，不是测试的前提 —— 喂 0.7071 只能证明"已经归一化的输入能过"，
            // 证明不了"实现会归一化"：斜向快 √2 倍照样绿。
            Tick(new Vector2(1f, 1f), 0f);

            float expected = _config.moveSpeed * _config.moveSpeed;
            float actual = _motor.EngineVelocity.sqrMagnitude;

            // 容差 0.01：Vector2.normalized 与 magnitude 的浮点误差约 1.2e-3，
            // 而"未归一化"造成的偏差是 +64（(1,1) 会得到 2×speed²），量级差 4 个数量级，不会误判。
            Assert.AreEqual(expected, actual, 0.01f,
                $"斜向速度平方应为 {expected}（= moveSpeed²），实测 {actual}；接近 2×{expected} 即未归一化");
        }

        /// <summary>回归：8 向吸附是静态纯函数，喂未归一化输入也必须吐单位向量 —— 本文件里唯一覆盖 PlayerController 那条通路的用例。</summary>
        [Test]
        public void M17_八向吸附输出单位向量()
        {
            // ① 键盘斜向 (1,1) → 45° 档的单位向量
            Vector2 diagonal = PlayerController.SnapMoveToEightDirections(new Vector2(1f, 1f), true);
            Assert.AreEqual(1f, diagonal.magnitude, 1e-3f, $"键盘斜向应被归一化，实测模长 {diagonal.magnitude}");
            Assert.Greater(Vector2.Dot(diagonal, new Vector2(0.7071f, 0.7071f)), 0.999f, $"应吸附到 45°，实测 {diagonal}");

            // ② 直向与摇杆"轻推"必须得到同一个单位向量：吸附的语义是"取方向"，代价是丢掉模拟幅度。
            Vector2 fullPush = PlayerController.SnapMoveToEightDirections(Vector2.right, true);
            Vector2 lightPush = PlayerController.SnapMoveToEightDirections(new Vector2(0.2f, 0f), true);
            Assert.AreEqual(fullPush, lightPush, "摇杆轻推与推满必须是同一个速度：8 向吸附会抹掉模拟幅度");

            // ③ 关掉吸附时也不能原样放行：斜向 (1,1) 的模长是 √2，会当帧写出快 41% 的速度
            Vector2 unsnapped = PlayerController.SnapMoveToEightDirections(new Vector2(1f, 1f), false);
            Assert.AreEqual(1f, unsnapped.magnitude, 1e-3f, "关掉吸附只应关掉「吸到 45°」，不该顺带关掉归一化");

            // ④ 零输入恒为零向量：不得因归一化而放大成 NaN
            Assert.AreEqual(Vector2.zero, PlayerController.SnapMoveToEightDirections(Vector2.zero, true));
            Assert.AreEqual(Vector2.zero, PlayerController.SnapMoveToEightDirections(Vector2.zero, false));

            // ⑤ 吸附后的方向必须是"逐位相同"的常量：否则直线行走会逐帧抖动
            Assert.AreEqual(fullPush, PlayerController.SnapMoveToEightDirections(new Vector2(5f, 0f), true),
                "同一档位的不同输入必须得到同一个结果：否则直线行走会逐帧抖动");
        }

        [Test]
        public void M3_方向切换无惯性()
        {
            Tick(Vector2.right, 0f);
            Assert.AreEqual(_config.moveSpeed, _motor.EngineVelocity.x, 1e-3f, "向右一帧后速度应为 +moveSpeed");

            Tick(Vector2.left, 0.02f);
            Assert.AreEqual(-_config.moveSpeed, _motor.EngineVelocity.x, 1e-3f,
                "反向输入必须当帧变为 -moveSpeed，出现中间值即存在加速度/衰减");
        }

        [Test]
        public void M4_零输入保持朝向()
        {
            Tick(Vector2.left, 0f);
            Assert.Less(_motor.Facing.x, 0f, "向左移动后朝向应为左");

            Tick(Vector2.zero, 0.02f);
            Assert.Less(_motor.Facing.x, 0f, "站住后朝向不得被重置");
            Assert.AreEqual(MovementStateTag.Idle, _logic.MoveGroup.Current);
        }

        [Test]
        public void M5_朝向只翻水平符号()
        {
            var go = CreateMotorObject("移动测试_朝向", out PlayerMotor motor, out _);

            try
            {
                // 「朝左」与「纯竖直」的先后顺序是本用例的重点：先朝左再朝上，
                // 水平镜像必须保持朝左 —— value.x == 0 时取绝对值就会翻回朝右。
                motor.Facing = Vector2.left;
                Assert.Less(go.transform.localScale.x, 0f, "朝左应翻成负缩放");
                Assert.Greater(go.transform.localScale.y, 0f, "竖直缩放不得被翻转");

                motor.Facing = Vector2.up;
                Assert.Less(go.transform.localScale.x, 0f, "纯竖直朝向不得改动已有的水平镜像");
                Assert.AreEqual(Vector2.up, motor.Facing, "竖直朝向仍要记进 Facing（供 8 向动画用）");

                motor.Facing = Vector2.right;
                Assert.Greater(go.transform.localScale.x, 0f, "朝右应翻回正缩放");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void M6_冲刺沿朝向八向()
        {
            // 先建立斜向朝向：喂未归一化的 (1,1)，由实现自己归一到 45°
            Tick(new Vector2(1f, 1f), 0f);
            _motor.EngineVelocity = Vector2.zero;

            // 再触发冲刺（无输入 → 用最近朝向）
            Tick(Vector2.zero, 0.02f, dashPressed: true, resetVelocity: false);

            Assert.AreEqual(MovementStateTag.Dash, _logic.MoveGroup.Current, "有缓冲按下且冷却已过，应抢占到 Dash");

            Vector2 v = _motor.EngineVelocity;

            Assert.AreEqual(_config.dashSpeed, v.magnitude, 1e-3f,
                $"冲刺速率应为 dashSpeed={_config.dashSpeed}；"
                + $"若为 {_config.dashSpeed * Mathf.Sqrt(2f):F2} 量级，说明方向没归一化就乘了速度");

            Vector2 expected = new Vector2(0.7071f, 0.7071f);
            Assert.Greater(Vector2.Dot(v.normalized, expected), 0.999f,
                $"冲刺方向应为玩家朝向 {expected}，实测 {v.normalized}；若等于 (1,0) 说明仍是固定 x 轴冲刺");
        }

        [Test]
        public void M7_冲刺走输入缓冲窗口()
        {
            _buffer.Push(new InputSnapshot(Vector2.zero, true, false), 0f);

            Assert.IsTrue(_logic.MoveGroup.CanDash(0.01f), "窗口内的冲刺按下应判定为可冲刺");
            Assert.IsTrue(_logic.MoveGroup.TryConsumeDash(0.01f), "首次消费应成功");
            Assert.IsFalse(_logic.MoveGroup.TryConsumeDash(0.01f), "同一次按下只能消费一次（否则会连冲两次）");

            _buffer.Push(new InputSnapshot(Vector2.zero, true, false), 10f);
            Assert.IsFalse(_logic.MoveGroup.CanDash(10f + _config.dashBufferTime + 0.5f), "超出缓冲窗口的按下必须失效");

            // 冷却内：即使缓冲有按下也不可冲
            _buffer.Push(new InputSnapshot(Vector2.zero, true, false), 20f);
            Assert.IsTrue(_logic.MoveGroup.TryConsumeDash(20f), "冷却已过应能消费");

            _buffer.Push(new InputSnapshot(Vector2.zero, true, false), 20.1f);
            Assert.IsFalse(_logic.MoveGroup.CanDash(20.1f), "冷却未过时不得再冲");
        }

        /// <summary>回归：第一个物理帧同样参与抢占 —— 若 CheckTransitions 在 CurrentState == null 时直接 return GetFallBackState()，玩家第一次按冲刺（冷却与缓冲都成立）会被吞掉。</summary>
        [Test]
        public void M14_首帧同样参与抢占()
        {
            var snap = new InputSnapshot(Vector2.right, true, false);
            _buffer.Push(in snap, 0f);

            Assert.IsTrue(_logic.MoveGroup.CanDash(0f), "前置条件：首帧冷却与缓冲都成立");

            Tick(Vector2.right, 0f, dashPressed: true);

            Assert.AreEqual(MovementStateTag.Dash, _logic.MoveGroup.Current, "首帧应直接进入 Dash");
            Assert.IsFalse(_buffer.CanConsume(InputType.Dash, 0f, _config.dashBufferTime),
                "首帧既已提交，这次按下必须被消费掉（否则第二帧会再冲一次）");
            Assert.Less(Vector2.Distance(new Vector2(_config.dashSpeed, 0f), _motor.EngineVelocity), 1e-3f,
                "首帧提交成功就该是冲刺速度，而不是基础态的 moveSpeed");
        }

        /// <summary>抢占失败（未提交）时状态、方向、速度都必须原样保留：Configure 只能跑在消费成功之后，否则状态对象会被一次失败的抢占改写。</summary>
        [Test]
        public void M13_抢占失败不得改动状态()
        {
            Tick(Vector2.right, 0f, dashPressed: true);
            Assert.AreEqual(MovementStateTag.Dash, _logic.MoveGroup.Current, "首帧应抢占到 Dash");
            Assert.AreEqual(Vector2.right, _logic.MoveGroup.Dash.Direction, "Configure 应把入场方向喂成输入方向");

            // t=0.15 时冷却（1.5s）远未过：抢占判定为假，本帧什么都不该发生
            _buffer.Push(new InputSnapshot(Vector2.up, true, false), 0.15f);
            Tick(Vector2.up, 0.15f, dashPressed: true, resetVelocity: false);

            Assert.AreEqual(MovementStateTag.Dash, _logic.MoveGroup.Current,
                "冲刺时长 0.2s 未到，且抢占未成立：必须仍在 Dash，不得被基础态接管");
            Assert.AreEqual(Vector2.right, _logic.MoveGroup.Dash.Direction,
                "抢占未提交却改写了方向，说明 Configure 跑在消费成功之前");
            Assert.Less(Vector2.Distance(new Vector2(_config.dashSpeed, 0f), _motor.EngineVelocity), 1e-3f,
                "抢占未提交不得让基础态接管速度：仍在 Dash 中应保持冲刺速度");

            Assert.IsTrue(_buffer.CanConsume(InputType.Dash, 0.15f, _config.dashBufferTime),
                "未提交的抢占不得消费缓冲 —— 按下应当原样留在里面");
        }

        [Test]
        public void M10_边界钳位与未接线退化()
        {
            var bounds = new BoundsArea(new Vector2(-2f, -2f), new Vector2(2f, 2f));

            Assert.IsTrue(bounds.TryClamp(new Vector2(99f, 0f), out Vector2 clamped), "越界位置必须报告已钳位");
            Assert.AreEqual(2f, clamped.x, 1e-3f, "应被钳到右边界");
            Assert.AreEqual(0f, clamped.y, 1e-3f, "未越界的分量不得被改动");
            Assert.IsFalse(bounds.TryClamp(new Vector2(2f, -2f), out _), "位置已在边界上时不得报告钳位（避免每帧写位置打断刚体插值）");

            // ① 默认值（min == max == 零）：组合根在 boundsArea 未接线时传的就是它
            var unset = default(BoundsArea);
            Assert.IsFalse(unset.IsValid, "默认值必须判为无效区域");
            Assert.IsFalse(unset.TryClamp(new Vector2(99f, -99f), out Vector2 kept), "无效区域不得报告钳位");
            Assert.AreEqual(new Vector2(99f, -99f), kept, "无效区域必须原样返回位置（否则玩家被钉死在地图原点）");

            // ② 只有一个轴有尺寸（例：BoxCollider2D 的 Size 有一轴是 0）
            var flat = new BoundsArea(new Vector2(-2f, 0f), new Vector2(2f, 0f));
            Assert.IsFalse(flat.IsValid, "单轴尺寸为 0 必须判无效——否则玩家会被钳到一条线上");
            Assert.IsFalse(flat.TryClamp(new Vector2(99f, -99f), out Vector2 keptFlat));
            Assert.AreEqual(new Vector2(99f, -99f), keptFlat, "无效区域必须原样返回位置");
        }

        [Test]
        public void M12_刚体速度真的被写入()
        {
            var go = CreateMotorObject("移动测试_速度", out PlayerMotor motor, out Rigidbody2D body);

            try
            {
                motor.Move(new Vector2(3f, -4f));      // 首次访问触发惰性自取与初始化

                Assert.IsTrue(motor.IsInitialized, "首次使用必须完成自取初始化（不依赖 Awake 时机）");
                // 这两条断言只有在创建时**故意写成非默认值**时才有意义：
                // 若创建时就把 gravityScale 设成 0，无论 Initialize 跑没跑断言都成立 —— 那是假绿。
                Assert.AreEqual(0f, body.gravityScale, "俯视角：Initialize 必须把重力缩放写成 0");
                Assert.IsTrue(body.freezeRotation, "俯视角：Initialize 必须冻结旋转");
                Assert.AreEqual(new Vector2(3f, -4f), body.velocity, "PlayerMotor.Move 必须写进 Rigidbody2D.velocity");
                Assert.AreEqual(new Vector2(3f, -4f), motor.EngineVelocity, "Velocity 必须回读同一份真值");

                motor.SetPosition(new Vector2(1.5f, 2.5f));
                Assert.AreEqual(new Vector2(1.5f, 2.5f), motor.Position, "SetPosition 必须落到物理体位置");

                // 初始化只生效一次：不能每次读速度都把物理参数重写回去
                body.gravityScale = 0.5f;
                _ = motor.EngineVelocity;
                Assert.AreEqual(0.5f, body.gravityScale, "重复访问不得再次执行初始化");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>敌人的物理参数归敌人执行器：连续碰撞检测由 EnemyMotor 固化 —— 写在别处就是"敌人的物理长什么样"有两个可能的答案，各有各的非默认值才测得出（否则断言恒真 = 假绿）。</summary>
        [Test]
        public void M22_敌人执行器固化敌人侧的物理参数()
        {
            var go = new GameObject("移动测试_敌人执行器");

            try
            {
                var body = go.AddComponent<Rigidbody2D>();
                body.gravityScale = 1f;
                body.freezeRotation = false;
                body.collisionDetectionMode = CollisionDetectionMode2D.Discrete;

                var motor = go.AddComponent<EnemyMotor>();
                motor.EnsureInitialized();

                Assert.IsTrue(motor.IsInitialized);
                Assert.AreEqual(0f, body.gravityScale, "共同的物理参数（重力缩放 0）");
                Assert.IsTrue(body.freezeRotation, "共同的物理参数（冻结旋转）");
                Assert.AreEqual(CollisionDetectionMode2D.Continuous, body.collisionDetectionMode,
                    "敌人侧独有的物理参数必须由敌人的执行器固化（否则会被高速击退穿墙）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>门禁由状态效果层产出（受击状态），在移动层的状态跑完之后统一施加 —— 写在状态之前会被 SnapVelocity 覆盖掉，表现就是"挨打了却纹丝不动"。</summary>
        [Test]
        public void M21_受击期间输入不接管速度()
        {
            _config.moveAcceleration = 0f;    // 让击退一帧到位、便于断言

            var damage = new DeepseaOil.Logic.Combat.Damage(
                Vector2.zero,
                0f,                                             // 只推不扣血
                DeepseaOil.Logic.Combat.DamageSource.Contact,
                Vector2.right,
                12f);

            Assert.IsTrue(_logic.TakeDamage(in damage, 0f), "纯击退也该生效");

            Tick(Vector2.up, 0.02f);          // 这一帧玩家正按着"上"

            Assert.AreEqual(12f, _motor.EngineVelocity.x, 1e-3f, "受击帧的速度由门禁决定");
            Assert.AreEqual(0f, _motor.EngineVelocity.y, 1e-3f, "输入不该在受击帧生效");
        }

        /// <summary>建一个 PlayerMotor 物体（先加 Rigidbody2D 再加组件，顺序不能反），接线与场景一致；物理参数故意留成非默认值，否则"Initialize 真的跑了"与"值恰好就是默认的"区分不开 = 假绿。</summary>
        /// <remarks>EditMode 下 AddComponent 不触发 Awake，所以本类不能依赖 Awake 里的自取与自检 —— PlayerMotor 的物理体引用因此做成惰性兜底，M12 走的正是那条路径。</remarks>
        private static GameObject CreateMotorObject(string name, out PlayerMotor motor, out Rigidbody2D body)
        {
            var go = new GameObject(name);

            body = go.AddComponent<Rigidbody2D>();
            body.gravityScale = 1f;          // 非默认：断言"被 Initialize 改成 0"才有意义
            body.freezeRotation = false;     // 非默认：同上

            motor = go.AddComponent<PlayerMotor>();

            return go;
        }
    }
}
