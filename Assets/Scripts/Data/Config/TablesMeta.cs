namespace DeepseaOil.Data
{
    /// <summary>手写表清单，非生成物，为表计数与关键表抽样提供判据</summary>
    /// <remarks>加表须同步补一行，漏登记不报错</remarks>
    public static class TablesMeta
    {
        public static readonly string[] Names =
        {
            "TbProjectile",
            "TbEnemy",
            "TbTileState",
            "TbPlayer",
            "TbWave",
            "TbTileInitial",
            "TbElementRule",
            "TbSeed",
            "TbElementDuoReaction",
        };

        public static int Count => Names.Length;
    }
}
