using UnityEngine;
using cfg.dso;
using DeepseaOil.Data;

namespace DeepseaOil.Logic.Grid
{
    // 一次 Tick/状态切换可见的上下文：格子、时间、两个端口
    // 时间由驱动方给：状态不读 Time，暂停时 DeltaTime 为 0 冻结
    public readonly struct TileContext
    {
        public readonly Vector3Int Cell;

        public readonly float Now;

        public readonly float DeltaTime;

        public readonly ITileScheduler Scheduler;

        /// <summary>唯一效果出口；可为 null（逻辑层单跑测试）</summary>
        public readonly ITileResolver Resolver;

        public TileContext(
            Vector3Int cell,
            float now,
            float deltaTime,
            ITileScheduler scheduler,
            ITileResolver resolver)
        {
            Cell = cell;
            Now = now;
            DeltaTime = deltaTime;
            Scheduler = scheduler;
            Resolver = resolver;
        }
    }

    /// <summary>状态提交口：Tick 请求进双缓冲队列（本帧提交下帧消费），转换进待处理表、Tick 循环后统一结算</summary>
    public interface ITileScheduler
    {
        void ScheduleTick(Vector3Int cell);

        void Transition(Vector3Int cell, TileStateType next);

        /// <summary>直接切状态（同步、同帧生效）；durationOverride &gt; 0 时覆盖表里的存活秒数</summary>
        bool SwitchTileState(Vector3Int cell, TileStateType next, float durationOverride = 0f);
    }

    /// <summary>状态的施加出口：找目标、算方向、判死活都在实现侧；状态只提交"对谁做什么"</summary>
    public interface ITileResolver
    {
        /// <summary>对格上目标造成一次伤害</summary>
        void DealCell(Vector3Int cell, float amount);

        /// <summary>对格上目标续一次减速；seconds 是续命窗口，按帧调用时就是本帧 Δt</summary>
        void ApplySlowCell(Vector3Int cell, float speedScale, float seconds);

        /// <summary>对格上目标施加麻痹</summary>
        void ApplyStunCell(Vector3Int cell, float seconds);
    }

    /// <summary>会被减速修饰影响的目标；当前只有敌人实现</summary>
    public interface ISlowable
    {
        /// <summary>续一次减速修饰，由目标的状态效果层持有；不续命即过期</summary>
        void ApplySlow(float speedScale, float seconds);
    }
}
