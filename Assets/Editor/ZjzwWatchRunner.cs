using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 【调参批次·编辑器侧看门狗】供 Agent 在已打开的编辑器里远程触发演示：
///   1. 检测到 Tuning/&lt;项目&gt;_editorplay.flag（zjzw / qxxs）→ 删掉 flag 并自动进入 Play 模式；
///   2. 运行时对应 Tuner（ZjzwTuner/QxxsTuner）看到 &lt;项目&gt;_autostart.flag → 免按键自动跑批次；
///   3. 批次完成写 &lt;项目&gt;_autostart.done → 本看门狗停止 Play（编辑器保持打开，不退出）。
/// 与 ZjzwAutoRunner（命令行 -autoZjzw / -autoQxxs，跑完退出 Unity）互补：这个只管开关 Play。
/// </summary>
[InitializeOnLoad]
public static class ZjzwWatchRunner
{
    private static readonly string _tuningDir;
    private static bool _triggered;
    private static string _activeDonePath; // 本次触发的完成标记路径（与触发的 flag 同前缀）
    private static double _finSince = -1;  // 非批次会话：DemoDriver 进入 Finished 的时刻（-1=未进入）

    private static readonly string[] TriggerFlags = { "zjzw_editorplay.flag", "qxxs_editorplay.flag" };

    static ZjzwWatchRunner()
    {
        _tuningDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Tuning").Replace('\\', '/');
        EditorApplication.update += Update;
        // 每次编译域重载都会走到这里——用它确认「编辑器已编译到哪个版本」（对比 Tuning/stamp.py check）
        Debug.Log("[BuildStamp] 编译版本 " + BuildStamp.Version + "（看门狗挂载）");
        Debug.Log("[ZjzwWatchRunner] 看门狗已挂载：写 Tuning/zjzw_editorplay.flag 或 qxxs_editorplay.flag 即自动开跑");
    }

    private static string FindTrigger()
    {
        for (int i = 0; i < TriggerFlags.Length; i++)
        {
            string p = Path.Combine(_tuningDir, TriggerFlags[i]);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Update()
    {
        // 远程编译请求（Agent 改完代码后写 Tuning/compile.flag）：Play 中编辑器持续 tick，
        // 无需窗口焦点即可刷新+编译（根治「每次都要点 Unity」）；编辑器空闲失焦时仍需 focus_unity.py 唤醒
        string cf = Path.Combine(_tuningDir, "compile.flag");
        if (File.Exists(cf))
        {
            try { File.Delete(cf); } catch { }
            Debug.Log("[ZjzwWatchRunner] 检测到编译请求 flag，刷新资源…");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return;
        }

        // 远程退出 Play（Play 卡死时写 Tuning/exitplay.flag）
        string ex = Path.Combine(_tuningDir, "exitplay.flag");
        if (EditorApplication.isPlaying && File.Exists(ex))
        {
            string why = "";
            try { why = File.ReadAllText(ex); } catch { }
            try { File.Delete(ex); } catch { }
            Debug.Log("[ZjzwWatchRunner] 检测到退出请求 flag，停止 Play（" + why.Trim() + "）");
            EditorApplication.isPlaying = false;
            return;
        }

        // 通用演示结束兜底（仅非批次会话）：手动跑的演示结束后游戏停在结算/录像界面
        // 等玩家按键，无人值守即"卡死"。检测 DemoDriver Finished 持续 5 秒 → 自动停 Play。
        // 批次会话（_triggered）由 Tuner 自己收尾，不干预。
        if (!_triggered && EditorApplication.isPlaying && !EditorApplication.isCompiling)
        {
            DemoDriver drv = UnityEngine.Object.FindObjectOfType<DemoDriver>();
            bool fin = false;
            if (drv != null)
            {
                System.Reflection.FieldInfo f = typeof(DemoDriver).GetField("_state",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object st = f != null ? f.GetValue(drv) : null;
                fin = st != null && st.ToString() == "Finished";
            }
            if (fin)
            {
                if (_finSince < 0) _finSince = EditorApplication.timeSinceStartup;
                else if (EditorApplication.timeSinceStartup - _finSince > 5.0)
                {
                    _finSince = -1;
                    Debug.Log("[ZjzwWatchRunner] 非批次会话演示已结束 5s，自动停止 Play（防挂在结算/录像界面）");
                    EditorApplication.isPlaying = false;
                    return;
                }
            }
            else
            {
                _finSince = -1;
            }
        }
        else if (!EditorApplication.isPlaying)
        {
            _finSince = -1;
        }

        // 压线墙重建请求（Agent 改完墙体数据后写 <项目>_rebuild.flag）：重建后自动接力触发跑批
        if (!EditorApplication.isPlaying && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
        {
            string rb = Path.Combine(_tuningDir, "qxxs_rebuild.flag");
            if (File.Exists(rb))
            {
                try { File.Delete(rb); } catch { }
                Debug.Log("[ZjzwWatchRunner] 检测到重建 flag，重新生成曲线行驶压线墙…");
                QxxsSidelineBuilder.Build();
                EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity");
                Debug.Log("[ZjzwWatchRunner] 压线墙重建完成，接力触发跑批");
                File.WriteAllText(Path.Combine(_tuningDir, "qxxs_editorplay.flag"), "rebuild");
                return;
            }
        }

        if (!_triggered && !EditorApplication.isPlaying && !EditorApplication.isCompiling
            && !EditorApplication.isUpdating)
        {
            string trigger = FindTrigger();
            if (trigger != null)
            {
                // <项目>_editorplay.flag → <项目>_autostart.done
                _activeDonePath = Path.Combine(_tuningDir, Path.GetFileName(trigger).Replace("_editorplay.flag", "_autostart.done"));
                try { File.Delete(trigger); } catch { }
                _triggered = true;
                Debug.Log("[ZjzwWatchRunner] 检测到触发 flag（" + Path.GetFileName(trigger) + "），进入 Play 模式");
                EditorApplication.isPlaying = true;
                return;
            }
        }

        // Play 中检测到 done 标记 → 停止 Play。_activeDonePath 允许为空：编译错误等异常
        // 会打断域重载、丢失触发记忆，此时靠扫描两个项目的 done 标记兜底（健壮性修复）
        if (EditorApplication.isPlaying && !EditorApplication.isCompiling)
        {
            string[] doneFiles = { "zjzw_autostart.done", "qxxs_autostart.done" };
            for (int i = 0; i < doneFiles.Length; i++)
            {
                string donePath = Path.Combine(_tuningDir, doneFiles[i]);
                if (!File.Exists(donePath)) continue;
                bool mine = donePath == _activeDonePath;
                string reason = "";
                try { reason = File.ReadAllText(donePath); } catch { }
                Debug.Log("[ZjzwWatchRunner] 收到完成标记（" + doneFiles[i] + (mine ? "" : "，非本次触发·兜底退出") + "）：" + reason.Trim() + "，停止 Play");
                try { File.Delete(donePath); } catch { }
                if (mine) _activeDonePath = null;
                _triggered = false;
                EditorApplication.isPlaying = false;
                break;
            }
        }
    }
}
