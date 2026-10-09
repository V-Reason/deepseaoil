using UnityEngine;

namespace DeepseaOil.Presentation.Primitive
{
    /// <summary>运行期生成的两张纯色 sprite：圆点与方块/圆环，缺美术资源时的 fallback</summary>
    /// <remarks>不给 SpriteRenderer 留空 sprite：会画默认白色方块，只受 localScale 控制，与"球多大/阴影缩多少"互相覆盖。具名 pixelsPerUnit 让"半径几米"与 localScale 换算有确定值；HideFlags.HideAndDontSave 防被 Resources.UnloadUnusedAssets() 回收。</remarks>
    public static class PrimitiveSprites
    {
        private const int PointTextureSize = 64;

        private const float PointPixelsPerUnit = 64f;

        private static Sprite _circle;
        private static Sprite _square;

        /// <summary>实心圆点 sprite，直径=1 世界单位，全工程共用一张</summary>
        public static Sprite Circle
        {
            get
            {
                if (_circle == null) _circle = BuildCircle();

                return _circle;
            }
        }

        /// <summary>实心方块 sprite，边长=1 世界单位，供"整格"提示</summary>
        public static Sprite Square
        {
            get
            {
                if (_square == null) _square = BuildSquare();

                return _square;
            }
        }

        /// <summary>造贴地圆/圆环 sprite；thicknessMeters 是世界单位，verticalSquash 1=不压扁，perspectiveTaper 0=上下对称椭圆</summary>
        public static Sprite GroundDiscOrRing(
            float radius,
            float verticalSquash,
            float perspectiveTaper,
            float thicknessMeters,
            bool solid)
        {
            // 环厚按世界单位给再换算成比例；实心盘走内圈缩到 0 的分支。
            float thickness = solid
                ? 1f
                : Mathf.Clamp(thicknessMeters / Mathf.Max(radius, 1e-4f), 0.02f, 0.9f);

            return BuildGroundShape(radius, verticalSquash, perspectiveTaper, thickness, solid);
        }

        /// <summary>配置一个纯色 sprite 渲染器，所有视效件都走这里</summary>
        public static void Configure(
            SpriteRenderer renderer,
            Sprite sprite,
            Color color,
            int sortingOrder,
            float diameterMeters)
        {
            if (renderer == null) return;

            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;

            renderer.transform.localScale = new Vector3(diameterMeters, diameterMeters, 1f);
        }

        /// <summary>把渲染器缩放设成"贴图烘的半径"换算值；缩放系数=目标直径/贴图直径</summary>
        public static void ConfigureGround(
            SpriteRenderer renderer,
            Sprite sprite,
            Color color,
            int sortingOrder,
            float bakedRadius,
            float diameterMeters)
        {
            if (renderer == null) return;

            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;

            float scale = diameterMeters / Mathf.Max(bakedRadius * 2f, 1e-4f);

            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }

        private static Sprite BuildCircle()
        {
            const int size = PointTextureSize;
            const float radius = size * 0.5f;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - radius;
                    float dy = y + 0.5f - radius;

                    bool inside = dx * dx + dy * dy <= radius * radius;

                    pixels[y * size + x] = inside
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 0);
                }
            }

            return Commit(texture, pixels, size, "GroundDot", PointPixelsPerUnit);
        }

        private static Sprite BuildSquare()
        {
            const int size = 4;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }

            // 像素密度取 size
            return Commit(texture, pixels, size, "GroundSquare", size);
        }

        /// <summary>造贴地形状：上下两半各是标准椭圆，vBase=radius×verticalSquash，vTop=vBase×(1−perspectiveTaper)；不参与落点计算</summary>
        /// <remarks>thickness 收窄后内圈不小于外圈环会整个消失且不报错；实心盘不走那条夹取。</remarks>
        public static Sprite BuildGroundShape(
            float radius,
            float verticalSquash,
            float perspectiveTaper,
            float thickness,
            bool solid = false)
        {
            const int size = PointTextureSize;

            thickness = Mathf.Clamp(thickness, 0.01f, 0.9f);
            verticalSquash = Mathf.Clamp(verticalSquash, 0.02f, 1f);
            perspectiveTaper = Mathf.Clamp(perspectiveTaper, 0f, 0.95f);

            // sprite 是正方形，用水平直径换算像素密度
            float pixelsPerUnit = size / Mathf.Max(radius * 2f, 1e-4f);

            float cx = size * 0.5f;
            float cy = size * 0.5f;
            float rx = size * 0.5f;

            float vBase = rx * verticalSquash;
            float vTop = vBase * (1f - perspectiveTaper);

            float innerScale = solid ? 0f : (1f - thickness);
            float ix = rx * innerScale;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;

                    // 屏幕 y 向上为正：dy > 0 是上半弧，用更平的那个竖直半径。
                    float v = dy >= 0f ? vTop : vBase;

                    float outer = (dx * dx) / (rx * rx) + (dy * dy) / (v * v);

                    bool inside = outer <= 1f;

                    if (inside && !solid)
                    {
                        float iy = v * innerScale;
                        float inner = (dx * dx) / (ix * ix) + (dy * dy) / (iy * iy);

                        inside = inner >= 1f;
                    }

                    pixels[y * size + x] = inside
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 0);
                }
            }

            return Commit(texture, pixels, size, "GroundShape", pixelsPerUnit);
        }

        /// <summary>把像素数组落成 sprite；alphaIsTransparency: true 不能关，否则透明像素残留颜色被双线性过滤带进边缘成脏边</summary>
        private static Sprite Commit(Texture2D texture, Color32[] pixels, int size, string name, float pixelsPerUnit)
        {
            texture.name = name;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),        // pivot 居中：localScale 才是"以自身为中心"缩放
                pixelsPerUnit,
                0,
                SpriteMeshType.FullRect);

            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;

            return sprite;
        }
    }
}
