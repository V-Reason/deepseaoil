using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Logic.Grid
{
    /// <summary>双缓冲 Tick 队列，本帧提交的请求下一帧才处理；每逻辑帧开头调一次 Swap 翻帧，一次性消费，同帧去重</summary>
    public sealed class TileTickQueue
    {
        private List<Vector3Int> _current = new();
        private List<Vector3Int> _next = new();
        private readonly HashSet<Vector3Int> _dedup = new();

        public IReadOnlyList<Vector3Int> Current => _current;

        public void Schedule(Vector3Int cell)
        {
            if (_dedup.Add(cell)) _next.Add(cell);
        }

        public void Swap()
        {
            (_current, _next) = (_next, _current);

            _next.Clear();
            _dedup.Clear();
        }
    }
}
