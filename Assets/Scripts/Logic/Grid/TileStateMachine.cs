using System.Collections.Generic;
using cfg.dso;

namespace DeepseaOil.Logic.Grid
{
    /// <summary>单格状态机，只管切换与回调（Tick 调度在 GridLogic 与队列里），切换立即、同步、同帧生效</summary>

    public sealed class TileStateMachine
    {
        private readonly Dictionary<TileStateType, System.Func<ITileState>> _factories = new();

        private ITileState _current;

        public TileStateType CurrentId => _current != null ? _current.Id : TileStateType.Normal;

        public ITileState Current => _current;

        /// <summary>注册一个状态的工厂，Normal 不需要注册</summary>
        public void Register(TileStateType id, System.Func<ITileState> factory)
        {
            if (factory == null) return;

            _factories[id] = factory;
        }

        /// <summary>已经是该状态时不重入、不重置（反复投水球不刷新泥浆计时），返回 false；造不出实例时也返回 false 且不动 _current</summary>
        /// <remarks>durationOverride &gt; 0 时覆盖表里的存活秒数（二级反应的结果存续用它）。</remarks>
        public bool SwitchTo(TileStateType next, in TileContext ctx, float durationOverride = 0f)
        {
            if (CurrentId == next) return false;

            ITileState created;

            try
            {
                created = Create(next);
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[Grid] 造状态 {next} 的实例时抛异常，本次切换作废：{e}");

                created = null;
            }

            if (created == null) return false;

            _current?.OnExit(in ctx);

            _current = created;

            if (durationOverride > 0f) _current.OverrideDuration(durationOverride);

            _current.OnEnter(in ctx);

            return true;
        }

        public void Tick(in TileContext ctx)
        {
            _current?.OnTick(in ctx);
        }

        private ITileState Create(TileStateType id)
        {
            if (!_factories.TryGetValue(id, out System.Func<ITileState> factory) || factory == null) return null;

            return factory();
        }
    }
}
