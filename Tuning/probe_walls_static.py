#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
静态解析 Exam Mode.unity 中所有挂 Wall.cs 的碰撞体真实世界足迹。
处理 stripped 预制体子物体：用 PrefabInstance 根节点的世界矩阵 × 模型 .obj 包围盒。
"""
import re, os, sys, math, glob

SCENE = os.path.join(os.path.dirname(__file__), "..", "Assets", "Scene", "Exam Mode.unity")
MODELDIR = os.path.join(os.path.dirname(__file__), "..", "Assets", "Model", "Car_DetectionRule")
WALL_GUID = "0c3775756661f6f46ac0afcd44c1364f"

HEADER = re.compile(r'(?m)^--- !u!(\d+) &(\d+)(?:\s+stripped)?\s*$')
def pv(s):
    m = re.search(r'\{x:\s*([-\d.eE]+),\s*y:\s*([-\d.eE]+),\s*z:\s*([-\d.eE]+)\}', s)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else (0,0,0)
def pq(s):
    m = re.search(r'\{x:\s*([-\d.eE]+),\s*y:\s*([-\d.eE]+),\s*z:\s*([-\d.eE]+),\s*w:\s*([-\d.eE]+)\}', s)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3)), float(m.group(4))) if m else (0,0,0,1)
def pf(s):
    m = re.search(r'\{fileID:\s*(\d+)\}', s)
    return int(m.group(1)) if m else None

# guid -> .obj 路径（来自 .meta 反查）
def map_guids():
    g2path = {}
    for meta in glob.glob(os.path.join(MODELDIR, "*.obj.meta")):
        guid = None
        with open(meta, "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                if "guid:" in line:
                    guid = line.split("guid:",1)[1].strip().strip()
                    break
        if guid:
            g2path[guid] = meta[:-5]  # 去掉 .meta
    return g2path

def obj_bbox(path):
    xs=[]; ys=[]; zs=[]
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            if line.startswith("v "):
                p = line.split()
                try:
                    xs.append(float(p[1])); ys.append(float(p[2])); zs.append(float(p[3]))
                except: pass
    if not xs: return None
    return (min(xs),max(xs),min(ys),max(ys),min(zs),max(zs))

def parse_unity(path):
    text = open(path, "r", encoding="utf-8", errors="ignore").read()
    matches = list(HEADER.finditer(text))
    transforms = {}; gameobjects = {}; colliders = {}; wall_gos = set()
    prefabs = {}  # fileID(1001) -> dict(transformParent, rootGO, rootLocalPos, rootLocalRot)
    meshguid_of = {}  # colliderGO -> mesh guid
    go_prefab = {}    # gameobject fid -> prefabInstance fid (stripped 子物体持有)
    for k, mk in enumerate(matches):
        typ=int(mk.group(1)); fid=int(mk.group(2))
        start=mk.end(); end=matches[k+1].start() if k+1<len(matches) else len(text)
        b=text[start:end]
        if typ==1:
            name=""; pinst=None
            for line in b.splitlines():
                if "m_Name:" in line:
                    name=line.split("m_Name:",1)[1].strip().strip('"'); break
                elif "m_PrefabInstance:" in line: pinst=pf(line)
            gameobjects[fid]=name
            if pinst is not None: go_prefab[fid]=pinst
        elif typ==4:
            d={"pos":(0,0,0),"rot":(0,0,0,1),"scale":(1,1,1),"father":None,"go":None,"name":"?"}
            for line in b.splitlines():
                if "m_LocalPosition:" in line: d["pos"]=pv(line)
                elif "m_LocalRotation:" in line: d["rot"]=pq(line)
                elif "m_LocalScale:" in line: d["scale"]=pv(line)
                elif "m_Father:" in line: d["father"]=pf(line)
                elif "m_GameObject:" in line: d["go"]=pf(line)
            transforms[fid]=d
        elif typ==65:
            go=None; isTrig=None; c=(0,0,0); s=(1,1,1)
            for line in b.splitlines():
                if "m_GameObject:" in line: go=pf(line)
                elif "m_IsTrigger:" in line: isTrig=(line.split(":")[1].strip()=="1")
                elif "m_Center:" in line: c=pv(line)
                elif "m_Size:" in line: s=pv(line)
            if go is not None: colliders.setdefault(go,[]).append(("box",isTrig,c,s))
        elif typ==64:
            go=None; isTrig=None; mg=None
            for line in b.splitlines():
                if "m_GameObject:" in line: go=pf(line)
                elif "m_IsTrigger:" in line: isTrig=(line.split(":")[1].strip()=="1")
                elif "m_Mesh:" in line:
                    mm=re.search(r'guid:\s*([0-9a-f]+)', line)
                    if mm: mg=mm.group(1)
            if go is not None:
                colliders.setdefault(go,[]).append(("mesh",isTrig,None,None))
                if mg: meshguid_of[go]=mg
        elif typ==114:
            sc=None; go=None
            for line in b.splitlines():
                if "m_Script:" in line:
                    mm=re.search(r'guid:\s*([0-9a-f]+)', line)
                    if mm: sc=mm.group(1)
                elif "m_GameObject:" in line: go=pf(line)
            if sc==WALL_GUID and go is not None: wall_gos.add(go)
        elif typ==1001:
            tp=None; rg=None; rpos=(0,0,0); rrot=(0,0,0,1)
            inmods=False; cur_path=None
            for line in b.splitlines():
                s=line.strip()
                if "m_TransformParent:" in line: tp=pf(line)
                elif "m_RootGameObject:" in line: rg=pf(line)
                elif "m_Modifications:" in line: inmods=True
                elif inmods:
                    if s.startswith("propertyPath:"):
                        cur_path=s.split(":",1)[1].strip()
                    elif s.startswith("value:"):
                        val=s.split(":",1)[1].strip()
                        try:
                            if cur_path=="m_LocalPosition.x": rpos=(float(val),rpos[1],rpos[2])
                            elif cur_path=="m_LocalPosition.y": rpos=(rpos[0],float(val),rpos[2])
                            elif cur_path=="m_LocalPosition.z": rpos=(rpos[0],rpos[1],float(val))
                        except: pass
            prefabs[fid]={"tp":tp,"rg":rg,"rpos":rpos,"rrot":rrot}
    for tfid,d in transforms.items():
        if d["go"] is not None and d["go"] in gameobjects:
            d["name"]=gameobjects[d["go"]]
    return transforms, gameobjects, colliders, wall_gos, prefabs, meshguid_of, go_prefab

def quat_to_mat(q,pos,scale):
    x,y,z,w=q; xx,yy,zz=x*x,y*y,z*z; xy,xz,yz=x*y,x*z,y*z; wx,wy,wz=w*x,w*y,w*z
    R=[[1-2*(yy+zz),2*(xy-wz),2*(xz+wy)],[2*(xy+wz),1-2*(xx+zz),2*(yz-wx)],[2*(xz-wy),2*(yz+wx),1-2*(xx+yy)]]
    return [[R[i][j]*scale[j] for j in range(3)]+[pos[i]] for i in range(3)]+[[0,0,0,1]]
def mat_mul(A,B):
    return [[sum(A[i][k]*B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def world_matrix(transforms,fid):
    M=None; cur=fid; chain=[]
    while cur is not None:
        if cur not in transforms: break
        d=transforms[cur]
        M=quat_to_mat(d["rot"],d["pos"],d["scale"]) if M is None else mat_mul(quat_to_mat(d["rot"],d["pos"],d["scale"]),M)
        chain.append(cur); cur=d["father"]
    return M,chain
def apply_point(M,p):
    x,y,z=p
    return (M[0][0]*x+M[0][1]*y+M[0][2]*z+M[0][3],
            M[1][0]*x+M[1][1]*y+M[1][2]*z+M[1][3],
            M[2][0]*x+M[2][1]*y+M[2][2]*z+M[2][3])
def footprint_of(M,bbox):
    x0,y0,z0,x1,y1,z1=bbox
    corners=[(x0,y0,z0),(x1,y0,z0),(x0,y1,z0),(x1,y1,z0),(x0,y0,z1),(x1,y0,z1),(x0,y1,z1),(x1,y1,z1)]
    xs=[]; zs=[]
    for c in corners:
        p=apply_point(M,c); xs.append(p[0]); zs.append(p[2])
    return min(xs),max(xs),min(zs),max(zs)

def find_root_transform(transforms, gameobjects, prefabs, go, go_prefab):
    # 从 wall GO 找其 PrefabInstance，取根 GO；优先用根 GO 的 Transform（非stripped），否则用 PrefabInstance m_TransformParent + rootLocalPos
    pinst_id = go_prefab.get(go)
    if pinst_id is None:
        # 该 GO 本身可能是某个 prefab 的根
        for pid,p in prefabs.items():
            if p["rg"]==go:
                pinst_id=pid; break
    if pinst_id is None or pinst_id not in prefabs:
        return None, go
    pinst = prefabs[pinst_id]
    rg = pinst["rg"]
    # 根 GO 的 Transform（非stripped）
    root_tf=None
    for t,d in transforms.items():
        if d.get("go")==rg:
            root_tf=t; break
    if rg is not None and root_tf is not None:
        M,_=world_matrix(transforms, root_tf)
        return M, rg
    # stripped 根：用 m_TransformParent 的世界矩阵 ∘ rootLocalPos
    tp=pinst["tp"]
    if tp in transforms:
        Mp,_=world_matrix(transforms, tp)
        if Mp is not None:
            Mr=quat_to_mat((0,0,0,1), pinst["rpos"], (1,1,1))
            M=mat_mul(Mr,Mp)
            sys.stderr.write("DBG go=%d tp=%d Mp0=(%g,%g,%g) rpos=%s M0=(%g,%g,%g)\n" % (go,tp,Mp[0][3],Mp[1][3],Mp[2][3],pinst["rpos"],M[0][3],M[1][3],M[2][3]))
            return M, rg
    sys.stderr.write("DBG go=%d tp=%s tp_in_transforms=%s\n" % (go, tp, tp in transforms))
    return None, rg

def main():
    g2path=map_guids()
    transforms, gameobjects, colliders, wall_gos, prefabs, meshguid_of, go_prefab = parse_unity(SCENE)
    print("Transform=%d  WallGO=%d  Prefab=%d" % (len(transforms), len(wall_gos), len(prefabs)))
    dbg=[24289438,250161808,660600825,1481059780,1896090117]
    for g in dbg:
        print("  GO %d -> prefab %s -> %s" % (g, go_prefab.get(g), prefabs.get(go_prefab.get(g)) if go_prefab.get(g) in prefabs else None))
    # 打印所有 prefab 的 rg/tp/rpos
    print("PREFABS:")
    for pid,p in prefabs.items():
        print("  pinst=%d rg=%s tp=%s rpos=%s" % (pid, p["rg"], p["tp"], p["rpos"]))
    print("="*80)
    print("所有挂 Wall.cs 的物体（世界足迹）：")
    print("="*80)
    for go in sorted(wall_gos):
        name=gameobjects.get(go,"?")
        # 优先用预制体根路径（stripped 子物体必须用这条）
        M=None
        if go in go_prefab:
            M,_=find_root_transform(transforms,gameobjects,prefabs,go,go_prefab)
        # 退化到直接 Transform
        if M is None:
            tfid=None
            for t,d in transforms.items():
                if d.get("go")==go: tfid=t; break
            if tfid is not None:
                M,_=world_matrix(transforms,tfid)
        if M is None:
            print("  [无法解析] %s go=%d" % (name,go)); continue
        center=apply_point(M,(0,0,0))
        print("%-26s go=%d  中心X=%9.3f Z=%9.3f" % (name,go,center[0],center[2]))
        for (kind,isTrig,c,s) in colliders.get(go,[]):
            trig="TRIG" if isTrig else "PHYS"
            if kind=="box":
                hx,hy,hz=s[0]/2,s[1]/2,s[2]/2
                fp=footprint_of(M,(c[0]-hx,c[1]-hy,c[2]-hz,c[0]+hx,c[1]+hy,c[2]+hz))
                print("    [%s box] X[%8.3f,%8.3f] Z[%8.3f,%8.3f]" % (trig,fp[0],fp[1],fp[2],fp[3]))
            else:
                mg=meshguid_of.get(go)
                objp=g2path.get(mg) if mg else None
                if objp and os.path.exists(objp):
                    bb=obj_bbox(objp)
                    fp=footprint_of(M,bb)
                    print("    [%s mesh %s] X[%8.3f,%8.3f] Z[%8.3f,%8.3f]" % (trig,os.path.basename(objp),fp[0],fp[1],fp[2],fp[3]))
                else:
                    print("    [%s mesh guid=%s 模型未定位]" % (trig,mg))
        print("-"*70)

if __name__=="__main__":
    main()
