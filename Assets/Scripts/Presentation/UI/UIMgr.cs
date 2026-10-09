using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using DeepseaOil.Data;
using DeepseaOil.Logic.Input;

namespace DeepseaOil.Presentation.UI
{
    public enum E_UILayer
    {
        Bottom,
        Middle,
        Top,
        System,
    }

    /// <summary>管理所有 UI 面板，普通类由 GameRoot 持有，异步加载的协程也由 GameRoot 托管</summary>
    /// <remarks>面板预设体名必须与类名一致。三件套的创建在 Init，唯一调用点是 GameRoot。</remarks>
    public class UIMgr : IUIOperation
    {
        private abstract class BasePanelInfo
        {
            public bool isHide;
            public abstract BasePanel Panel { get; }
            public bool CanBeHide => Panel != null && Panel.CanBeHideByKey;

            public abstract void Hide(bool isDestroy);
        }

        private class PanelInfo<T> : BasePanelInfo where T : BasePanel
        {
            public T panel;
            public UnityAction<T> callBack;
            public E_UILayer Layer => panel.Layer;

            private readonly UIMgr _owner;

            public PanelInfo(UIMgr owner, UnityAction<T> callBack)
            {
                _owner = owner;
                this.callBack += callBack;
            }

            public override BasePanel Panel => panel;

            public override void Hide(bool isDestroy)
            {
                _owner.HidePanel<T>();
            }
        }


        public Camera uiCamera;
        private Canvas uiCanvas;
        private EventSystem uiEventSystem;

        /// <summary>装配是否完成</summary>
        public bool IsReady { get; private set; }

        /// <summary>重复 EventSystem 是否已报过一次（防刷屏）</summary>
        private bool _warnedDuplicateEventSystem;

        private Transform bottomLayer;
        private Transform middleLayer;
        private Transform topLayer;
        private Transform systemLayer;

        // 资源 Key：Resources 相对路径、不带扩展名；ResolvePath 两种形式都容忍
        private const string UI_CAMERA_KEY    = "ui/UICamera";
        private const string UI_CANVAS_KEY    = "ui/Canvas";
        private const string UI_EVENT_SYS_KEY = "ui/EventSystem";
        private const string UI_PANEL_PREFIX  = "ui/Panel/";

        private readonly Dictionary<string, BasePanelInfo> panelDic = new Dictionary<string, BasePanelInfo>();

        private readonly Dictionary<E_UILayer, Stack<BasePanelInfo>> openPanels = new()
        {
            { E_UILayer.Bottom, new Stack<BasePanelInfo>() },
            { E_UILayer.Middle, new Stack<BasePanelInfo>() },
            { E_UILayer.Top, new Stack<BasePanelInfo>() },
            { E_UILayer.System, new Stack<BasePanelInfo>() }
        };

        public UIMgr()
        {
        }

        /// <summary>装配 UI 三件套，唯一调用点是 GameRoot.Assemble</summary>
        public void Init()
        {
            if (IsReady)
            {
                Debug.LogError("[UI] UIMgr.Init 被调用了两次：它只该由 GameRoot 调一次。");
                return;
            }

            uiCamera = GameObject.Instantiate(AssetModule.Load<GameObject>(UI_CAMERA_KEY)).GetComponent<Camera>();
            GameObject.DontDestroyOnLoad(uiCamera.gameObject);

            uiCanvas = GameObject.Instantiate(AssetModule.Load<GameObject>(UI_CANVAS_KEY)).GetComponent<Canvas>();
            uiCanvas.worldCamera = uiCamera;
            GameObject.DontDestroyOnLoad(uiCanvas.gameObject);

            bottomLayer = uiCanvas.transform.Find("Bottom");
            middleLayer = uiCanvas.transform.Find("Middle");
            topLayer = uiCanvas.transform.Find("Top");
            systemLayer = uiCanvas.transform.Find("System");

            DisableSceneEventSystems();

            uiEventSystem = GameObject.Instantiate(AssetModule.Load<GameObject>(UI_EVENT_SYS_KEY)).GetComponent<EventSystem>();
            GameObject.DontDestroyOnLoad(uiEventSystem.gameObject);

            IsReady = true;
        }

