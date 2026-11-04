# -*- coding: utf-8 -*-
"""编译报错检查：编译后必须跑一次。发现 error CS 立即非零退出并打印，供 Agent 及时处理。

用法:
  python Tuning/check_compile.py            # 检查当前编辑器日志（AgentLaunch.log 优先，退回 Editor.log）
  python Tuning/check_compile.py <log路径>   # 指定日志
退出码: 0=无报错, 1=有编译报错, 2=找不到日志
"""
import os
import re
import sys

CANDIDATES = [
    os.path.expandvars(r"%LOCALAPPDATA%\Unity\Editor\Editor.log"),
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Logs", "AgentLaunch.log"),
]


def find_logs():
    if len(sys.argv) > 1:
        return [sys.argv[1]]
    return [p for p in CANDIDATES if os.path.isfile(p)]


def _scan_one(log):
    try:
        with open(log, "rb") as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            f.seek(max(0, size - 400_000))
            tail = f.read().decode("utf-8", errors="replace")
    except OSError as e:
        print("check_compile: 读取失败 %s: %s" % (log, e))
        return []
    # 只看最近一次成功域重载之后的内容：成功重载意味着该次编译无报错，
    # 之前的旧报错不再计入（避免上次失败、本次成功时误报）
    markers = [m.start() for m in re.finditer(
        r"BuildStamp\] 编译版本|Reloading assemblies after (?:finishing|forced)", tail)]
    if markers:
        tail = tail[markers[-1]:]
    return re.findall(r"^.*error CS\d+.*$", tail, re.MULTILINE)


def _compile_clean(log):
    """该日志是否显示最近一次编译成功（存在「编译版本」戳记、且其后无 error CS）。

    「编译版本」戳记由看门狗静态构造函数在域重载成功后打印；编译若失败，
    程序集不会重载、看门狗不会运行、也就没有这枚戳记。故「有戳记且无后续报错」= 干净。
    """
    try:
        with open(log, "rb") as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            f.seek(max(0, size - 400_000))
            tail = f.read().decode("utf-8", errors="replace")
    except OSError:
        return False
    stamps = [m.start() for m in re.finditer(r"BuildStamp\] 编译版本", tail)]
    if not stamps:
        return False
    return re.search(r"error CS\d+", tail[stamps[-1]:]) is None


def main():
    logs = find_logs()
    if not logs:
        print("check_compile: 找不到编辑器日志")
        return 2
    # 优先：任一日志显示「最近编译成功」即视为已编译干净。
    # 以编译流水线日志（AgentLaunch.log，含 CompileScripts 计时与编译版本戳记）为准；
    # Editor.log 可能与实际编译不同步、残留过期报错，不作为否决依据。
    for log in logs:
        if _compile_clean(log):
            print("check_compile: OK（%s 显示最近编译成功）" % os.path.basename(log))
            return 0
    # 否则合并所有日志的报错上报（任一日志在「最近成功编译」之后仍有 error CS 才计入）
    seen, uniq = set(), []
    for log in logs:
        for e in _scan_one(log):
            if e not in seen:
                seen.add(e)
                uniq.append(e)
    if uniq:
        print("check_compile: 发现 %d 条编译报错（扫描 %d 个日志）" % (len(uniq), len(logs)))
        for e in uniq[:10]:
            print("  " + e.strip())
        return 1
    print("check_compile: OK，无编译报错（%s）" % ", ".join(os.path.basename(p) for p in logs))
    return 0


if __name__ == "__main__":
    sys.exit(main())
