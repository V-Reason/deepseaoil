using DeepseaOil.Foundation;

namespace DeepseaOil.Presentation.Visual
{
    public static class RenderOrder
    {
        /// <summary>贴地件固定层（310），不参与 Y-Sort</summary>
        public const int GroundShadow = 310;

        /// <summary>瞄准反馈层（320），压着格效果、被格上角色盖住</summary>
        public const int Aim = 320;

        public const int YSortBandStart = 400;

        public const int YSortBandEnd = 1000;

        /// <summary>每世界单位档数，4 档表示每 0.25 米一个独立深度层级</summary>
        public const float YSortLevelsPerUnit = 4f;

        public const int ShatterPiece = 1200;

        /// <summary>0=Default</summary>
        public const int OverlayLayer = 0;

        public static int ActorOrder(float y)
        {
            return YSort.OrderFor(y, YSortBandStart, YSortBandEnd, YSortLevelsPerUnit);
        }

        /// <summary>球的档位，与角色同一频带，取贴地位置的 y 而非弧线视觉高度</summary>
        public static int BallOrder(float groundY)
        {
            return ActorOrder(groundY);
        }
    }
}