        /// <summary>拆除三件套并归还资源引用计数</summary>
        /// <remarks>必须早于 AssetModule.Dispose，且必须幂等。</remarks>
        public void Dispose()
        {
            if (!IsReady) return;

            IsReady = false;

            foreach (KeyValuePair<string, BasePanelInfo> kv in panelDic)
            {
                AssetModule.Release(UI_PANEL_PREFIX + kv.Key);
            }

            panelDic.Clear();

            foreach (Stack<BasePanelInfo> stack in openPanels.Values)
            {
                stack.Clear();
            }

            if (uiEventSystem != null) GameObject.Destroy(uiEventSystem.gameObject);
            if (uiCanvas != null) GameObject.Destroy(uiCanvas.gameObject);
            if (uiCamera != null) GameObject.Destroy(uiCamera.gameObject);

            AssetModule.Release(UI_EVENT_SYS_KEY);
            AssetModule.Release(UI_CANVAS_KEY);
            AssetModule.Release(UI_CAMERA_KEY);

            uiEventSystem = null;
            uiCanvas = null;
            uiCamera = null;
            bottomLayer = null;
            middleLayer = null;
            topLayer = null;
            systemLayer = null;
        }

        /// <summary>停用场景里多余的 EventSystem 并就该去清理场景报一次错</summary>
        /// <remarks>停用而非销毁：场景那份是用户资产，停用足以让它走 OnDisable 摘掉注册。必须用 includeInactive 重载，失活的那份被激活时警告会回来。</remarks>
        private void DisableSceneEventSystems()
        {
            //调用点在实例化 ui/EventSystem 之前
            EventSystem[] sceneEventSystems = UnityEngine.Object.FindObjectsOfType<EventSystem>(true);

            foreach (EventSystem sceneEventSystem in sceneEventSystems)
            {
                if (sceneEventSystem == uiEventSystem)
                    continue;

                sceneEventSystem.gameObject.SetActive(false);

                if (_warnedDuplicateEventSystem)
                    continue;

                _warnedDuplicateEventSystem = true;
                Debug.LogError(
                    "[UI] 场景里已经有一份 EventSystem（以及建 UI 文本时 Unity 自动生成的 Canvas），" +
                    "UIMgr 已停用场景那份、改用自己从 Resources 创建并跨场景常驻的那一份；" +
                    "否则 Unity 会每帧刷一条「There are 2 event systems in the scene.」。\n" +
                    "清理方法：在 Hierarchy 里删掉场景自带的 EventSystem 与那个自动生成的 Canvas —— " +
                    "UI 的 Camera / Canvas / EventSystem 三件套由 UIMgr 从 Assets/Resources/ui/ 自动创建，" +
                    "场景里不需要第二份。\n" +
                    "这条只报一次（本局不会再刷）。",
                    sceneEventSystem);
            }
        }

        public Transform GetLayerFather(E_UILayer layer)
        {
            switch (layer)
            {
                case E_UILayer.Bottom:
                    return bottomLayer;
                case E_UILayer.Middle:
                    return middleLayer;
                case E_UILayer.Top:
                    return topLayer;
                case E_UILayer.System:
                    return systemLayer;
                default:
                    return null;
            }
        }

        /// <summary>显示面板；isSync 方法体从不读</summary>
        public void ShowPanel<T>(UnityAction<T> callBack = null, bool isSync = true) where T : BasePanel
        {
            string panelName = typeof(T).Name;
            if (panelDic.ContainsKey(panelName))
            {
                PanelInfo<T> panelInfo = panelDic[panelName] as PanelInfo<T>;
                if (panelInfo.panel == null)
                {
                    panelInfo.isHide = false;

                    if (callBack != null)
                        panelInfo.callBack += callBack;
                }
                else
                {
                    if (!panelInfo.panel.gameObject.activeSelf)
                        panelInfo.panel.gameObject.SetActive(true);

                    panelInfo.panel.ShowMe();
                    panelInfo.isHide = false;
                    callBack?.Invoke(panelInfo.panel);
                    openPanels[panelInfo.Layer].Push(panelInfo);
                }
                return;
            }

            panelDic.Add(panelName, new PanelInfo<T>(this, callBack));

            // GameRoot 是全工程唯一常驻 MonoBehaviour（DontDestroyOnLoad），UIMgr 不是 MonoBehaviour，协程由它托管
            GameRoot.Instance.StartCoroutine(CoLoadPanel<T>(panelName));
        }

