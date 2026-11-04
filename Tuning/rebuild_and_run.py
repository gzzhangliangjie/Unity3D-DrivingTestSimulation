# -*- coding: utf-8 -*-
"""改完 C# 后的唯一入口：打版本戳 → 强制 Unity Refresh → 等到编译版本匹配 → 跑批 → 核验结果。

用法：
  python Tuning/rebuild_and_run.py cftc-v9 Cftc
  python Tuning/rebuild_and_run.py cftc-v9 Dcrk Cftc Bpqb Zjzw Qxxs

为什么需要它（2026-11-04 事故）：
  focus_unity.py 只把窗口拉到前台，Unity 不一定重新导入资源；曾出现源码已是 v8、
  跑批实际执行 v7，白跑一轮还按 v7 的结论去改路线。本脚本把「编译版本 == 源码版本」
  设成跑批的硬前置条件，不匹配就拒绝启动，不给旧代码任何机会。

核验不只是 run_examsim.py 的 PASS（那只代表结算冻结），还会读本轮新增日志判断：
  撞墙记录是否新增 / 是否扣 100 分 / Play 运行版本是否等于源码版本。
"""
import ctypes
import io
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TUNING = os.path.join(ROOT, "Tuning")
LOG = os.path.join(TUNING, "unity_editor.log")
WALL_HIT = os.path.join(TUNING, "wall_hit.txt")
BUILD_STAMP = os.path.join(ROOT, "Assets/Script/MainGUI/Tutorial/BuildStamp.cs")

sys.path.insert(0, TUNING)
import stamp  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")


def log_size():
    return os.path.getsize(LOG) if os.path.exists(LOG) else 0


def read_since(offset):
    if not os.path.exists(LOG):
        return ""
    with io.open(LOG, "rb") as f:
        f.seek(offset)
        return f.read().decode("utf-8", "replace")


def wall_hit_lines():
    if not os.path.exists(WALL_HIT):
        return 0
    with io.open(WALL_HIT, encoding="utf-8", errors="replace") as f:
        return sum(1 for _ in f)


def focus_unity():
    """把 Unity 主窗口拉到前台，返回 hwnd；失败返回 0。"""
    u = ctypes.windll.user32
    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    targets = []

    def cb(hwnd, _lp):
        if u.IsWindowVisible(hwnd):
            n = u.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(hwnd, buf, n + 1)
            if "Unity" in buf.value and "Unity Hub" not in buf.value:
                targets.append(hwnd)
        return True

    u.EnumWindows(EnumProc(cb), None)
    if not targets:
        return 0
    hwnd = targets[0]
    u.ShowWindow(hwnd, 9)
    u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0003)
    time.sleep(0.25)
    u.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0003)
    u.keybd_event(0x12, 0, 0, 0)  # ALT 骗过前台锁
    time.sleep(0.05)
    u.SetForegroundWindow(hwnd)
    u.keybd_event(0x12, 0, 2, 0)
    time.sleep(0.4)
    return hwnd if u.GetForegroundWindow() == hwnd else 0


def send_ctrl_r():
    """Unity 编辑器 Ctrl+R = Assets/Refresh，会强制重导入 + 域重载 + 重新编译。"""
    u = ctypes.windll.user32
    u.keybd_event(0x11, 0, 0, 0)  # CTRL down
    u.keybd_event(0x52, 0, 0, 0)  # R down
    time.sleep(0.06)
    u.keybd_event(0x52, 0, 2, 0)
    u.keybd_event(0x11, 0, 2, 0)


