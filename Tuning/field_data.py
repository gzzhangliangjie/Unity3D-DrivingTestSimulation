#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
场地数据获取流程（统一版 v3）
================================
设计原则（科目二实跑复盘，用户明确要求）：
  * 路线按「车道 + 库位 + 参考点」设计；碰撞体只作兜底校验，不是路线主约束。
  * 所有场地数据都要能正常获取，且覆盖全科目（Dcrk / Cftc / Bpqb / Zjzw / Qxxs）。
  * 不在 Python 里静态反算 prefab 碰撞体变换——墙几何一律取 Unity 权威导出。

数据来源（三个互相独立的来源，可交叉校验）：
  1) 墙 / 库位几何 —— Tuning/zjzw_scene_dump.txt
     （ZjzwSceneDump.cs 用 FindObjectsOfType<Wall> + Unity 原生 c.bounds 导出，覆盖全场 Wall；
      在 Unity 重跑 ZjzwSceneDump.Dump 即可刷新）。
  2) 参考点 point_left / point_carbarn —— Exam Mode.unity 静态解析（已验证可靠）。
  3) 各科路线 _wps —— DemoDriver.cs 的 Build*Path()（自动解析，用于锚点与越界校验）。

产出 Tuning/field_data.json：
  {
    "<科目>": {
      "points": {参考点名: [x, z]},
      "lane":   [x0,z0,x1,z1],   车道走廊（可行驶区）
      "slot":   [x0,z0,x1,z1],   库位口袋（可行驶区；无库位则 null）
      "route":  [[x,z], ...],    当前演示路线
      "start"/"end": [x,z],
      "check":  {越界路点列表},
      "walls":  [...]            兜底元数据
    }
  }

用法：
  python Tuning/field_data.py           # 报告 + 写 JSON
  python Tuning/field_data.py --quiet  # 仅写 JSON
