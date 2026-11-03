# -*- coding: utf-8 -*-
"""直角转弯自动调参工具（配合 Unity 里的 ZjzwTuner 使用，零第三方依赖）

用法（在项目根目录运行）：
  python Tuning/tune_zjzw.py gen              # 随机生成 12 组候选写入 Tuning/zjzw_tuning.json
  python Tuning/tune_zjzw.py gen --n 20       # 生成 20 组
  python Tuning/tune_zjzw.py best             # 分析 Tuning/zjzw_results.jsonl，排名并导出最优参数
  python Tuning/tune_zjzw.py plot             # 把每轮轨迹画成 SVG 对比图（Tuning/zjzw_plot.svg）
  python Tuning/tune_zjzw.py reset            # 清空结果文件（重新跑批次前用）

完整闭环（只开一次 Unity 工程）：
  1. python Tuning/tune_zjzw.py gen
  2. Unity 打开工程 → 考试模式 → 直角转弯单项练习 → 按 T（批次自动连续跑完）
  3. python Tuning/tune_zjzw.py best / plot
  4. 把 zjzw_best.json 里的参数写回 zjzw_tuning.json 的 candidates（或手改）再按 T，
     期间 Unity 一直开着——改的是 Assets 外的 JSON，不会触发重编译
"""
import argparse
import json
import math
import os
import random
import sys

BASE = os.path.dirname(os.path.abspath(__file__))
TUNING_JSON = os.path.join(BASE, "zjzw_tuning.json")
RESULTS_JSONL = os.path.join(BASE, "zjzw_results.jsonl")
BEST_JSON = os.path.join(BASE, "zjzw_best.json")
PLOT_SVG = os.path.join(BASE, "zjzw_plot.svg")

# 车辆/场景常量（与 DemoDriver / DrivingKernel 一致）
MIN_RADIUS = 2.6 / math.tan(math.radians(38.0))  # 3.33：轴距 2.6m / tan(38° 打死方向)

DEFAULT_TUNE = {
    "entryZ": -33.5, "cornerX": 33.0, "radius": 3.33, "arcStep": 30, "exitEndZ": -19.2,
    "lookForward": 3.0, "steerGain": 2.4, "steerSpeedFwd": 2.4, "arriveDist": 2.2,
    "throttleMax": 0.8, "vApproach": 9, "vCorner": 6, "vExit": 9, "maxRoundSeconds": 45,
}

# 随机候选的采样范围（gen 用；核心变量优先，其余固定减少噪声）
RANGES = {
    "cornerX": (32.0, 34.2),
    "radius": (3.35, 3.8),
    "lookForward": (2.2, 3.8),
    "steerGain": (1.8, 3.0),
    "vCorner": (5.0, 7.0),
}


def _utf8_stdout():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass


# ----------------------------------------------------------------------
# 生成候选
# ----------------------------------------------------------------------
def cmd_gen(args):
    rng = random.Random(args.seed)
    candidates = []
    for i in range(args.n):
        t = dict(DEFAULT_TUNE)
        t["cornerX"] = round(rng.uniform(*RANGES["cornerX"]), 1)
        t["radius"] = round(rng.uniform(*RANGES["radius"]), 2)
        t["lookForward"] = round(rng.uniform(*RANGES["lookForward"]), 1)
        t["steerGain"] = round(rng.uniform(*RANGES["steerGain"]), 1)
        t["vCorner"] = round(rng.uniform(*RANGES["vCorner"]), 0)
        t["arcStep"] = rng.choice([20, 25, 30])
        t["id"] = i + 1
        t["note"] = "随机组：cornerX=%.1f R=%.2f look=%.1f gain=%.1f vC=%.0f" % (
            t["cornerX"], t["radius"], t["lookForward"], t["steerGain"], t["vCorner"])
        candidates.append(t)
    batch = {"repeat": args.repeat, "timeScale": args.timeScale, "candidates": candidates}
    with open(TUNING_JSON, "w", encoding="utf-8") as f:
        json.dump(batch, f, ensure_ascii=False, indent=2)
    print("已生成 %d 组候选 -> %s（repeat=%d timeScale=%.1f）" % (
        len(candidates), TUNING_JSON, args.repeat, args.timeScale))
    print("Unity 里按 T 开始批次；跑完后运行 best 分析")


