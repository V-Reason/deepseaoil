using DeepseaOil.Generated;
using DeepseaOil.Logic.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeepseaOil.Presentation.Input
{
    /// <summary>输入采样器：GameRoot 每渲染帧调一次，采样点唯一</summary>

    public sealed class InputProvider : MonoBehaviour
    {
        private InputSys _input;

        private GameRoot _root;

        private InputAction _attack;

        private InputAction _altAttack;

        private InputAction _plant;

        private InputAction _aim;

        private Vector2 _move;

        private bool _grabHeld;

        private bool _dashPressed;

        private bool _inputEnabled = true;

        /// <summary>本渲染帧瞄准屏幕坐标；是"当前位置"不是"按下沿"</summary>
        public Vector2 AimScreen { get; private set; }

        public bool AttackPressedThisFrame { get; private set; }

        public bool AltAttackPressedThisFrame { get; private set; }

        /// <summary>E 键：播种当前战备种子</summary>
        public bool PlantPressedThisFrame { get; private set; }

        /// <summary>本渲染帧的移动输入（已夹到单位长度）；生命神泉的"完全静止"判据之一</summary>
        public Vector2 MoveInput { get; private set; }

        // 输入开关；暂停/菜单时为 false
        public bool IsInputEnabled => _inputEnabled;

        private void Awake()
        {
            _input = new InputSys();

            Bind();
        }

        /// <summary>按路径取动作；取不到当场报出来，不静默变成"按键没反应"</summary>
        private void Bind()
        {
            _attack = Resolve("Player/Attack");
            _altAttack = Resolve("Player/AltAttack");
            _plant = Resolve("Player/Plant");
            _aim = Resolve("Player/Aim");
        }

        private InputAction Resolve(string path)
        {
            InputAction action = _input.asset.FindAction(path, throwIfNotFound: false);

            if (action == null)
            {
                Debug.LogError(
                    $"[Input] InputSys.inputactions 里找不到动作 {path}：" +
                    "该战斗输入将永久失效。请在动作表里补上它（并保存资产）。", this);
            }

            return action;
        }

        private void Start()
        {
            _root = GameRoot.Instance;
            _root.RegisterInputProvider(this);
        }

        private void OnDestroy()
        {
            // 用 Start 抓住的引用，不能再写 GameRoot.Instance：退出 Play/切场景时它可能已销毁，getter 会再 new 一个
            if (_root != null) _root.UnregisterInputProvider(this);
        }

        private void OnEnable()
        {
            _input.Player.Enable();
        }

        private void OnDisable()
        {
            _input.Player.Disable();
        }

        public void Sample()
        {
            // 指针输入先采，不受 _inputEnabled 影响；禁用期间要清掉按下沿，否则恢复那帧会把暂停前的按键当按下。
            SamplePointer();

            if (!_inputEnabled) return;

            _move = Vector2.ClampMagnitude(
                _input.Player.Move.ReadValue<Vector2>(),
                1f
            );

            MoveInput = _move;

            _grabHeld = _input.Player.Grab.IsPressed();

            // 瞬时输入累积，物理帧侧取走
            _dashPressed |= _input.Player.Dash.WasPressedThisFrame();
        }

        /// <remarks>禁用时按下沿必须清成 false：指针采样不受动作表开关影响，少了它"暂停时点一下鼠标"会当成开火。</remarks>
        private void SamplePointer()
        {
            if (!_inputEnabled)
            {
                AttackPressedThisFrame = false;
                AltAttackPressedThisFrame = false;
                PlantPressedThisFrame = false;

                return;
            }

            if (_aim != null) AimScreen = _aim.ReadValue<Vector2>();

            AttackPressedThisFrame = Pressed(_attack);

            AltAttackPressedThisFrame = Pressed(_altAttack);

            PlantPressedThisFrame = Pressed(_plant);
        }

        private static bool Pressed(InputAction action)
        {
            return action != null && action.WasPressedThisFrame();
        }

        /// <summary>取物理帧输入快照；消费后清除按下沿</summary>
        public InputSnapshot ConsumeSnapshot()
        {
            var snapshot = new InputSnapshot(
                _move,
                _dashPressed,
                _grabHeld
            );

            _dashPressed = false;

            return snapshot;
        }

        public void Clear()
        {
            _move = Vector2.zero;
            _grabHeld = false;
            _dashPressed = false;
        }

        public void SetInputEnabled(bool enabled)
        {
            _inputEnabled = enabled;

            if (!enabled)
                Clear();
        }
    }
}
