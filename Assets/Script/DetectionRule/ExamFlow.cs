using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 全图考试（SelectPass.Sites.Default）流程调度器：把五个科目按真实考试顺序串成一场考试。
///
/// 为什么需要它：五个 Det_* 检测器是各自独立挂在场景里的，从场景加载开始就同时跑
/// Update —— 倒计时各自走、中途停车各自判。单科练习没事（只考一项），但全图考试里
/// 车在倒车入库库内合规停稳时，Det_CFTC / Det_BPQB / Det_ZJZW / Det_QXXS 都会当成
/// "中途停车"扣分（实测倒库段被 Det_CFTC 扣 5 分）；而且 BPQB 只有 30 秒倒计时，
/// 从进场就开始走，到坡道前早就超时扣 100。
///
/// 做法：一次只激活当前项目的检测器（Det_*.SetArmed），其余休眠（不计时、不判中途停车）；
/// 当前项目完成后进入"前往下一项"的过渡态，车开进下一项目区域才激活它。
/// 与真实考试一致：项目之间的行驶不计中途停车。
///
/// 顺序（与场地地理一致，自西向东再向北）：倒车入库 → 侧方停车 → 直角转弯 → 半坡起步 → 曲线行驶。
/// 最后一项完成后由场景原有的 Victory（Qxxs_WinWall）触发结算，本类不再干预。
///
/// 全部运行时自安装，不需要在编辑器里挂脚本或拖引用。
/// </summary>
public class ExamFlow : MonoBehaviour
{
    public static ExamFlow Instance { get; private set; }

    /// <summary>真实考试顺序。</summary>
    private static readonly SelectPass.Sites[] Order =
    {
        SelectPass.Sites.Dcrk, SelectPass.Sites.Cftc, SelectPass.Sites.Zjzw,
        SelectPass.Sites.Bpqb, SelectPass.Sites.Qxxs
    };

    private static readonly string[] ProjectNames =
    {
        "倒车入库", "侧方停车", "直角转弯", "半坡起步", "曲线行驶"
    };

    /// <summary>
    /// 各项目"考点地块"范围（xMin, xMax, zMin, zMax，世界坐标）。
    /// 车驶入该范围才激活对应检测器；范围之间刻意留空档（连接段路上不判分）。
    /// 场地真值：倒库 x[-110,-52]、侧方 x[-24,-2]、直角入口 x≈13 出口 x≈36、坡道 x≈35 z[-3,50]、曲线 z≥111。
    /// </summary>
    private static readonly Vector4[] Zones =
    {
        new Vector4(-130f, -47f, -48f, -26f),  // 倒车入库
        new Vector4(-42f, -2f, -38f, -24f),    // 侧方停车（左边界收到 -42，避开倒库出口通道）
        new Vector4(10f, 44f, -38f, -14f),     // 直角转弯
        new Vector4(26f, 44f, -6f, 53f),       // 半坡起步（-6 起，避让直角弯出口直道）
        new Vector4(0f, 60f, 105f, 165f),      // 曲线行驶
    };

    // ---------------- 检测器引用 ----------------
    private Det_DCRK _dcrk;
    private Det_CFTC _cftc;
    private Det_ZJZW _zjzw;
    private Det_BPQB _bpqb;
    private Det_QXXS _qxxs;

    private Transform _player;

    /// <summary>当前正在考的项目索引（-1 = 尚未开始）。</summary>
    private int _idx = -1;
    /// <summary>过渡态里即将进入的项目索引。</summary>
    private int _next = -1;
    private bool _inTransit;
    private bool _finished;

    // HUD
    private Canvas _canvas;
    private Text _hud;
    private float _hudTimer;