# ----------------------------------------------------------------------
# 读取结果
# ----------------------------------------------------------------------
def load_results():
    rounds = []
    if not os.path.exists(RESULTS_JSONL):
        return rounds
    with open(RESULTS_JSONL, "r", encoding="utf-8") as f:
        for ln in f:
            ln = ln.strip()
            if not ln:
                continue
            try:
                rounds.append(json.loads(ln))
            except ValueError:
                pass  # 跳过写坏的行（域重载瞬间写入被截断等）
    return rounds


def build_ref_path(tune):
    """复刻 DemoDriver.BuildZjzwPath 的几何，生成参考路径点列表 [(x,z), ...]"""
    t = {**DEFAULT_TUNE, **{k: v for k, v in tune.items() if k in DEFAULT_TUNE}}
    entryZ, cornerX, R = t["entryZ"], t["cornerX"], t["radius"]
    arcStep, exitEndZ = t["arcStep"], t["exitEndZ"]
    wps = [(13.2, -33.7), (20.0, entryZ + 0.1), (26.5, entryZ), (cornerX, entryZ)]
    steps = max(1, round(90.0 / max(5.0, arcStep)) - 1)
    for i in range(1, steps + 1):
        phi = math.radians(90.0 * i / (steps + 1))
        wps.append((cornerX + R * math.sin(phi), entryZ + R - R * math.cos(phi)))
    wps.append((cornerX + R, entryZ + R))
    exitX = cornerX + R + 0.07
    exitStartZ = entryZ + R
    wps.append((exitX, exitStartZ + (exitEndZ - exitStartZ) * 0.55))
    wps.append((exitX, exitStartZ + (exitEndZ - exitStartZ) * 0.78))
    wps.append((exitX, exitEndZ))
    return wps


# ----------------------------------------------------------------------
# 分析排名 + 导出最优
# ----------------------------------------------------------------------
def cmd_best(_args):
    rounds = load_results()
    if not rounds:
        print("没有结果可分析（%s 不存在或为空）——先在 Unity 里按 T 跑批次" % RESULTS_JSONL)
        return

    by_id = {}
    for r in rounds:
        by_id.setdefault(r.get("id", -1), []).append(r)

    rows = []
    for tid, rs in by_id.items():
        passes = [r for r in rs if r.get("outcome") == "pass"]
        fails = [r for r in rs if r.get("outcome") != "pass"]
        row = {
            "id": tid,
            "rounds": len(rs),
            "pass": len(passes),
            "passRate": len(passes) / len(rs),
            "avgMaxDev": (sum(r.get("maxDev", 0.0) for r in passes) / len(passes)) if passes else None,
            "avgSeconds": (sum(r.get("seconds", 0.0) for r in passes) / len(passes)) if passes else None,
            "failReasons": sorted(set(r.get("outcome", "?") for r in fails)),
            "tune": rs[0].get("tune") or {},
        }
        rows.append(row)

    # 评分：合格率降序 -> 平均最大偏差升序 -> 平均用时升序（偏差/用时取 None 最大，排到最后）
    def key(row):
        dev = row["avgMaxDev"] if row["avgMaxDev"] is not None else 1e9
        sec = row["avgSeconds"] if row["avgSeconds"] is not None else 1e9
        return (-row["passRate"], dev, sec)

    rows.sort(key=key)

    print()
    print("共 %d 轮结果，%d 组候选。排名（合格率 > 轨迹偏差 > 用时）：" % (len(rounds), len(rows)))
    print()
    print("| 排名 | 组# | 轮数 | 合格率 | 平均偏差(m) | 平均用时(s) | 失败原因 | 关键参数 |")
    print("|---|---|---|---|---|---|---|---|")
    for rank, row in enumerate(rows, 1):
        t = row["tune"]
        kp = "cornerX=%.1f R=%.2f look=%.1f gain=%.1f vC=%.0f" % (
            t.get("cornerX", 0), t.get("radius", 0), t.get("lookForward", 0),
            t.get("steerGain", 0), t.get("vCorner", 0))
        dev = "%.2f" % row["avgMaxDev"] if row["avgMaxDev"] is not None else "-"
        sec = "%.1f" % row["avgSeconds"] if row["avgSeconds"] is not None else "-"
        reasons = ",".join(row["failReasons"]) if row["failReasons"] else "-"
        print("| %d | #%s | %d | %.0f%% | %s | %s | %s | %s |" % (
            rank, row["id"], row["rounds"], row["passRate"] * 100, dev, sec, reasons, kp))

    if rows and rows[0]["passRate"] > 0:
        best = rows[0]
        best_tune = {**DEFAULT_TUNE, **best["tune"]}
        best_tune["id"] = best["id"]
        best_tune["note"] = "tune_zjzw.py best 选出（合格率 %.0f%%，平均偏差 %s m）" % (
            best["passRate"] * 100,
            "%.2f" % best["avgMaxDev"] if best["avgMaxDev"] is not None else "-")
        out = {
            "best": best_tune,
            "stats": {
                "rounds": len(rounds), "passRate": best["passRate"],
                "avgMaxDev": best["avgMaxDev"], "avgSeconds": best["avgSeconds"],
            },
            "ranking": [
                {"id": r["id"], "passRate": r["passRate"], "avgMaxDev": r["avgMaxDev"],
                 "avgSeconds": r["avgSeconds"]} for r in rows[:5]],
        }
        with open(BEST_JSON, "w", encoding="utf-8") as f:
            json.dump(out, f, ensure_ascii=False, indent=2)
        print()
        print("最优参数已导出 -> %s（组#%d）" % (BEST_JSON, best["id"]))
        print("用法：把 best 字段的参数贴回 zjzw_tuning.json 的 candidates 再按 T 精调，")
        print("     或直接改 DemoDriver.BuildZjzwPath 的默认值固化。")
    else:
        print()
        print("没有合格轮：放宽候选范围（调大 RANGES / 改 zjzw_tuning.json）后 reset + 重跑")


