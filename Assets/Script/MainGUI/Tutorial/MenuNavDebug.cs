using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// 临时调试探针：记录主菜单点击与面板显隐，用于排查项目选择问题。排查完删除。
/// </summary>
public class MenuNavDebug : MonoBehaviour
{
    private static GameObject _panelOne;
    private static GameObject _panelTwo;
    private static bool _lastOne;
    private static bool _lastTwo;
    private static string _lastSelected = "";
    private static int _shotSeq = 0;

    private static void Shot(string tag)
    {
        if (!Application.isPlaying) return;
        _shotSeq++;
        string path = "Temp/probe_" + _shotSeq + "_" + tag + ".png";
        ScreenCapture.CaptureScreenshot(path);
        Debug.Log("[PROBE] 截图->" + path);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded += OnLoaded;
    }

    private static void OnLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Main Menu") return;
        var go = new GameObject("MenuNavDebug");
        DontDestroyOnLoad(go);
        go.AddComponent<MenuNavDebug>();
    }

    private void Start()
    {
        _panelOne = GameObject.Find("Panel_One");
        _panelTwo = GameObject.Find("Panel_Two");
        _lastOne = _panelOne != null && _panelOne.activeSelf;
        _lastTwo = _panelTwo != null && _panelTwo.activeSelf;
        Debug.Log("[PROBE] 主菜单已加载。Panel_One=" + _lastOne + " Panel_Two=" + _lastTwo);
    }

    private void Update()
    {
        // 记录面板显隐变化
        if (_panelOne != null && _panelOne.activeSelf != _lastOne)
        {
            _lastOne = _panelOne.activeSelf;
            Debug.Log("[PROBE] Panel_One 变为 " + _lastOne);
            Shot("panel1_" + (_lastOne ? "on" : "off"));
        }
        if (_panelTwo != null && _panelTwo.activeSelf != _lastTwo)
        {
            _lastTwo = _panelTwo.activeSelf;
            Debug.Log("[PROBE] Panel_Two 变为 " + _lastTwo);
            Shot("panel2_" + (_lastTwo ? "on" : "off"));
        }

        // 记录鼠标按下时命中的 UI 对象
        if (Input.GetMouseButtonDown(0) && EventSystem.current != null)
        {
            var ped = new PointerEventData(EventSystem.current)
            {
                position = Input.mousePosition
            };
            var results = new System.Collections.Generic.List<RaycastResult>();
            EventSystem.current.RaycastAll(ped, results);
            if (results.Count > 0)
            {
                string chain = results[0].gameObject.name;
                Transform t = results[0].gameObject.transform.parent;
                int depth = 0;
                while (t != null && depth < 4)
                {
                    chain += " < " + t.name;
                    t = t.parent;
                    depth++;
                }
                Debug.Log("[PROBE] 鼠标点击命中: " + chain + " (共命中 " + results.Count + " 层) 屏幕坐标=" + Input.mousePosition);
                Shot("click");
            }
            else
            {
                Debug.Log("[PROBE] 鼠标点击未命中任何 UI, 屏幕坐标=" + Input.mousePosition);
            }
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnLoaded;
    }
}
