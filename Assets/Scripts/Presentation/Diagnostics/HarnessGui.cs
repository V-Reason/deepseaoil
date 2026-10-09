using UnityEngine;

namespace DeepseaOil.Presentation.Diagnostics
{
    /// <summary>面板放大倍数按屏幕收口；命中矩形要乘回倍数</summary>
    /// <remarks>被测对象在正中，倍数越过 0.5 会被左上角面板盖住并吃掉点击</remarks>
    internal static class HarnessGui
    {
        private const float MaxWidthRatio = 0.47f;

        private const float MaxHeightRatio = 0.97f;

        internal static float Scale(float guiScale, Vector2 panelSize)
        {
            float want = guiScale > 0f ? guiScale : 2f;

            float byWidth = Screen.width * MaxWidthRatio / Mathf.Max(panelSize.x, 1f);
            float byHeight = Screen.height * MaxHeightRatio / Mathf.Max(panelSize.y, 1f);

            return Mathf.Max(1f, Mathf.Min(want, Mathf.Min(byWidth, byHeight)));
        }

        internal static Rect ScreenRect(Vector2 panelOrigin, Vector2 panelSize, float scale)
        {
            return new Rect(panelOrigin.x * scale, panelOrigin.y * scale, panelSize.x * scale, panelSize.y * scale);
        }
    }
}
