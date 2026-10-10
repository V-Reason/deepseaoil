using cfg.dso;

namespace DeepseaOil.Logic.Grid
{
    /// <summary>一个格子状态：进/出/每次 Tick 三个钩子，状态自己决定要不要继续 Tick</summary>

    public interface ITileState
    {
        TileStateType Id { get; }

        /// <summary>进入本格状态时调用一次，初次转换与从别的状态切来同路</summary>
        void OnEnter(in TileContext ctx);

        void OnTick(in TileContext ctx);

        void OnExit(in TileContext ctx);

        /// <summary>覆盖表里的存活秒数；由格子层在 OnEnter 之前调用，seconds &gt; 0 才有效</summary>
        /// <remarks>二级反应的"结果存续"与表里的 duration 是两回事，故用覆盖而不是改表值。</remarks>
        void OverrideDuration(float seconds);
    }
}
