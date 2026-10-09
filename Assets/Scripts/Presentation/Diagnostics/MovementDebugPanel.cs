using DeepseaOil.Logic;
using DeepseaOil.Logic.Events;
using DeepseaOil.Logic.Movement;
using DeepseaOil.Logic.Player;
using UnityEngine;

namespace DeepseaOil.Presentation.Diagnostics
{
    /// 移动调试面板，显示移动状态、帧首真值与提交量、引擎回读速度、边界接线
    /// 状态机首次进入不发事件，故直接读 MoveGroup.Current；提交后预期与引擎速度不一致即引擎干预（撞障碍、外力），属正常。引擎速度是上一物理步的值，滞后一帧；世界边界一行是接线成败的唯一可见指示
    public sealed class MovementDebugPanel : MonoBehaviour
    {
        [SerializeField] private PlayerController controller = default;
        [SerializeField] private Vector2 panelOrigin = new Vector2(8f, 8f);
        [SerializeField] private Vector2 panelSize = new Vector2(380f, 180f);

        [Tooltip("面板放大倍数（相对 IMGUI 默认 12px 字号）。实际倍数还会按屏幕收口，保证面板不过屏幕中线；0/负数按 2 倍兜底。")]
        [SerializeField] private float guiScale = 2f;

        [SerializeField] private bool isPanelVisible = true;

        private MovementStateTag _previous;
        private MovementStateTag _last;
        private int _changeCount;

        private void Start()
        {
            EventBus<MovementStateChanged>.Subscribe(OnStateChanged);
        }

        private void OnDestroy()
        {
            EventBus<MovementStateChanged>.Unsubscribe(OnStateChanged);
        }

        private void OnStateChanged(MovementStateChanged evt)
        {
            _previous = evt.Previous;
            _last = evt.Current;
            _changeCount++;
        }

        private void OnGUI()
        {
            if (!isPanelVisible) return;

            Matrix4x4 saved = GUI.matrix;
            float scale = HarnessGui.Scale(guiScale, panelSize);

            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            // 拆一层：矩阵恢复只有一处，下面"未接线"那条 return 不会漏掉恢复
            DrawPanel();

            GUI.matrix = saved;
        }

        private void DrawPanel()
        {
            GUILayout.BeginArea(new Rect(panelOrigin.x, panelOrigin.y, panelSize.x, panelSize.y), GUI.skin.box);

            PlayerLogic logic = controller == null ? null : controller.Logic;
            if (logic == null)
            {
                GUILayout.Label("MovementDebugPanel: 未接线");
                GUILayout.EndArea();
                return;
            }

            WorldInfo world = controller.World;
            Vector2 frameStart = logic.Motor.FrameStartVelocity;
            Vector2 submitted = logic.Motor.SubmittedDelta;
            Vector2 expected = frameStart + submitted;
            Vector2 engine = controller.EngineVelocity;
            Vector2 facing = logic.Motor.Facing;

            GUILayout.Label($"状态: {logic.MoveGroup.Current}");
            GUILayout.Label(_changeCount == 0 ? "切换: (首帧)" : $"切换: {_previous} → {_last} ×{_changeCount}");
            GUILayout.Label($"输入方向: ({world.MoveDirection.x:F2}, {world.MoveDirection.y:F2})   朝向: ({facing.x:F2}, {facing.y:F2})");
            GUILayout.Label($"帧首真值 v: ({frameStart.x:F2}, {frameStart.y:F2})");
            GUILayout.Label($"本帧 Δv: ({submitted.x:F2}, {submitted.y:F2})");
            GUILayout.Label($"提交后预期: ({expected.x:F2}, {expected.y:F2})");
            GUILayout.Label($"引擎速度: ({engine.x:F2}, {engine.y:F2})   速率: {engine.magnitude:F2}");

            BoundsArea bounds = world.Bounds;
            GUILayout.Label(bounds.IsValid
                ? $"世界边界: 已启用  ({bounds.Min.x:F1}, {bounds.Min.y:F1}) ~ ({bounds.Max.x:F1}, {bounds.Max.y:F1})"
                : "世界边界: 未接线（不钳位）");

            GUILayout.EndArea();
        }
    }
}
