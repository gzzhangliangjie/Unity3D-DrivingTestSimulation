#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
科目二·单项模拟自动跑批编排器（Agent 用）。

原理：复用 Assets/Editor/ExamSimRunner.cs 的 flag 通道——
向 Tuning/examsim_run.flag 写入项目名（Dcrk/Cftc/Bpqb/Zjzw/Qxxs），
运行器（挂在 EditorApplication.update，任意 Play 状态都响应）会：
  消费 flag → 必要时中断并重进 Play → 注入 SelectPass → 自动开演示 →
  结算后自动停 Play，并写 Tuning/examsim_run.done（含 ok:/fail: 与原因）。

本脚本按顺序把多个项目串起来跑，每跑完一个再触发下一个，最后汇总通过/失败。
纯本地文件轮询，不需要 GUI；Unity 编辑器需处于打开状态（本项目已确认在跑）。

用法（后台或前台均可）：
  python Tuning/run_examsim.py              # 默认跑 Dcrk Cftc Bpqb
  python Tuning/run_examsim.py Zjzw Qxxs   # 指定项目
"""
import os
import sys
import time

TUNING_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)))
FLAG = os.path.join(TUNING_DIR, "examsim_run.flag")
DONE = os.path.join(TUNING_DIR, "examsim_run.done")

CONSUME_TIMEOUT = 90     # 等 flag 被消费（含重编译/域重载）上限
DONE_TIMEOUT = 240       # 等单个项目演示跑完/结算上限
GAP_BETWEEN = 4          # 项目之间留点缓冲，避免抢在停 Play 中途

TERMINAL_OK = "自动停 Play"
TERMINAL_FAIL_PREFIX = "fail:"


def log(msg):
    ts = time.strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def write_flag(site):
    with open(FLAG, "w", encoding="utf-8") as f:
        f.write(site)


def wait_flag_consumed(timeout):
    """flag 文件被运行器读走即视为已消费。"""
    waited = 0
    while waited < timeout:
        if not os.path.exists(FLAG):
            return True
        time.sleep(1)
        waited += 1
    return False


def wait_done_terminal(timeout):
    """
    等待 done 文件出现并进入终态。
    注意：运行器在「演示开始」时会先写一次 ok: 演示已开始，
    在「结算/停 Play」时再写一次含 '自动停 Play' 的内容；
    fail 则以 'fail:' 开头。忽略「演示已开始」继续等。
    """
    waited = 0
    while waited < timeout:
        if os.path.exists(DONE):
            try:
                with open(DONE, "r", encoding="utf-8") as f:
                    content = f.read()
            except Exception:
                content = ""
            if TERMINAL_OK in content or content.strip().startswith(TERMINAL_FAIL_PREFIX):
                return content.strip()
        time.sleep(1)
        waited += 1
    return None


def run_one(site):
    log(f"=== 触发 {site} ===")
    # 清掉上次的 done，防止误判
    try:
        if os.path.exists(DONE):
            os.remove(DONE)
    except Exception:
        pass
    write_flag(site)
    log(f"已写 flag={site}，等待运行器消费（含重编译）…")
    if not wait_flag_consumed(CONSUME_TIMEOUT):
        log(f"[!] {site}: {CONSUME_TIMEOUT}s 内 flag 未被消费——Unity 可能未在编辑态/编译卡死，终止本批。")
        return (site, "TIMEOUT_CONSUME", "")
    log(f"{site}: flag 已消费，等待演示跑完/结算…")
    content = wait_done_terminal(DONE_TIMEOUT)
    if content is None:
        log(f"[!] {site}: {DONE_TIMEOUT}s 内无终态 done，可能演示未启动或卡死。")
        return (site, "TIMEOUT_DONE", "")
    if content.startswith(TERMINAL_FAIL_PREFIX):
        log(f"[X] {site} 失败：{content}")
        return (site, "FAIL", content)
    else:
        log(f"[√] {site} 完成：{content}")
        return (site, "PASS", content)


def main():
    sites = sys.argv[1:] or ["Dcrk", "Cftc", "Bpqb"]
    log(f"开始跑批，项目顺序：{' → '.join(sites)}")
    log(f"Tuning 目录：{TUNING_DIR}")
    results = []
    for s in sites:
        r = run_one(s)
        results.append(r)
        # 项目间缓冲
        if r[1] in ("TIMEOUT_CONSUME", "TIMEOUT_DONE"):
            log("发生超时，停止后续项目（请检查 Unity 编辑器状态）。")
            break
        log(f"--- 间隔 {GAP_BETWEEN}s ---")
        time.sleep(GAP_BETWEEN)

    log("")
    log("==== 跑批汇总 ====")
    all_pass = True
    for s, status, content in results:
        mark = {"PASS": "√", "FAIL": "X"}.get(status, "!")
        log(f"  {mark} {s}: {status}  {content}")
        if status != "PASS":
            all_pass = False
    log("全部通过 ✅" if all_pass else "存在未通过项 ⚠️")
    return 0 if all_pass else 2


if __name__ == "__main__":
    sys.exit(main())
