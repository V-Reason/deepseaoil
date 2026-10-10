namespace DeepseaOil.Presentation.Effects
{
    /// <remarks>None=0 是哨兵值，Play(EffectId.None) 静默返回 EffectHandle.None，表示不播。新增 EffectId 动三处：本枚举加一项、EffectCatalog 加一行、需要资源才加预制体。</remarks>
    public enum EffectId
    {
        None = 0,

        BurstSparks,

        Flash,

        Shake,

        ScreenShake,

        MudSplash,

        Shatter,

        Highlight,

        SteamBlast,
    }
}