# ----------------------------------------------------------------------
# 轨迹对比图（SVG，无第三方依赖）
# ----------------------------------------------------------------------
def cmd_plot(_args):
    rounds = load_results()
    if not rounds:
        print("没有结果可画——先在 Unity 里按 T 跑批次")
        return

    # 视口：x 10..42（东），z -36..-16（北在上）
    X0, X1, Z0, Z1 = 10.0, 42.0, -36.0, -16.0
    W, H = 960.0, 600.0
    PAD = 40.0

    def px(x):
        return PAD + (x - X0) / (X1 - X0) * (W - 2 * PAD)

    def py(z):
        return PAD + (Z1 - z) / (Z1 - Z0) * (H - 2 * PAD)

    palette = ["#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4",
               "#008080", "#9a6324", "#800000", "#808000", "#000075",
               "#f79583", "#2b8a00"]

    def color_for(tid):
        return palette[int(tid) % len(palette)]

    svg = []
    svg.append('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" font-family="sans-serif">' % (int(W), int(H)))
    svg.append('<rect width="100%%" height="100%%" fill="white"/>')
    # 网格（每 5 米）
    for gx in range(10, 45, 5):
        svg.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="#dddddd" stroke-width="1"/>' % (
            px(gx), py(Z0), px(gx), py(Z1)))
        svg.append('<text x="%.1f" y="%d" font-size="11" fill="#999999" text-anchor="middle">%d</text>' % (px(gx), int(H) - 12, gx))
    for gz in range(-35, -15, 5):
        svg.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="#dddddd" stroke-width="1"/>' % (
            px(X0), py(gz), px(X1), py(gz)))
        svg.append('<text x="%d" y="%.1f" font-size="11" fill="#999999" text-anchor="end">%d</text>' % (int(PAD) - 6, py(gz) + 4, gz))

    # 每组的参考路径（浅色虚线）
    seen_ids = set()
    for r in rounds:
        tid = r.get("id", -1)
        if tid in seen_ids or not r.get("tune"):
            continue
        seen_ids.add(tid)
        wps = build_ref_path(r["tune"])
        pts = " ".join("%.1f,%.1f" % (px(x), py(z)) for x, z in wps)
        svg.append('<polyline points="%s" fill="none" stroke="%s" stroke-width="1.5" stroke-dasharray="6,4" opacity="0.45"/>' % (pts, color_for(tid)))

    # 每轮实际轨迹（失败=虚线）
    for r in rounds:
        trace = r.get("trace") or []
        if len(trace) < 4:
            continue
        tid = r.get("id", -1)
        ok = r.get("outcome") == "pass"
        pts = " ".join("%.1f,%.1f" % (px(trace[i]), py(trace[i + 1])) for i in range(0, len(trace) - 1, 2))
        dash = "" if ok else ' stroke-dasharray="4,3"'
        svg.append('<polyline points="%s" fill="none" stroke="%s" stroke-width="2"%s opacity="%.2f"/>' % (
            pts, color_for(tid), dash, 0.9 if ok else 0.5))
        # 结束点标记：成功=实心圆，失败=叉
        ex, ez = trace[-2], trace[-1]
        if ok:
            svg.append('<circle cx="%.1f" cy="%.1f" r="4" fill="%s"/>' % (px(ex), py(ez), color_for(tid)))
        else:
            svg.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="2.5"/>' % (
                px(ex) - 4, py(ez) - 4, px(ex) + 4, py(ez) + 4, color_for(tid)))
            svg.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="2.5"/>' % (
                px(ex) - 4, py(ez) + 4, px(ex) + 4, py(ez) - 4, color_for(tid)))

    # 起点与 End 触发器
    svg.append('<circle cx="%.1f" cy="%.1f" r="6" fill="none" stroke="#333333" stroke-width="2"/>' % (px(13.2), py(-33.7)))
    svg.append('<text x="%.1f" y="%.1f" font-size="12" fill="#333333">起点</text>' % (px(13.2) + 10, py(-33.7) - 8))
    svg.append('<rect x="%.1f" y="%.1f" width="8" height="8" fill="#333333"/>' % (px(39.98) - 4, py(-27.67) - 4))
    svg.append('<text x="%.1f" y="%.1f" font-size="12" fill="#333333">End</text>' % (px(39.98) + 8, py(-27.67)))

    # 图例
    ly = PAD + 4
    svg.append('<text x="%d" y="%.1f" font-size="13" fill="#222222">轨迹对比（虚线=参考路径，叉=失败终点）</text>' % (int(W) - 250, PAD - 18))
    legend_ids = sorted(seen_ids)
    for tid in legend_ids[:12]:
        svg.append('<line x1="%d" y1="%.1f" x2="%d" y2="%.1f" stroke="%s" stroke-width="3"/>' % (
            int(W) - 245, ly + 4, int(W) - 225, ly + 4, color_for(tid)))
        svg.append('<text x="%d" y="%.1f" font-size="12" fill="#222222">#%s</text>' % (int(W) - 218, ly + 8, tid))
        ly += 18

    svg.append("</svg>")
    with open(PLOT_SVG, "w", encoding="utf-8") as f:
        f.write("\n".join(svg))
    print("轨迹图已导出 -> %s（%d 轮，%d 组）" % (PLOT_SVG, len(rounds), len(legend_ids)))


