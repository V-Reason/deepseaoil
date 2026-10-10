using UnityEngine;

namespace DeepseaOil.Foundation
{
    /// <summary>Y-Sort，世界 y 折成渲染档位的纯函数，频带由表现层 RenderOrder 给</summary>
    /// <remarks>y 越小=档位越大=越晚绘制。档位量化，同档顺序不定；频带外钳两端。</remarks>
    public static class YSort
    {
        /// <summary>世界 y → [bandStart, bandEnd] 档位，小端=最远，颠倒时先交换；取整 floor(y×levelsPerUnit+0.5)，NaN 或 levelsPerUnit≤0 返回 bandStart</summary>
        public static int OrderFor(float y, int bandStart, int bandEnd, float levelsPerUnit)
        {
            if (bandEnd < bandStart)
            {
                int swap = bandStart;
                bandStart = bandEnd;
                bandEnd = swap;
            }

            if (float.IsNaN(y) || float.IsNaN(levelsPerUnit) || levelsPerUnit <= 0f) return bandStart;

            int stepped = Mathf.FloorToInt(y * levelsPerUnit + 0.5f);

            // 中点映射：y=0 对应频带正中心，负 Y 坐标自然向上增长而绝不溢出封顶
            int midOrder = (bandStart + bandEnd) / 2;
            int order = midOrder - stepped;

            return Mathf.Clamp(order, bandStart, bandEnd);
        }
    }
}