        /// <remarks>轮询而非 await：await 续体会在 AssetModule.Tick 分发循环里重入 UIMgr</remarks>
        private System.Collections.IEnumerator CoLoadPanel<T>(string panelName) where T : BasePanel
        {
            string key = UI_PANEL_PREFIX + panelName;
            var handle = AssetModule.LoadAsync<GameObject>(key);

            while (!handle.IsDone)
                yield return null;

            if (!panelDic.TryGetValue(panelName, out var raw) || !(raw is PanelInfo<T> panelInfo))
            {
                AssetModule.Release(key);
                yield break;
            }

            if (panelInfo.isHide)
            {
                panelDic.Remove(panelName);
                AssetModule.Release(key);
                yield break;
            }

            var prefab = handle.Asset;
            if (prefab == null)
            {
                //降级资源也可能为 null，摘掉占位以免永久占坑
                Debug.LogError($"[UI] 面板加载失败，已放弃显示：{panelName}");
                panelDic.Remove(panelName);
                yield break;
            }

            GameObject panelObj = GameObject.Instantiate(prefab, middleLayer, false);

            T panel = panelObj.GetComponent<T>();
            Transform father = GetLayerFather(panel.Layer) ?? middleLayer;
            if (panel.transform.parent != father) 
                panel.transform.SetParent(father, false);

            panel.ShowMe();
            panelInfo.callBack?.Invoke(panel);
            panelInfo.callBack = null;
            panelInfo.panel = panel;
            openPanels[panelInfo.Layer].Push(panelInfo);
        }

        public void HidePanel<T>(bool isDestory = false) where T : BasePanel
        {
            string panelName = typeof(T).Name;
            if (panelDic.ContainsKey(panelName))
            {
                PanelInfo<T> panelInfo = panelDic[panelName] as PanelInfo<T>;
                if (panelInfo.panel == null)
                {
                    panelInfo.isHide = true;
                    panelInfo.callBack = null;
                }
                else
                {
                    if (panelInfo.isHide)
                        return;

                    panelInfo.isHide = true;
                    if (isDestory)
                    {
                        GameObject.Destroy(panelInfo.panel.gameObject);
                        panelDic.Remove(panelName);
                        //与 ShowPanel 的 LoadAsync 成对；引用计数归零进冷却期，不立即卸载
                        AssetModule.Release(UI_PANEL_PREFIX + panelName);
                    }
                    else
                        panelInfo.panel.gameObject.SetActive(false);
                    panelInfo.panel.HideMe();
                }
            }
        }

        public void GetPanel<T>(UnityAction<T> callBack) where T : BasePanel
        {
            string panelName = typeof(T).Name;
            if (panelDic.ContainsKey(panelName))
            {
                PanelInfo<T> panelInfo = panelDic[panelName] as PanelInfo<T>;
                if (panelInfo.panel == null)
                {
                    panelInfo.callBack += callBack;
                }
                else if (!panelInfo.isHide)
                {
                    callBack?.Invoke(panelInfo.panel);
                }
            }
            else
            {
                Debug.LogWarning($"面板{typeof(T)}还未被加载");
            }
        }


        public static void AddCustomEventListener(UIBehaviour control, EventTriggerType type, UnityAction<BaseEventData> callBack)
        {
            EventTrigger trigger = control.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = control.gameObject.AddComponent<EventTrigger>();

            EventTrigger.Entry entry = new EventTrigger.Entry();
            entry.eventID = type;
            entry.callback.AddListener(callBack);

            trigger.triggers.Add(entry);
        }


        public bool TryCloseTopmostPanel()
        {
            E_UILayer[] layers = {E_UILayer.System, E_UILayer.Top, E_UILayer.Middle, E_UILayer.Bottom};

            foreach (var layer in layers)
            {
                var stack = openPanels[layer];

                while (stack.Count > 0)
                {
                    BasePanelInfo info = stack.Peek();

                    if (info.Panel == null || info.isHide)
                    {
                        stack.Pop();
                        continue;
                    }

                    if (!info.Panel.CanBeHideByKey)
                    {
                        return false;
                    }

                    stack.Pop();
                    info.Hide(false);
                    return true;
                }
            }
            return false;
        }
        
        public void OpenPausePanel()
        {
            ShowPanel<PausePanel>();
        }

        public void OpenExitConfirmPanel()
        {
            ShowPanel<ExitConfirmPanel>();
        }
    }
}