# ----------------------------------------------------------------------
def cmd_reset(_args):
    if os.path.exists(RESULTS_JSONL):
        os.remove(RESULTS_JSONL)
        print("已删除 %s（Unity 里按 T 即从第 1 轮重新跑）" % RESULTS_JSONL)
    else:
        print("结果文件本来就不存在")


def main():
    _utf8_stdout()
    parser = argparse.ArgumentParser(description="直角转弯自动调参工具")
    sub = parser.add_subparsers(dest="cmd", required=True)
    p_gen = sub.add_parser("gen", help="随机生成候选参数")
    p_gen.add_argument("--n", type=int, default=12, help="候选组数（默认 12）")
    p_gen.add_argument("--repeat", type=int, default=1, help="每组重复轮数（默认 1）")
    p_gen.add_argument("--timeScale", type=float, default=1.0, help="调参轮加速倍率（默认 1.0）")
    p_gen.add_argument("--seed", type=int, default=None, help="随机种子（复现同一批候选）")
    p_gen.set_defaults(func=cmd_gen)
    sub.add_parser("best", help="分析结果排名并导出最优参数").set_defaults(func=cmd_best)
    sub.add_parser("plot", help="画轨迹对比 SVG").set_defaults(func=cmd_plot)
    sub.add_parser("reset", help="清空结果文件").set_defaults(func=cmd_reset)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
