using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 【直角转弯·编辑器侧看门狗】供 Agent 在已打开的编辑器里远程触发演示：
///   1. 检测到 Tuning/zjzw_editorplay.flag → 删掉 flag 并自动进入 Play 模式；
///   2. 运行时 ZjzwTuner 看到 Tuning/zjzw_autostart.flag → 免按键自动跑批次；
///   3. 批次完成写 zjzw_autostart.done → 本看门狗停止 Play（编辑器保持打开，不退出）。
/// 与 ZjzwAutoRunner（命令行 -autoZjzw，跑完退出 Unity）互补：这个只管开关 Play。
/// </summary>
[InitializeOnLoad]
public static class ZjzwWatchRunner
{
    private static readonly string _tuningDir;
    private static bool _triggered;

    static ZjzwWatchRunner()
    {
        _tuningDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Tuning").Replace('\\', '/');
        EditorApplication.update += Update;
        Debug.Log("[ZjzwWatchRunner] 看门狗已挂载：写 Tuning/zjzw_editorplay.flag 即自动开跑");
    }

    private static string TriggerPath { get { return Path.Combine(_tuningDir, "zjzw_editorplay.flag"); } }
    private static string DonePath { get { return Path.Combine(_tuningDir, "zjzw_autostart.done"); } }

    private static void Update()
    {
        if (!_triggered && !EditorApplication.isPlaying && !EditorApplication.isCompiling
            && !EditorApplication.isUpdating && File.Exists(TriggerPath))
        {
            try { File.Delete(TriggerPath); } catch { }
            _triggered = true;
            Debug.Log("[ZjzwWatchRunner] 检测到触发 flag，进入 Play 模式");
            EditorApplication.isPlaying = true;
            return;
        }

        if (EditorApplication.isPlaying && File.Exists(DonePath))
        {
            string reason = "";
            try { reason = File.ReadAllText(DonePath); } catch { }
            Debug.Log("[ZjzwWatchRunner] 批次完成（" + reason.Trim() + "），停止 Play");
            try { File.Delete(DonePath); } catch { }
            _triggered = false;
            EditorApplication.isPlaying = false;
        }
    }
}