    // ============================================================
    //  安装
    // ============================================================
    private void Awake()
    {
        Instance = this;
        // Awake 里就把非首项的检测器关掉：检测器首帧 Update 就可能把"车静止"判成中途停车扣 100
        CacheDetectors();
        ArmProject(0);
        _idx = 0;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void CacheDetectors()
    {
        _dcrk = FindObjectOfType<Det_DCRK>();
        _cftc = FindObjectOfType<Det_CFTC>();
        _zjzw = FindObjectOfType<Det_ZJZW>();
        _bpqb = FindObjectOfType<Det_BPQB>();
        _qxxs = FindObjectOfType<Det_QXXS>();
    }

    /// <summary>只激活指定项目，其余全部休眠。</summary>
    private void ArmProject(int index)
    {
        SelectPass.Sites cur = index >= 0 && index < Order.Length ? Order[index] : SelectPass.Sites.Default;
        if (_dcrk != null) _dcrk.SetArmed(cur == SelectPass.Sites.Dcrk);
        if (_cftc != null) _cftc.SetArmed(cur == SelectPass.Sites.Cftc);
        if (_zjzw != null) _zjzw.SetArmed(cur == SelectPass.Sites.Zjzw);
        if (_bpqb != null) _bpqb.SetArmed(cur == SelectPass.Sites.Bpqb);
        if (_qxxs != null) _qxxs.SetArmed(cur == SelectPass.Sites.Qxxs);
    }

    private void ArmAllOff()
    {
        ArmProject(-1);
    }

    // ============================================================
    //  主循环
    // ============================================================
    private void Update()
    {
        if (_finished) return;

        if (_player == null)
        {
            GameObject go = GameObject.FindGameObjectWithTag("Player");
            if (go == null) return;
            _player = go.transform;
        }

        Vector3 pos = _player.position;

        if (_inTransit)
        {
            if (_next >= 0 && _next < Order.Length && InZone(_next, pos))
                EnterProject(_next);
        }
        else if (_idx >= 0 && IsProjectDone(_idx, pos))
        {
            BeginTransit(_idx + 1);
        }

        _hudTimer -= Time.deltaTime;
        if (_hudTimer <= 0f)
        {
            _hudTimer = 0.2f;
            RefreshHud();
        }
    }

    /// <summary>当前项目是否已完成。</summary>
    private bool IsProjectDone(int index, Vector3 pos)
    {
        switch (Order[index])
        {
            case SelectPass.Sites.Dcrk:
                // 两次倒库完成（ParkingCount==2）；检测器被出口 End 触发器销毁也算完成
                return _dcrk == null || _dcrk.ParkingCount >= 2;
            case SelectPass.Sites.Cftc:
                return _cftc == null || _cftc.IsCompleted;
            case SelectPass.Sites.Zjzw:
                // 直角转弯没有内部状态机：车驶出弯道、进到出口直道即完成
                return pos.z > -18.5f && pos.x > 33f;
            case SelectPass.Sites.Bpqb:
                return _bpqb == null || _bpqb.IsCompleted;
            default:
                // 曲线行驶是最后一项，由 Victory 触发器收尾，这里不推进
                return false;
        }
    }

    private bool InZone(int index, Vector3 pos)
    {
        if (index < 0 || index >= Zones.Length) return false;
        Vector4 z = Zones[index];
        return pos.x >= z.x && pos.x <= z.y && pos.z >= z.z && pos.z <= z.w;
    }

    private void BeginTransit(int nextIndex)
    {
        ArmAllOff();
        if (nextIndex >= Order.Length)
        {
            _finished = true;
            _idx = -1;
            _inTransit = false;
            Debug.Log("[ExamFlow] 五个项目全部完成，等待结算");
            RefreshHud();
            return;
        }
        _idx = -1;
        _next = nextIndex;
        _inTransit = true;
        Debug.Log(string.Format("[ExamFlow] 「{0}」完成 → 前往「{1}」",
            ProjectNames[nextIndex - 1], ProjectNames[nextIndex]));
        RefreshHud();
    }

    private void EnterProject(int index)
    {
        _idx = index;
        _next = -1;
        _inTransit = false;
        ArmProject(index);
        Debug.Log(string.Format("[ExamFlow] 开始第 {0}/5 项：{1}", index + 1, ProjectNames[index]));
        RefreshHud();
    }

    // ============================================================
    //  HUD：左上角项目进度表
    // ============================================================
    private void EnsureUI()
    {
        if (_hud != null) return;

        GameObject cgo = new GameObject("ExamFlowCanvas");
        _canvas = cgo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 40;
        CanvasScaler cs = cgo.AddComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1920f, 1080f);
        cs.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();

        GameObject bg = new GameObject("Panel");
        bg.transform.SetParent(cgo.transform, false);
        Image img = bg.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.42f);
        img.raycastTarget = false;
        RectTransform bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.012f, 0.70f);
        bgRt.anchorMax = new Vector2(0.30f, 0.965f);
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        GameObject txtGo = new GameObject("Text");
        txtGo.transform.SetParent(bg.transform, false);
        _hud = txtGo.AddComponent<Text>();
        _hud.font = FindChineseFont();
        _hud.color = Color.white;
        _hud.alignment = TextAnchor.UpperLeft;
        _hud.fontSize = 26;
        _hud.lineSpacing = 1.15f;
        _hud.raycastTarget = false;
        Outline outline = txtGo.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        RectTransform txtRt = txtGo.GetComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = new Vector2(14f, 10f);
        txtRt.offsetMax = new Vector2(-10f, -8f);
    }

    private static Font FindChineseFont()
    {
        Text[] texts = Object.FindObjectsOfType<Text>();
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null && texts[i].font != null) return texts[i].font;
        }
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private void RefreshHud()
    {
        EnsureUI();
        if (_hud == null) return;

        System.Text.StringBuilder sb = new System.Text.StringBuilder(160);
        sb.AppendLine("<b>科目二 · 全图考试</b>");
        for (int i = 0; i < Order.Length; i++)
        {
            string mark;
            if (_finished || i < _idx) mark = "✓";
            else if (i == _idx) mark = "▶";
            else if (_inTransit && i == _next) mark = "→";
            else mark = "　";
            sb.Append(mark).Append(' ').Append(i + 1).Append(". ").Append(ProjectNames[i]);
            if (i < Order.Length - 1) sb.AppendLine();
        }
        if (_inTransit && _next >= 0 && _next < Order.Length)
            sb.AppendLine().Append("前往下一个项目…");
        _hud.text = sb.ToString();
    }
}

/// <summary>
/// ExamFlow 安装看门狗：常驻（DontDestroyOnLoad），场景加载时按需创建调度器。
/// 用 sceneLoaded 而不是轮询，保证在检测器第一次 Update 之前就把非本项检测器关掉。
/// </summary>
public class ExamFlowInstaller : MonoBehaviour
{
    /// <summary>游戏启动时装一个常驻看门狗（跨场景不销毁），之后的每次场景加载都能接管。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Object.FindObjectOfType<ExamFlowInstaller>() != null) return;
        GameObject go = new GameObject("ExamFlowInstaller");
        DontDestroyOnLoad(go);
        go.AddComponent<ExamFlowInstaller>();
    }

    private void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryInstall(SceneManager.GetActiveScene());
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryInstall(scene);
    }

    private static void TryInstall(Scene scene)
    {
        if (scene.name != "Exam Mode") return;
        SelectPass sp = Object.FindObjectOfType<SelectPass>();
        if (sp == null || sp.sites != SelectPass.Sites.Default) return;
        if (ExamFlow.Instance != null) return;
        GameObject go = new GameObject("ExamFlow");
        go.AddComponent<ExamFlow>();
    }
}
