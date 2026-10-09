namespace DeepseaOil.Data
{
    /// <summary>地块效果种类，取值与 cfg.dso.TileEffectType 表号一一对应</summary>
    /// <remarks>Skid(2)/Block(5)/Fixed(7) 保留枚举位与表行但本轮不实现。</remarks>
    public enum TileEffectKind
    {
        /// <summary>无效果，id=0，默认值</summary>
        None = 0,

        /// <summary>减速，scale=速度倍率（0.5=半速），seconds=续命时长</summary>
        Slow = 1,

        /// <summary>滑行，沿朝向滑 cells 格、seconds 秒；本轮未实现</summary>
        Slide = 3,

        /// <summary>击退，cells=距离（格），冲量由执行者按格边长折算</summary>
        KnockBack = 4,

        /// <summary>麻痹，seconds=不能行动的时长；本轮未接移动门禁</summary>
        Numbness = 6,

        /// <summary>瞬时伤害，amount=伤害值</summary>
        InstantDamage = 8,

        /// <summary>持续伤害，每 interval 秒扣 perTick，累计 seconds；0=持续到状态结束</summary>
        DamageOverTime = 9,

        /// <summary>温湿度继承，按三个比率放大元素，1=原样</summary>
        InheritElement = 10,

        /// <summary>清除植物，清掉该格元素的"含植物"标签位；radius=十字范围，当前只作用于本格</summary>
        ClearPlants = 11,
    }

    /// <summary>一次已定值的格子效果：Kind ＋ 该种类用到的槽位</summary>
    /// <remarks>Data 层产出、Logic 层消费。只能经工厂构造；读取时先看 Kind 再取对应属性。</remarks>
    public readonly struct TileEffectValue
    {
        public readonly TileEffectKind Kind;

        /// <summary>槽位1，伤害值/滑行格数/击退格数/每次伤害/每秒伤害/清除范围</summary>
        private readonly float _a;

        /// <summary>槽位2，持续时间（秒）/伤害间隔（秒）/湿度继承比率/每帧伤害</summary>
        private readonly float _b;

        /// <summary>槽位3，温度继承比率/导电继承比率/累计时长</summary>
        private readonly float _c;

        private TileEffectValue(TileEffectKind kind, float a, float b, float c)
        {
            Kind = kind;
            _a = a;
            _b = b;
            _c = c;
        }

        /// <summary>减速用速度倍率，1=不减速</summary>
        public float Scale => _a;

        /// <summary>持续时间（秒）</summary>
        public float Seconds => _b;

        /// <summary>瞬时伤害用伤害值</summary>
        public float Amount => _a;

        /// <summary>持续伤害用每次伤害</summary>
        public float PerTick => _a;

        /// <summary>持续伤害用间隔（秒），0=每帧</summary>
        public float Interval => _b;

        /// <summary>持续伤害用累计时长（秒），0=持续到状态结束</summary>
        public float Duration => _c;

        /// <summary>滑行/击退的距离（格）</summary>
        public float Cells => _a;

        /// <summary>清除植物用范围（格）</summary>
        public int Radius => (int)_a;

        /// <summary>是不是"进格一下"的一次性效果：只该在进入那一刻生效</summary>
        /// <remarks>判据是语义而非表里的 flag 列（该列至今无读取点，且瞬时伤害行的 level1 填 false，按它判会漏）；瞬时伤害/击退逐帧重放会变成每秒 60 次掉血/弹开，持续伤害（按 interval 攒拍）与减速/麻痹（续命）不属此类</remarks>
        public bool IsEnterOnly
            => Kind == TileEffectKind.InstantDamage || Kind == TileEffectKind.KnockBack;

        /// <summary>温度继承比率</summary>
        public float TemperatureRatio => _a;

        public float WetRatio => _b;

        public float ConductivityRatio => _c;

        public static TileEffectValue Slow(float scale, float seconds)
            => new TileEffectValue(TileEffectKind.Slow, scale, seconds, 0f);

        public static TileEffectValue InstantDamage(float amount)
            => new TileEffectValue(TileEffectKind.InstantDamage, amount, 0f, 0f);

        public static TileEffectValue KnockBack(float cells)
            => new TileEffectValue(TileEffectKind.KnockBack, cells, 0f, 0f);

        public static TileEffectValue Slide(int cells, float seconds)
            => new TileEffectValue(TileEffectKind.Slide, cells, seconds, 0f);

        public static TileEffectValue Numbness(float seconds)
            => new TileEffectValue(TileEffectKind.Numbness, seconds, 0f, 0f);

        public static TileEffectValue DamageOverTime(float perTick, float interval, float seconds)
            => new TileEffectValue(TileEffectKind.DamageOverTime, perTick, interval, seconds);

        public static TileEffectValue InheritElement(float tempRatio, float wetRatio, float condRatio)
            => new TileEffectValue(TileEffectKind.InheritElement, tempRatio, wetRatio, condRatio);

        public static TileEffectValue ClearPlants(int radius)
            => new TileEffectValue(TileEffectKind.ClearPlants, radius, 0f, 0f);

        public override string ToString()
        {
            return Kind switch
            {
                TileEffectKind.Slow => $"Slow(scale={Scale}, {Seconds}s)",
                TileEffectKind.InstantDamage => $"InstantDamage({Amount})",
                TileEffectKind.KnockBack => $"KnockBack({Cells}格)",
                TileEffectKind.Slide => $"Slide({Cells}格, {Seconds}s)",
                TileEffectKind.Numbness => $"Numbness({Seconds}s)",
                TileEffectKind.DamageOverTime => $"DoT({PerTick}/{Interval}s, {Duration}s)",
                TileEffectKind.InheritElement => $"InheritElement(temp={TemperatureRatio}, wet={WetRatio}, cond={ConductivityRatio})",
                TileEffectKind.ClearPlants => $"ClearPlants(r={Radius})",
                _ => "None",
            };
        }
    }
}
