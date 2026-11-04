# -*- coding: utf-8 -*-
"""编译版本戳：解决「正在跑的批次是不是最新代码」无法直视的问题。

用法：
  python Tuning/stamp.py [备注]   # 生成新版本号写入 BuildStamp.cs（每次改完 C# 跑一次）
  python Tuning/stamp.py check    # 对比 源码版本 vs 编辑器已编译版本 vs Play 中版本

原理：BuildStamp.Version 是一个 C# 常量。
  - 编辑器每次编译后域重载，ZjzwWatchRunner 静态构造函数打印 [BuildStamp]（编译版本）；
  - 每次 Play 后 RuntimeInitializeOnLoadMethod 打印（运行版本）；
  - 跑批每轮结果 JSONL 里带 ver 字段。
三者一致 = 跑的是最新代码。
"""
import io, os, re, sys, datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(ROOT, "Assets/Script/MainGUI/Tutorial/BuildStamp.cs")
META = CS + ".meta"
LOG = os.path.expanduser("~/AppData/Local/Unity/Editor/Editor.log")
GUID = "a1b2c3d4e5f60718293a4b5c6d7e8f90"  # 固定 GUID，重复生成不变

TEMPLATE = """// 由 Tuning/stamp.py 自动生成——不要手改版本号，改完代码后运行 python Tuning/stamp.py [备注]
public static class BuildStamp
{{
    public const string Version = "{ver}";

    // 每次 Play 进场景时打印，用于确认运行中的二进制版本
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void LogRuntime()
    {{
        UnityEngine.Debug.Log("[BuildStamp] 运行版本 " + Version);
    }}
}}
"""


def stamp(note=""):
    ver = "v" + datetime.datetime.now().strftime("%m%d-%H%M")
    if note:
        ver += "-" + note
    io.open(CS, "w", encoding="utf-8").write(TEMPLATE.format(ver=ver))
    if not os.path.exists(META):
        io.open(META, "w", encoding="utf-8").write(
            "fileFormatVersion: 2\nguid: %s\n" % GUID)
    print("stamped:", ver)


def src_version():
    m = re.search(r'Version = "([^"]+)"', io.open(CS, encoding="utf-8").read())
    return m.group(1) if m else "(无 BuildStamp.cs)"


def last_in_log(tag):
    if not os.path.exists(LOG):
        return "(无日志)"
    v = "(未出现)"
    for line in io.open(LOG, encoding="utf-8", errors="ignore"):
        if tag in line:
            m = re.search(tag.split("]")[0][1:] + r"\] .*?([a-z0-9]+\d{4}-\d{4}(?:-[\w\u4e00-\u9fff]*)?)", line)
            m2 = re.search(r"(v\d{4}-\d{4}(?:-[^，。\s]+)?)", line)
            if m2:
                v = m2.group(1)
    return v


def check():
    sv = src_version()
    cv = last_in_log("[BuildStamp] 编译版本")
    rv = last_in_log("[BuildStamp] 运行版本")
    print("源码版本    :", sv)
    print("已编译版本  :", cv)
    print("Play运行版本:", rv)
    ok = (cv == sv)
    print("结论        :", "编辑器已是最新 ✔" if ok else "⚠ 编辑器尚未编译最新代码（需要激活 Unity 窗口触发刷新）")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) > 1 and sys.argv[1] == "check":
        check()
    else:
        stamp(sys.argv[1] if len(sys.argv) > 1 else "")
        check()
