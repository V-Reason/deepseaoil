using DeepseaOil.Logic;
using DeepseaOil.Logic.Events;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UIElements;

namespace DeepseaOil.Presentation.UI
{
    public class SettingPanel : BasePanel
    {
        public override E_UILayer Layer => E_UILayer.Top;
        public override bool CanBeHideByKey => true;

        private TMP_Text txtSfxNum;
        private TMP_Text txtBgmNum;

        protected override void Awake()
        {
            base.Awake();
            txtSfxNum = GetComponent<TMP_Text>("txtSfxNum");
            txtBgmNum = GetComponent<TMP_Text>("txtBgmNum");
        }

        public override void ShowMe()
        {
            ShowBgmValue((int)(GameRoot.Instance.Audio.BgmVolume * 100));
            ShowSfxValue((int)(GameRoot.Instance.Audio.SfxVolume * 100));
        }

        public override void HideMe()
        {
        }

        protected override void OnButtonClicked(string name)
        {
            switch (name)
            {
                case "BtnClose":
                    GameRoot.Instance.UI.HidePanel<SettingPanel>();
                    break;
                case "BtnReturnMenu":
                    GameRoot.Instance.Game.ChangeState(GameState.Menu);
                    break;
            }
        }

        protected override void OnSliderValueChange(string sliderName, float value)
        {
            switch (sliderName)
            {
                case "SliderSfx":
                    ShowSfxValue((int)value);
                    break;
                case "SliderBgm":
                    ShowBgmValue((int)value);
                    break;
            }
        }

        public void ShowSfxValue(int vol)
        {
            if (txtSfxNum != null) txtSfxNum.text = vol.ToString() + " " + "%";

            GameRoot.Instance.Audio.SetSfxVolume(vol * 0.01f);
        }

        public void ShowBgmValue(int vol)
        {
            if (txtBgmNum != null) txtBgmNum.text = vol.ToString() + " " + "%";

            GameRoot.Instance.Audio.SetBgmVolume(vol * 0.01f);
        }

    }
}
