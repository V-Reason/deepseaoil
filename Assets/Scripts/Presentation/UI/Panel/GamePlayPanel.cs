using DeepseaOil.Logic;
using TMPro;
using UnityEngine;

namespace DeepseaOil.Presentation.UI
{
    public class GamePlayPanel : BasePanel
    {
        public override E_UILayer Layer => E_UILayer.Bottom;
        public override bool CanBeHideByKey => false;

        private TMP_Text txtCountdown;
        private TMP_Text txtNextWave;

        protected override void Awake()
        {
            base.Awake();

            txtCountdown = GetComponent<TMP_Text>("TxtCountdown");
            txtNextWave = GetComponent<TMP_Text>("TxtNextWave");
        }

        public override void ShowMe() { }

        public override void HideMe() { }

        protected override void OnButtonClicked(string name)
        {
            switch (name)
            {
                case "BtnPause":
                    GameRoot.Instance.Game.ChangeState(GameState.Paused);
                    break;

                case "BtnSetting":
                    // GameRoot.Instance.Game.ChangeState(GameState.Paused);
                    break;
            }
        }
    }
}