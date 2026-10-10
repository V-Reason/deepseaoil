using UnityEngine;

namespace DeepseaOil.Foundation
{
    /// <summary>世界 y 折成渲染档位的纯函数，频带由 RenderOrder 给</summary>
    /// <remarks>y 越小档位越大、越晚绘制；量化后同档顺序不定</remarks>
    public static class YSort
    {
        /// <summary>世界 y → [bandStart, bandEnd] 档位，小端=最远；取整 floor(y×levelsPerUnit+0.5)，NaN 或 levelsPerUnit≤0 返回 bandStart</summary>
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

            // 中点映射：y=0 落在频带正中心，负 Y 自然向上增长
            int midOrder = (bandStart + bandEnd) / 2;
            int order = midOrder - stepped;

            return Mathf.Clamp(order, bandStart, bandEnd);
        }
    }
}