def wait_compiled(ver, offset, timeout=150):
    """轮询日志，等到 [BuildStamp] 编译版本 == ver。"""
    tag = "[BuildStamp] 编译版本 " + ver
    deadline = time.time() + timeout
    refreshed = False
    while time.time() < deadline:
        chunk = read_since(offset)
        if tag in chunk:
            if "error CS" in chunk:
                print("[FAIL] 编译报错：")
                for line in chunk.splitlines():
                    if "error CS" in line:
                        print("   ", line.strip()[:200])
                return False
            print("[OK] 已编译:", ver)
            return True
        if not refreshed and time.time() > deadline - timeout + 6:
            # 6 秒还没自动编译 → 强制 Refresh 一次
            if focus_unity():
                send_ctrl_r()
                print("[..] 已发送 Ctrl+R 强制 Refresh")
            else:
                print("[WARN] 找不到 Unity 窗口，只能等自动刷新")
            refreshed = True
        time.sleep(1.0)
    print("[FAIL] 等待编译超时 %ss，最后日志片段：" % timeout)
    print(read_since(offset)[-1200:])
    return False


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    note, sites = sys.argv[1], sys.argv[2:]

    for flag in ("examsim_run.flag",):
        if os.path.exists(os.path.join(TUNING, flag)):
            print("[FAIL] %s 仍存在，可能上一次跑批还没结束，先等它跑完" % flag)
            return 2

    # 1) 打新版本戳（BuildStamp.cs 变化本身就会触发 Unity 重编译）
    stamp.stamp(note)
    ver = stamp.src_version()
    print("[1/5] 源码版本:", ver)
    offset = log_size()

    # 2) 聚焦 + 强制刷新 + 等编译版本匹配
    if not wait_compiled(ver, offset):
        return 3
    print("[2/5] 编译版本与源码一致 ✔")

    # 3) 编译错误检查（用真实日志，不用 AppData 里那份陈旧 Editor.log）
    r = subprocess.run([sys.executable, os.path.join(TUNING, "check_compile.py"), LOG],
                       cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print("[3/5] check_compile:", (r.stdout or "").strip()[-300:])
    if r.returncode != 0:
        print("[FAIL] 编译检查未通过，拒绝跑批")
        return 3

    hits_before = wall_hit_lines()
    offset = log_size()  # 只核验本轮新增日志

    # 4) 跑批
    print("[4/5] 跑批:", " ".join(sites))
    r = subprocess.run([sys.executable, os.path.join(TUNING, "run_examsim.py")] + sites,
                       cwd=ROOT)
    run_ok = (r.returncode == 0)

    # 5) 结果核验
    chunk = read_since(offset)
    hits_after = wall_hit_lines()
    new_hits = max(0, hits_after - hits_before)
    score_lines = [l.strip() for l in chunk.splitlines() if "[Score] 扣" in l]
    score100 = sum(1 for l in score_lines if "扣 100 分" in l)
    runtime_ok = ("[BuildStamp] 运行版本 " + ver) in chunk
    runtime_ver = [l.strip() for l in chunk.splitlines() if "[BuildStamp] 运行版本" in l]

    print("\n[5/5] ==== 核验（本轮新增日志）====")
    print("  跑批退出码      :", r.returncode, "(0=结算冻结/演示正常结束)")
    print("  运行版本        :", runtime_ver[-1] if runtime_ver else "(未出现)",
          "→", "✔ 与源码一致" if runtime_ok else "✘ 不是最新代码！")
    print("  新增撞墙记录    :", new_hits, "条 (跑前 %d → 跑后 %d)" % (hits_before, hits_after))
    print("  扣分次数        :", len(score_lines), "(其中扣 100 分 %d 次)" % score100)
    for l in score_lines[:6]:
        print("      ", l[:120])
    # 100 分要求：整个批次不允许出现任何扣分（含「中途停车」扣 5 分）
    ok = run_ok and runtime_ok and new_hits == 0 and len(score_lines) == 0
    print("  结论            :", "✅ 真正通过（100 分）" if ok else "❌ 未通过（见上）")
    if not ok:
        demos = [l.strip() for l in chunk.splitlines() if "[DEMO] pos=" in l]
        if demos:
            print("  最后 3 帧       :")
            for l in demos[-3:]:
                print("   ", l[-170:])
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
