using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Wave;
using TMPro;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Presentation.UI
{
    /// <summary>战斗 HUD：血量 / 水弹 / 土弹 / 战备种子 / 波次与阶段</summary>

    public sealed class HudPanel : BasePanel
    {
        private const string HpTextName = "txtHp";

        private const string WaterTextName = "txtWater";

        private const string WaveTextName = "txtWave";

        private const string SeedTextName = "txtSeed";

        private const string PhaseTextName = "txtPhase";

        private TMP_Text _hp;
        private TMP_Text _water;
        private TMP_Text _wave;
        private TMP_Text _seed;
        private TMP_Text _phase;

        private bool _subscribed;

        /// <summary>放 Bottom 不挡 Esc：关闭时按 System→Top→Middle→Bottom 找第一个能关的</summary>
        public override E_UILayer Layer => E_UILayer.Bottom;

        public override bool CanBeHideByKey => false;

        protected override string SetInitialTxt(string name)
        {
            switch (name)
            {
                case HpTextName:
                case WaterTextName:
                case WaveTextName:
                case SeedTextName:
                case PhaseTextName:
                    return "--";

                default:
                    return base.SetInitialTxt(name);
            }
        }

        protected override void Awake()
        {
            base.Awake();

            _hp = GetComponent<TMP_Text>(HpTextName);
            _water = GetComponent<TMP_Text>(WaterTextName);
            _wave = GetComponent<TMP_Text>(WaveTextName);
            _seed = GetComponent<TMP_Text>(SeedTextName);
            _phase = GetComponent<TMP_Text>(PhaseTextName);
        }

        public override void ShowMe()
        {
            Subscribe();

            EventBus<RequestHudRefresh>.Publish(new RequestHudRefresh());
        }

        public override void HideMe()
        {
            Unsubscribe();
        }

        private void OnDestroy()
        {
            // 静态事件总线不会因物体销毁自动解除引用，必须显式退订。
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;

            _subscribed = true;

            EventBus<PlayerHealthChanged>.Subscribe(OnHealthChanged);
            EventBus<PlayerAmmoChanged>.Subscribe(OnAmmoChanged);
            EventBus<WaveChanged>.Subscribe(OnWaveChanged);
            EventBus<WavePhaseChanged>.Subscribe(OnWavePhaseChanged);
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;

            _subscribed = false;

            EventBus<PlayerHealthChanged>.Unsubscribe(OnHealthChanged);
            EventBus<PlayerAmmoChanged>.Unsubscribe(OnAmmoChanged);
            EventBus<WaveChanged>.Unsubscribe(OnWaveChanged);
            EventBus<WavePhaseChanged>.Unsubscribe(OnWavePhaseChanged);
        }

        private void OnHealthChanged(PlayerHealthChanged evt)
        {
            if (_hp == null) return;

            _hp.text = $"血量 {Mathf.CeilToInt(evt.Current)}/{Mathf.CeilToInt(evt.Max)}";
        }

        private void OnAmmoChanged(PlayerAmmoChanged evt)
        {
            if (_water != null)
            {
                _water.text = $"水 {evt.Water}/{evt.WaterCapacity} · 土 {evt.Earth}/{evt.EarthCapacity}";
            }

            if (_seed == null) return;

            _seed.text = evt.Seed == SeedType.None
                ? "种子：空（等下一波配给）"
                : $"种子：{SeedName(evt.Seed)}";
        }

        private void OnWaveChanged(WaveChanged evt)
        {
            if (_wave == null) return;

            _wave.text = $"第 {evt.WaveIndex} 波 · 敌人 {evt.Alive}";
        }

        private void OnWavePhaseChanged(WavePhaseChanged evt)
        {
            if (_phase == null) return;

            switch (evt.Phase)
            {
                case WavePhase.Prep:
                    _phase.text = evt.GrantSeed == SeedType.None
                        ? $"备战中 · 第 {evt.WaveIndex} 波"
                        : $"备战中 · 配给：{SeedName(evt.GrantSeed)}";
                    return;

                case WavePhase.Battle:
                    _phase.text = "战斗！";
                    return;

                default:
                    _phase.text = "清扫战场";
                    return;
            }
        }

        /// <summary>种子显示名；取不到时退回枚举名，绝不显示空白</summary>
        private static string SeedName(SeedType seed)
        {
            Data.SeedSpec spec = Data.ConfigModule.GetSeed(seed);

            return spec != null ? spec.Name : seed.ToString();
        }
    }
}
