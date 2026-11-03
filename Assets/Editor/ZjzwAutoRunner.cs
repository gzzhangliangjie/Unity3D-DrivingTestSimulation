using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 【直角转弯调参·编辑器侧自动运行器】供外部脚本/Agent 无人值守跑调参批次。
///
/// 用法（命令行启动 Unity 时带 -autoZjzw 参数）：
///   Unity.exe -projectPath &lt;工程&gt; -logFile &lt;日志&gt; -autoZjzw
/// 流程：
///   1. 工程打开后自动进入 Play 模式（无论当前打开的是哪个场景）；
///   2. 运行时 ZjzwTuner 检测到 Tuning/zjzw_autostart.flag → 自动补
///      SelectPass(sites=Zjzw) → 加载 Exam Mode → 连续跑完调参批次；
///   3. 批次完成/中止时 ZjzwTuner 写 Tuning/zjzw_autostart.done；
///   4. 本运行器看到 done → 停止 Play → 退出 Unity（exitCode=0）。
/// 退出码：0=批次正常完成；2=Play 在完成前被停止；3=超时兜底；5=无法进入 Play。
/// </summary>
[InitializeOnLoad]
public static class ZjzwAutoRunner
{
    private static readonly bool _auto;
    private static readonly string _tuningDir;
    private static readonly double _loadTime;   // 工程打开时刻（进入 Play 超时判定用）
    private static double _playStart;           // 进入 Play 时刻（批次超时判定用）
    private static bool _exiting;

    static ZjzwAutoRunner()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-autoZjzw") { _auto = true; break; }
        }
        if (!_auto) return;

        _tuningDir = Path.Combine(Application.dataPath, "..", "Tuning").Replace('\\', '/');
        _loadTime = EditorApplication.timeSinceStartup;

        // 清掉上次的完成标记：本次运行以新写的 done 为准
        try { if (File.Exists(DonePath)) File.Delete(DonePath); } catch { }

        Debug.Log("[ZjzwAutoRunner] 检测到 -autoZjzw：稍后自动进入 Play 模式");
        EditorApplication.delayCall += EnterPlay;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update += Update;
    }

    private static string DonePath
    {
        get { return Path.Combine(_tuningDir, "zjzw_autostart.done"); }
    }

    private static void EnterPlay()
    {
        if (_exiting) return;
        if (!EditorApplication.isPlaying && !EditorApplication.isCompiling)
        {
            Debug.Log("[ZjzwAutoRunner] 进入 Play 模式");
            EditorApplication.isPlaying = true;
        }
    }

    private static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (!_auto || _exiting) return;
        if (change == PlayModeStateChange.EnteredPlayMode)
        {
            _playStart = EditorApplication.timeSinceStartup;
        }
        else if (change == PlayModeStateChange.ExitingPlayMode)
        {
            // Play 结束：done 存在 = 批次完成（正常退出）；否则视为异常退出
            bool done = File.Exists(DonePath);
            Debug.Log("[ZjzwAutoRunner] Play 结束（" + (done ? "批次完成" : "批次未完成") + "），退出 Unity");
            _exiting = true;
            EditorApplication.delayCall += () => EditorApplication.Exit(done ? 0 : 2);
        }
    }

    private static void Update()
    {
        if (!_auto || _exiting) return;

        if (EditorApplication.isPlaying)
        {
            if (_playStart == 0) return;

            if (File.Exists(DonePath))
            {
                Debug.Log("[ZjzwAutoRunner] 批次完成标记已出现，停止 Play");
                EditorApplication.isPlaying = false;
                return;
            }
            // 批次超时兜底（默认 12 组 × ~30s，留足余量到 40 分钟）
            if (EditorApplication.timeSinceStartup - _playStart > 40 * 60)
            {
                Debug.LogError("[ZjzwAutoRunner] 批次超时兜底，强制退出（exitCode=3）");
                _exiting = true;
                EditorApplication.Exit(3);
            }
        }
        else
        {
            // 一直进不了 Play（编译错误弹安全模式等）：5 分钟后放弃
            if (EditorApplication.timeSinceStartup - _loadTime > 300 && _playStart == 0)
            {
                Debug.LogError("[ZjzwAutoRunner] 无法进入 Play 模式，退出（exitCode=5）");
                _exiting = true;
                EditorApplication.Exit(5);
            }
        }
    }
}
