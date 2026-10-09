using System.Collections.Generic;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Data
{
    /// <summary>持有 tile_effect 表行，在数据层完成档位解析</summary>
    /// <remarks>档位在此选定，Logic 只见已定值 TileEffectValue；pos 为 1-based（同 effectValuePos 列），越界夹第 1 档并报 Warning</remarks>
    public sealed class TileEffectSpec
    {
        private readonly cfg.dso.TileEffect _row;

        public TileEffectSpec(cfg.dso.TileEffect row)
        {
            _row = row;
        }

        public TileEffectType Id => _row.Id;

        public string Name => _row.Name;

        /// <summary>表内注释原文</summary>
        public string Tip => _row.Tip;

        /// <summary>只认 value1 的条目数</summary>
        /// <remarks>value2/interval/flag 是修饰列，短于档数时沿用最后一档（见 Value），不塌成 0</remarks>
        public int LevelCount
        {
            get
            {
                return _row.Value1?.Count ?? 0;
            }
        }

        /// <summary>取第 pos 档（1-based）</summary>
        public TileEffectValue GetEffect(int pos)
        {
            return GetEffect(pos, out _);
        }

        /// <summary>inRange=false=夹取后兜底值</summary>
        public TileEffectValue GetEffect(int pos, out bool inRange)
        {
            int count = LevelCount;

            inRange = count > 0 && pos >= 1 && pos <= count;

            int index = inRange ? pos - 1 : 0;

            if (!inRange)
            {
                Debug.LogWarning(
                    $"[Config] 地块效果 {Id}（{Name}）没有第 {pos} 档（共 {count} 档）：按第 1 档兜底。请核对表里的 effectValuePos。");
            }

            if (count == 0)
            {
                // value1 无条目=这行还没填，返回 default
                return default;
            }

            return Build(index);
        }

        /// <summary>index 0-based，调用方保证在范围内</summary>
        private TileEffectValue Build(int index)
        {
            float v1 = Value(_row.Value1, index);
            float v2 = Value(_row.Value2, index);
            float interval = Value(_row.Interval, index);

            switch (Id)
            {
                case TileEffectType.Slow:
                    return TileEffectValue.Slow(v1, v2);

                case TileEffectType.Slid:
                    return TileEffectValue.Slide((int)v1, v2);

                case TileEffectType.KnockBack:
                    return TileEffectValue.KnockBack(v1);

                case TileEffectType.Numbness:
                    return TileEffectValue.Numbness(v1);

                case TileEffectType.DamageInstant:
                    return TileEffectValue.InstantDamage(v1);

                case TileEffectType.DamageOverTime:
                    return TileEffectValue.DamageOverTime(v1, interval, 0f);

                case TileEffectType.InheritedTW:
                    return TileEffectValue.InheritElement(v1, v2, interval);

                case TileEffectType.ClearPlant:
                    return TileEffectValue.ClearPlants((int)v1);

                // None/Skid/Block/Fixed 保留表行，解析成无效果
                default:
                    return default;
            }
        }

        /// <summary>该列没写到这一档时沿用最后一档（返回 0 会让 DoT 间隔变成每帧一次）</summary>
        private static float Value(IReadOnlyList<float> values, int index)
        {
            if (values == null || values.Count == 0 || index < 0) return 0f;

            return values[index < values.Count ? index : values.Count - 1];
        }
    }
}
