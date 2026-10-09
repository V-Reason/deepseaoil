using DeepseaOil.Data;
using UnityEngine;

namespace DeepseaOil.Logic.Movement
{
    /// <summary>速度账本：一帧的速度变更累加区 ＋ 帧末一次写出</summary>
    /// <remarks>调用方只有 ActorLogic.FixedTick；瞬变累进（单位/秒）不乘 Δt，加速度累进（单位/秒²）帧末乘 Δt，混在一处会让击退距离随帧率变化</remarks>
    public interface IActorLedger
    {
        Vector2 Velocity { get; }

        Vector2 SubmittedDelta { get; }

        Vector2 FrameStartVelocity { get; }

        /// <summary>速度乘数，1=不缩放；帧首复位</summary>
        /// <remarks>写口夹取 NaN→1、负数→0、&gt;1→1</remarks>
        float SpeedScale { get; set; }

        /// <summary>帧首：读真值、清空累加区、复位速度乘数</summary>
        void BeginStep(float now, float deltaTime);

        /// <summary>帧末：有提交才写出一次</summary>
        void Commit();

        /// <summary>累加一次冲量：一次性速度变化，不乘 Δt</summary>
        void AddImpulse(Vector2 deltaVelocity);

        /// <summary>累加一次持续力：单位/秒²，本帧贡献 = 该值 × Δt</summary>
        void AddForce(Vector2 acceleration);

        void SetVelocity(Vector2 velocity);

        /// <summary>速度硬钳到上限；上限 ≤0 不限制</summary>
        void ClampSpeed(float maxSpeed);

        /// <summary>缩放外力累加强度，俯视角角色填 0</summary>
        void SetExtraForceScale(float scale);
    }

    /// <summary>角色移动执行器完整契约：写物理体（IMovementMotor）＋ 控制律与账本（IStateHost ＋ IActorLedger）。只接受配置、不暴露配置</summary>
    public interface IActorMotor : IMovementMotor, Foundation.IStateHost, IActorLedger
    {
        /// <summary>角色共用运动参数，只写不读</summary>
        CharacterConfig Config { get; }

        /// <summary>装配期注入运动参数：玩家给 PlayerConfig，敌人由 EnemySpec 合表值与 EnemyTuning 造一份</summary>
        void Configure(CharacterConfig config);

        /// <summary>移动层的"走"：有惯性按加速度逼近，零惯性当帧直达；方向可未归一化</summary>
        new void MoveTowards(Vector2 direction, float speed);

        new void BrakeTowards();

        /// <summary>急停：速度当帧归零</summary>
        void StopMove();

        /// <summary>俯视角移动：速度整体接管为 direction × speed（零惯性直达）</summary>
        void MoveDirection(Vector2 direction, float speed);

        /// <summary>面向给定方向，零向量表示不改朝向</summary>
        void FaceTowards(Vector2 direction);
    }
}