"""
import os, sys, json, argparse, re, math

sys.path.insert(0, os.path.dirname(__file__))
import probe_walls_static as P

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SCENE = P.SCENE
DUMP = os.path.join(HERE, "zjzw_scene_dump.txt")
DRIVER = os.path.join(ROOT, "Assets", "Script", "MainGUI", "Tutorial", "DemoDriver.cs")
OUT = os.path.join(HERE, "field_data.json")

# 车身包络（米），用于路线越界校验
CAR_LEN = 4.5
CAR_WID = 1.8
TOL = 0.30  # 合法区容差

# ---------------------------------------------------------------------------
# 科目 ↔ 场地：锚点取各科几何中心（世界 XZ），用于把 Unity dump 的墙归类。
# 注意科目缩写：Dcrk=倒车入库(西侧 x≈-72)；Bpqb=坡道定点停车(东侧 x≈35)。
# 二者在 v2 曾被写反，导致墙/参考点整体错位，v3 已校正并与 DemoDriver 注释交叉核对。
# ---------------------------------------------------------------------------
SUBJECT_ANCHOR = {
    "Dcrk": (-72.0, -37.0),
    "Cftc": (-13.5, -29.7),
    "Bpqb": (35.0, 30.0),
    "Zjzw": (30.0, -28.0),
    "Qxxs": (27.0, 142.0),
}

# 参考点 fileID（来自 resolve_points.py；Dcrk / Bpqb 在 v3 互换归位）
SUBJECT_POINTS = {
    "Dcrk": {"point_left": 1734991517, "point_carbarn": 1419415041},
    "Cftc": {"point_left": 1134610892, "point_carbarn": 554216451},
    "Bpqb": {"point_carbarn": 116163298},
    "Zjzw": {},
    "Qxxs": {},
}

# 无字面 _wps 数组（程序生成路线）的科目，起点按 DemoDriver 注释标定
FALLBACK_START = {
    "Zjzw": (13.2, -33.7),
    "Qxxs": (41.7, 111.0),
}


# ---------------------------------------------------------------------------
# 1) 参考点：场景静态解析
# ---------------------------------------------------------------------------
def point_world(transforms, fid):
    if fid is None or fid not in transforms:
        return None
    M, _ = P.world_matrix(transforms, fid)
    if M is None:
        return None
    p = P.apply_point(M, (0, 0, 0))
    return [round(p[0], 3), round(p[2], 3)]


# ---------------------------------------------------------------------------
# 2) Unity 权威 dump：墙几何（含三角形顶点）
# ---------------------------------------------------------------------------
def parse_dump(path):
    """{wall_name: {kind, aabb:[x0,z0,x1,z1], verts:[(x,z)...], pos, rotY, active}}"""
    if not os.path.exists(path):
        sys.stderr.write("WARN: 缺少 %s —— 请先在 Unity 跑 ZjzwSceneDump.Dump\n" % path)
        return {}
    text = open(path, "r", encoding="utf-8", errors="ignore").read()
    names = {}
    for m in re.finditer(
            r"^Wall\s+(\S+)\s+pos=\(([-\d.]+),([-\d.]+)\)\s+rotY=([-\d.]+)\s+active=(\w+)",
            text, re.M):
        names[m.group(1)] = {
            "pos": (float(m.group(2)), float(m.group(3))),
            "rotY": float(m.group(4)), "active": m.group(5) == "True"}

    head = re.compile(
        r"^(\S+)\s+(MeshCollider|BOX)\s+(?:bounds|AABB)=\(([-\d.]+),([-\d.]+)\)~\(([-\d.]+),([-\d.]+)\)")
    triples = re.compile(
        r"^\s*\(([-\d.]+),([-\d.]+)\)\s+\(([-\d.]+),([-\d.]+)\)\s+\(([-\d.]+),([-\d.]+)\)")
    lines = text.splitlines()
    walls = {}
    i = 0
    while i < len(lines):
        mh = head.match(lines[i].strip())
        if not mh:
            i += 1
            continue
        name = mh.group(1)
        rec = walls.setdefault(name, {"kind": mh.group(2), "aabb": [], "verts": []})
        rec["kind"] = mh.group(2)
        rec["aabb"] = [float(mh.group(k)) for k in (3, 4, 5, 6)]
        j = i + 1
        if j < len(lines) and lines[j].strip().startswith("顶点"):
            j += 1
            while j < len(lines):
                t = triples.match(lines[j])
                if not t:
                    break
                v = [float(x) for x in t.groups()]
                rec["verts"] += [(v[0], v[1]), (v[2], v[3]), (v[4], v[5])]
                j += 1
            i = j
            continue
        i += 1

    for n, rec in walls.items():
        meta = names.get(n, {})
        rec["name"] = n
        rec["pos"] = meta.get("pos")
        rec["rotY"] = meta.get("rotY")
        rec["active"] = meta.get("active")
    return walls


# ---------------------------------------------------------------------------
# 3) 由墙顶点 Z 层剖面求「车道走廊 / 库位口袋」
#    —— 只读顶点统计，不做三角形栅格化、不反算 prefab 变换。
#    库形墙在 Z 方向有三个主层：北边线 zN / 库口线 zM / 库后墙 zS。
#    最深层(zS)的 X 跨度即库位宽度；库口线(zM)与库后墙(zS)之间是库位纵深。
# ---------------------------------------------------------------------------
def z_profile(verts, tol=0.16):
    """按 Z 聚类，返回 [(z_mean, xmin, xmax, count), ...]，按 z 升序。"""
    if not verts:
        return []
    pts = sorted(verts, key=lambda p: p[1])
    layers = []
    for x, z in pts:
        if layers and z - layers[-1][0][-1] <= tol:
            layers[-1][0].append(z)
            layers[-1][1].append(x)
        else:
            layers.append(([z], [x]))
    return [(sum(zs) / len(zs), min(xs), max(xs), len(xs)) for zs, xs in layers]


def derive_lane_slot(walls, points, start, end=None):
    """从库形 MeshCollider 求 (lane, slot, source_note)。"""
    cands = [w for w in walls
             if w["kind"] == "MeshCollider" and len(w["verts"]) >= 30]
    if not cands:
        return None, None, "无网格墙"
    w = max(cands, key=lambda r: len(r["verts"]))
    prof = z_profile(w["verts"])
    if len(prof) < 3:
        return None, None, "Z 层不足 3 层，无法判定库形"
    zS, sx0, sx1, _ = prof[0]          # 最深：库后墙
    zM = prof[1][0]                     # 库口线
    zN = prof[-1][0]                    # 最北：北边线
    x0, _, x1, _ = w["aabb"][0], w["aabb"][1], w["aabb"][2], w["aabb"][3]

    slot = [round(sx0, 3), round(zS, 3), round(sx1, 3), round(zM, 3)]

    # 车道在哪一侧：用「起点」相对北边线的位置判定（起点是场景对象，独立于路线设计）。
    #   Cftc 起点 z=-28.5 < zN=-26.62 → 车道夹在库口线与北边线之间；
    #   Dcrk 起点 z=-25.8 > zN=-30.99 → 车道在库区北侧通道。
    sz = start[1] if start else None
    if sz is not None and sz > zN:
        lane_z = (zN, sz + 4.0)
    else:
        lane_z = (zM, zN)
    # 车道是东西向长廊，X 不能只取墙宽——外扩到起终点（墙 X 只是库区段）
    xs = [v for v in (x0, x1, start[0] if start else None, end[0] if end else None)
          if v is not None]
    lane = [round(min(xs) - 2, 3), round(min(lane_z), 3),
            round(max(xs) + 2, 3), round(max(lane_z), 3)]
    note = "%s 顶点 Z 层 zS=%.2f zM=%.2f zN=%.2f" % (w["name"], zS, zM, zN)
    return lane, slot, note


def box_lane(walls, start, end):
    """BOX 墙科目（Bpqb 坡道）：用东墙 + 端墙 + 起终点求通道带。"""
    if not walls:
        return None
    # 取所有 BOX 墙 AABB 的并集边界
    x0 = min(w["aabb"][0] for w in walls)
    x1 = max(w["aabb"][2] for w in walls)
    z0 = min(w["aabb"][1] for w in walls)
    z1 = max(w["aabb"][3] for w in walls)
    # 通道在墙包围盒内、东墙以西；上下由起终点外扩
    sx = start[0] if start else x0
    ex = end[0] if end else x1
    sz = start[1] if start else z0
    ez = end[1] if end else z1
    return [round(min(sx, ex) - 2, 3), round(min(sz, ez) - 2, 3),
            round(max(sx, ex) + 2, 3), round(max(sz, ez) + 2, 3)]


# ---------------------------------------------------------------------------
# 4) DemoDriver.cs 路线解析（用于锚点、起终点、越界校验）
# ---------------------------------------------------------------------------
def parse_routes(path):
    text = open(path, "r", encoding="utf-8", errors="ignore").read()
    routes = {}
    for m in re.finditer(r"private void Build(\w+)Path\(\)", text):
        subj = m.group(1)
        if subj not in SUBJECT_ANCHOR:
            continue
        tail = text[m.end(): m.end() + 16000]
        nxt = re.search(r"private void Build\w+Path\(\)", tail)
        body = tail[:nxt.start()] if nxt else tail
        arr = re.search(r"_wps\s*=\s*new Vector3\[\]\s*\{(.*?)\};", body, re.S)
        seg = arr.group(1) if arr else ""
        pts = [(float(a), float(b)) for a, b in
               re.findall(r"new Vector3\(([-\d.]+)f?\s*,\s*0f\s*,\s*([-\d.]+)f?\)", seg)]
        if pts:
            routes[subj] = pts
    return routes


def build_zjzw_route(text):
    """复现 BuildZjzwPath() 的默认几何（参数从源码正则提取，随改动自动同步）。"""
    i = text.find("private void BuildZjzwPath()")
    if i < 0:
        return []
    seg = text[i:i + 6000]

    def val(name, dft):
        m = re.search(r"float\s+%s\s*=\s*t\s*!=\s*null\s*\?\s*t\.\w+\s*:\s*([-\d.]+)f" % name, seg)
        return float(m.group(1)) if m else dft

    entry_z = val("entryZ", -33.5)
    corner_x = val("cornerX", 33.0)
    radius = val("radius", 3.33)
    arc_step = val("arcStep", 30.0)
    exit_end_z = val("exitEndZ", -19.2)
    pts = [(13.2, -33.7), (20.0, entry_z + 0.1), (26.5, entry_z), (corner_x, entry_z)]
    steps = max(1, round(90.0 / max(5.0, arc_step)) - 1)
    for k in range(1, steps + 1):
        phi = math.radians(90.0 * k / (steps + 1))
        pts.append((corner_x + radius * math.sin(phi),
                    entry_z + radius - radius * math.cos(phi)))
    pts.append((corner_x + radius, entry_z + radius))
    exit_x = corner_x + radius
    a = entry_z + radius
    for t in (0.55, 0.78):
        pts.append((exit_x + 0.07, a + (exit_end_z - a) * t))
    pts.append((exit_x + 0.07, exit_end_z))
    return pts


def parse_qxxs_centerline(text):
    """解析 BuildQxxsPath() 的 QxxsCenterline 常量（字符串拼接的 x,z; 序列）。"""
    i = text.find("private const string QxxsCenterline")
    if i < 0:
        return []
    j = text.find("/// <summary>", i)
    seg = text[i:j if j > i else i + 12000]
    pts = [(float(a), float(b)) for a, b in
           re.findall(r"([-\d.]+),([-\d.]+)", seg)]
    return pts


# ---------------------------------------------------------------------------
# 5) 路线越界校验（含车身旋转包络）
# ---------------------------------------------------------------------------
def in_zone(x, z, zone, tol=TOL):
    if not zone:
        return False
    x0, z0, x1, z1 = zone
    return (x0 - tol) <= x <= (x1 + tol) and (z0 - tol) <= z <= (z1 + tol)


def car_corners(p, heading):
    """车中心 p、航向 heading（弧度，XZ 平面）→ 四角。"""
    c, s = math.cos(heading), math.sin(heading)
    hl, hw = CAR_LEN / 2, CAR_WID / 2
    out = []
    for dx, dz in ((hl, hw), (hl, -hw), (-hl, hw), (-hl, -hw)):
        out.append((p[0] + dx * c - dz * s, p[1] + dx * s + dz * c))
    return out


def corner_ok(c, lane, slot):
    """车身角合法：在车道内 / 库位内 / 库口过渡带内（车尾已入库、车头仍在车道）。"""
    if slot and in_zone(c[0], c[1], slot):
        return True
    if lane and in_zone(c[0], c[1], lane):
        return True
    if lane and slot:
        # 库口 X 范围内，Z 允许从库底一直连到车道北界
        if (slot[0] - TOL) <= c[0] <= (slot[2] + TOL) and \
           (slot[1] - TOL) <= c[1] <= (lane[3] + TOL):
            return True
    return False


def check_route(route, lane, slot):
    """返回越界详情：路点本身 + 车身四角（含旋转包络）。"""
    if not route or not (lane or slot):
        return {"ok": True, "issues": [], "note": "无路线或无法定合法区"}
    issues = []
    for i, p in enumerate(route):
        ok_pt = in_zone(p[0], p[1], lane) or in_zone(p[0], p[1], slot)
        if i + 1 < len(route):
            q = route[i + 1]
            h = math.atan2(q[1] - p[1], q[0] - p[0])
        elif i > 0:
            q = route[i - 1]
            h = math.atan2(p[1] - q[1], p[0] - q[0])
        else:
            h = 0.0
        bad = [c for c in car_corners(p, h) if not corner_ok(c, lane, slot)]
        if not ok_pt or bad:
            issues.append({"wp": i, "pos": [round(p[0], 2), round(p[1], 2)],
                           "center_out": not ok_pt, "body_corners_out": len(bad)})
    return {"ok": len(issues) == 0, "issues": issues,
            "note": "车身 %.1fx%.1fm，容差 %.2fm，含旋转包络" % (CAR_LEN, CAR_WID, TOL)}


# 实跑停点相对路点的系统性偏差（由 unity_editor.log 逐帧标定）。
# key = 科目，value = (从该 wp 起生效, Z 偏移 m)。用于把「静态路点合法、
# 但实跑从偏移位置出发」带入出库段扫掠校验。
EMPIRICAL_OFFSET = {"Cftc": (18, -0.50)}  # v14 实跑：出库起点比路点偏南 ≈0.5m


def check_sweep(route, lane, slot, step=0.3, offset_from=None, offset_z=0.0):
    """连续扫掠校验：相邻路点之间插值，抓「静态路点合法但中途车身扫到墙」。

    只检查路点本身看不出库口横墙（东/西翼）这类线状障碍：车身在两点之间
    转过时，角点会短暂越过 zM 且 x 落在缺口外。这里按 step 米采样车身四角。
    约束：
      - 库口横墙带 |z - zM| <= 0.25 且 x 在缺口 X 之外 → 撞东/西翼横墙
      - 角点越过车道北界 zN 或库底 zS → 压线
    """
    if not route or not slot or not lane:
        return {"ok": True, "issues": [], "note": "无库口缺口科目，跳过扫掠"}
    zM, zS, zN = slot[3], slot[1], lane[3]
    gx0, gx1 = slot[0], slot[2]
    issues, seen = [], set()

    def off(idx):
        """实跑偏移从起点最大、沿出库段线性衰减到 0（车会逐步追回路点）。"""
        if offset_from is None or idx < offset_from:
            return 0.0
        span = max(1, len(route) - 1 - offset_from)
        return offset_z * (1.0 - min(1.0, (idx - offset_from) / float(span)))

    for i in range(len(route) - 1):
        p, q = list(route[i]), list(route[i + 1])
        p[1] += off(i)
        q[1] += off(i + 1)
        h = math.atan2(q[1] - p[1], q[0] - p[0])
        dist = math.hypot(q[0] - p[0], q[1] - p[1])
        n = max(1, int(dist / step))
        for k in range(n + 1):
            t = k / n
            c = (p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)
            for cc in car_corners(c, h):
                why = None
                # 只有车身角位于墙线南侧附近 / 正跨墙线才算风险；
                # 已整体北于横墙 0.2m 以上属于安全侧，不报假阳性。
                if (zM - 0.35) <= cc[1] <= (zM + 0.05) and \
                   (cc[0] < gx0 - 0.05 or cc[0] > gx1 + 0.05):
                    why = "库口横墙"
                elif cc[1] > zN + 0.05 or cc[1] < zS - 0.05:
                    why = "北边线/库底"
                if why:
                    key = (i, why)
                    if key not in seen:
                        seen.add(key)
                        issues.append({"seg": i, "pos": [round(c[0], 2), round(c[1], 2)],
                                       "why": why})
                    break
    note = "步长 %.2fm 连续包络；库口缺口 X[%.2f,%.2f]" % (step, gx0, gx1)
    if offset_from is not None:
        note += "；wp>=%d 按实跑偏移 Z%+.2fm" % (offset_from, offset_z)
    return {"ok": len(issues) == 0, "issues": issues, "note": note}


def classify_wall(rec):
    a = rec["aabb"]
    cx, cz = (a[0] + a[2]) / 2, (a[1] + a[3]) / 2
    best, bd = None, 1e9
    for subj, (ax, az) in SUBJECT_ANCHOR.items():
        d = math.hypot(cx - ax, cz - az)
        if d < bd:
            bd, best = d, subj
    return best


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    transforms, gameobjects, colliders, wall_gos, prefabs, meshguid_of, go_prefab = P.parse_unity(SCENE)
    dump_walls = parse_dump(DUMP)
    driver_text = open(DRIVER, "r", encoding="utf-8", errors="ignore").read()
    routes = parse_routes(DRIVER)
    # Cftc 出库 = 入库倒车段逆序回放（与 DemoDriver.BuildCftcPath() 的派生规则一致）。
    # 源码里 _wps 只静态写 18 点入库主线，出库由 enter[16]→enter[6] 倒序生成，
    # 这里同步派生，保证 field_data.json 里的路线 = 实际演示路线（含出库）。
    if len(routes.get("Cftc", [])) >= 18:
        r = routes["Cftc"]
        routes["Cftc"] = r + list(reversed(r[6:17])) + [
            [-4.15, -28.70], [6.7, -28.44], [8.5, -28.44]]
    # 程序生成路线的两科：从源码几何复现，纳入同一套场地数据 / 校验
    for subj, pts in (("Zjzw", build_zjzw_route(driver_text)),
                      ("Qxxs", parse_qxxs_centerline(driver_text))):
        if pts and subj not in routes:
            routes[subj] = pts

    out = {s: {"points": {}, "lane": None, "slot": None, "route": [],
               "start": None, "end": None, "check": {}, "sweep": {},
               "walls": [], "source": ""}
           for s in SUBJECT_ANCHOR}

    # 墙归类（兜底元数据）
    grouped = {s: [] for s in SUBJECT_ANCHOR}
    for name, rec in dump_walls.items():
        grouped[classify_wall(rec)].append(rec)
    for subj, recs in grouped.items():
        for rec in recs:
            out[subj]["walls"].append({
                "name": rec["name"], "kind": rec["kind"],
                "aabb": [round(v, 3) for v in rec["aabb"]],
                "verts": len(rec["verts"]), "rotY": rec.get("rotY"),
                "active": rec.get("active")})

    # 参考点
    for subj, pmap in SUBJECT_POINTS.items():
        for pname, fid in pmap.items():
            out[subj]["points"][pname] = point_world(transforms, fid)

    # 路线
    for subj, pts in routes.items():
        out[subj]["route"] = [[round(x, 2), round(z, 2)] for x, z in pts]
        out[subj]["start"] = [round(pts[0][0], 2), round(pts[0][1], 2)]
        out[subj]["end"] = [round(pts[-1][0], 2), round(pts[-1][1], 2)]
    for subj, st in FALLBACK_START.items():
        if not out[subj]["route"]:
            out[subj]["start"] = list(st)

    # 车道 / 库位
    for subj in SUBJECT_ANCHOR:
        mesh = [w for w in grouped[subj] if w["kind"] == "MeshCollider" and len(w["verts"]) >= 30]
        if subj in ("Cftc", "Dcrk"):
            lane, slot, note = derive_lane_slot(
                mesh, out[subj]["points"], out[subj]["start"], out[subj]["end"])
            out[subj]["lane"], out[subj]["slot"] = lane, slot
            out[subj]["source"] = note
        elif subj in ("Bpqb",):
            out[subj]["lane"] = box_lane(grouped[subj], out[subj]["start"], out[subj]["end"])
            out[subj]["source"] = "BOX 墙包围盒 + 路线起终点（坡道通道）"
        else:
            # Zjzw / Qxxs：地面标线定义车道，dump 只有压线墙；用路线包络作通道，
            # 墙 AABB 作为边界元数据，不拿来反推可行驶区。
            r = routes.get(subj, [])
            if r:
                xs, zs = [p[0] for p in r], [p[1] for p in r]
                out[subj]["lane"] = [round(min(xs) - 2, 2), round(min(zs) - 2, 2),
                                     round(max(xs) + 2, 2), round(max(zs) + 2, 2)]
                out[subj]["source"] = "路线包络（地面标线车道；墙 AABB 仅兜底）"
            else:
                out[subj]["source"] = "程序生成路线，车道由地面标线定义"

    # 越界校验（路点静态包络） + 连续扫掠（路点之间的车身角轨迹）
    for subj in SUBJECT_ANCHOR:
        r = routes.get(subj, [])
        out[subj]["check"] = check_route(r, out[subj]["lane"], out[subj]["slot"])
        if subj in EMPIRICAL_OFFSET:
            frm, dz = EMPIRICAL_OFFSET[subj]
            out[subj]["sweep"] = check_sweep(r, out[subj]["lane"], out[subj]["slot"],
                                             offset_from=frm, offset_z=dz)
        else:
            out[subj]["sweep"] = check_sweep(r, out[subj]["lane"], out[subj]["slot"])

    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)

    if args.quiet:
        return
    print("=" * 78)
    print("场地数据获取（统一版 v3）  scene =", os.path.basename(SCENE))
    print("墙几何 =", os.path.basename(DUMP), "（Unity 权威导出）  路线 =", os.path.basename(DRIVER))
    print("=" * 78)
    for subj, rec in out.items():
        print("\n### %s ###" % subj)
        for pn, pv in rec["points"].items():
            print("  [点] %-16s %s" % (pn, pv if pv else "未解析"))
        if rec["lane"]:
            l = rec["lane"]
            print("  [车道] X[%s,%s] Z[%s,%s]  中线(%.2f,%.2f)" % (
                l[0], l[2], l[1], l[3], (l[0] + l[2]) / 2, (l[1] + l[3]) / 2))
        if rec["slot"]:
            s = rec["slot"]
            print("  [库位] X[%s,%s] Z[%s,%s]  宽=%.2f 深=%.2f" % (
                s[0], s[2], s[1], s[3], s[2] - s[0], s[3] - s[1]))
        print("  [来源] %s" % rec["source"])
        print("  [起点] %s  [终点] %s  路点=%d" % (rec["start"], rec["end"], len(rec["route"])))
        ck = rec["check"]
        print("  [校验] %s  %s" % ("通过" if ck["ok"] else "越界 %d 处" % len(ck["issues"]),
                                 ck["note"]))
        for it in ck["issues"][:8]:
            print("         wp%-2d %s  中心越界=%s  车身角越界=%d" % (
                it["wp"], it["pos"], it["center_out"], it["body_corners_out"]))
        sw = rec.get("sweep", {})
        if sw:
            print("  [扫掠] %s  %s" % (
                "通过" if sw["ok"] else "风险 %d 处" % len(sw["issues"]), sw["note"]))
            for it in sw["issues"][:5]:
                print("         seg%-2d %s  %s" % (it["seg"], it["pos"], it["why"]))
        print("  [墙·兜底] " + (", ".join(w["name"] for w in rec["walls"]) or "无"))
    print("\n已写", OUT)


if __name__ == "__main__":
    main()
