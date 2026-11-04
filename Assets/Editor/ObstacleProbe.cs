using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 【障碍物探针】扫描 Exam Mode 场景中指定矩形区域（世界坐标 x/z）内的所有碰撞体，
/// 输出名称/类型/尺寸/世界位置，用于自动演示路线的绕障校准（杆子、锥桶等非 Wall 障碍）。
///
/// 触发方式（两种）：
/// 1. 编辑器启动后自动跑一次，用默认区域（Dcrk 车道段）；
/// 2. 写 Tuning/probe_area.flag（内容可空 = 用默认区域，或 "x1,z1,x2,z2"）→
///    编辑器下一帧消费并删除 flag、重新扫描——改 txt 即可反复探测，无需重编译。
/// </summary>
[InitializeOnLoad]
public static class ObstacleProbe
{
    private const string FlagRel = "Tuning/probe_area.flag";
    private static bool _started;
    private static float _x1 = -92f, _z1 = -34f, _x2 = -64f, _z2 = -22f; // 默认：Dcrk 车道段（全科首轮卡点附近）

    static ObstacleProbe()
    {
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        // Play 中不跑：探针会 SyncTransforms / 甚至 OpenScene，中途干扰正在跑的演示批次
        if (EditorApplication.isPlaying) return;
        string root = System.IO.Directory.GetParent(Application.dataPath).FullName;
        string flag = System.IO.Path.Combine(root, FlagRel);

        if (!System.IO.File.Exists(flag)) return;   // 只认 flag，不再于编辑器启动时自动跑一轮
        string area = System.IO.File.ReadAllText(flag).Trim();
        try { System.IO.File.Delete(flag); } catch { }
        _started = true;

        if (!string.IsNullOrEmpty(area))
        {
            var parts = area.Split(new[] { ',', ' ', ';', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[0].ToUpper() == "G")
            {
                // G 模式：G,x1,z1,x2,z2[,step,stepZ] —— 网格向下 raycast，输出每格地面材质的
                // 归属（路面/草坪/其它），用于校准"连接段别压草坪"。
                float gx1 = float.Parse(parts[1]), gz1 = float.Parse(parts[2]);
                float gx2 = float.Parse(parts[3]), gz2 = float.Parse(parts[4]);
                float st = parts.Length >= 6 ? float.Parse(parts[5]) : 2f;
                float stz = parts.Length >= 7 ? float.Parse(parts[6]) : st;
                RunGround(root, gx1, gz1, gx2, gz2, st, stz);
                return;
            }
            if (parts.Length >= 4 && parts[0].ToUpper() == "R")
            {
                // R 模式：R,y,fromX,fromZ,toX,toZ[,step] —— 沿直线以 0.1m 步进做 raycast，
                // 输出每步从 (x,z) 沿给定方向打到 length 的命中情况，用于测墙的开口位置。
                float ry = float.Parse(parts[1]);
                float fx = float.Parse(parts[2]), fz = float.Parse(parts[3]);
                float tx = float.Parse(parts[4]), tz = float.Parse(parts[5]);
                float dirx = 0, dirz = 0, len = 2f;
                if (parts.Length >= 8)
                {
                    dirx = float.Parse(parts[6]); dirz = float.Parse(parts[7]);
                    if (parts.Length >= 9) len = float.Parse(parts[8]);
                }
                RunRays(root, ry, fx, fz, tx, tz, dirx, dirz, len);
                return;
            }
            if (parts.Length >= 4)
            {
                float.TryParse(parts[0], out _x1);
                float.TryParse(parts[1], out _z1);
                float.TryParse(parts[2], out _x2);
                float.TryParse(parts[3], out _z2);
            }
        }
        Run(root);
    }

    /// <summary>
    /// 沿 (fromX,fromZ)→(toX,toZ) 直线每 0.25m 取一个探针点，从 (x, ry, z) 沿 (dirx,dirz) 方向
    /// raycast len 米，输出命中（W=墙）/未命中（.）的序列，用于测库房 mesh 的开口位置。
    /// dirx/dirz 均为 0 时默认沿 +z（向南）打。
    /// </summary>
    private static void RunRays(string root, float ry, float fx, float fz, float tx, float tz,
                                float dirx, float dirz, float len)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.name != "Exam Mode")
            scene = EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity", OpenSceneMode.Single);
        Physics.SyncTransforms();

        float dx = tx - fx, dz = tz - fz;
        float total = Mathf.Sqrt(dx * dx + dz * dz);
        int steps = Mathf.Max(1, Mathf.CeilToInt(total / 0.25f));
        if (dirx == 0 && dirz == 0) dirz = 1f;
        float dl = Mathf.Sqrt(dirx * dirx + dirz * dirz);
        dirx /= dl; dirz /= dl;

        var sb = new StringBuilder();
        sb.AppendFormat("[ObstacleProbe-RAY] 线 ({0:F1},{1:F1})->({2:F1},{3:F1}) y={4:F1} dir=({5:F1},{6:F1}) len={7:F1} 步长0.25m：\n",
            fx, fz, tx, tz, ry, dirx, dirz, len);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            float px = fx + dx * t, pz = fz + dz * t;
            var ray = new Ray(new Vector3(px, ry, pz), new Vector3(dirx, 0, dirz));
            var hits = Physics.RaycastAll(ray, len);
            float best = float.MaxValue;
            string name = null;
            foreach (var h in hits)
            {
                if (h.collider.name.StartsWith("Plane")) continue;
                if (h.distance < best) { best = h.distance; name = h.collider.name; }
            }
            if (name != null)
                sb.AppendFormat("  ({0:F2},{1:F2}) W@{2:F2} {3}\n", px, pz, best, name);
            else
                sb.AppendFormat("  ({0:F2},{1:F2}) .\n", px, pz);
        }
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 地面归属网格扫描：每个格点向上方 y=20 向下 raycast，取最上层"Plane*"地面瓦片的材质名，
    /// 归类成 路面(R)/草坪(G)/其它(?) 并输出字符网格，用于判断连接段是否压草坪。
    /// 输出文件：Tuning/ground_probe.txt
    /// </summary>
    private static void RunGround(string root, float x1, float z1, float x2, float z2, float step, float stepZ)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.name != "Exam Mode")
            scene = EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity", OpenSceneMode.Single);
        Physics.SyncTransforms();

        var sb = new StringBuilder();
        sb.AppendFormat("# 地面归属扫描 x[{0:F1},{1:F1}] z[{2:F1},{3:F1}] step=({4:F1},{5:F1})\n", x1, x2, z1, z2, step, stepZ);
        sb.AppendLine("# 图例：R=路面  G=草坪  ?=其它/未命中   行=z（自南向北），列=x（自西向东）");
        // 表头：x 坐标
        sb.Append("      ");
        for (float x = x1; x <= x2 + 0.01f; x += step) sb.AppendFormat("{0,4:F0}", x);
        sb.AppendLine();

        var kinds = new System.Collections.Generic.Dictionary<string, string>();
        for (float z = z1; z <= z2 + 0.01f; z += stepZ)
        {
            sb.AppendFormat("{0,5:F0} ", z);
            for (float x = x1; x <= x2 + 0.01f; x += step)
            {
                var ray = new Ray(new Vector3(x, 20f, z), Vector3.down);
                var hits = Physics.RaycastAll(ray, 40f);
                string cell = "?";
                float bestY = float.MinValue;
                foreach (var h in hits)
                {
                    if (!h.collider.name.StartsWith("Plane")) continue;
                    if (h.point.y <= bestY) continue;
                    bestY = h.point.y;
                    cell = ClassifyGround(h.collider, kinds);
                }
                sb.AppendFormat("{0,4}", cell);
            }
            sb.AppendLine();
        }
        // 材质->分类 明细，便于核对
        sb.AppendLine();
        sb.AppendLine("# 材质分类明细");
        foreach (var kv in kinds) sb.AppendFormat("  {0} => {1}\n", kv.Key, kv.Value);

        string path = System.IO.Path.Combine(root, "Tuning/ground_probe.txt").Replace('\\', '/');
        try { System.IO.File.WriteAllText(path, sb.ToString()); } catch (System.Exception e) { Debug.LogWarning("GroundProbe write failed: " + e.Message); }
        Debug.Log("[ObstacleProbe-G] 地面归属已写入 " + path);
    }

    /// <summary>把地面瓦片按材质名归类：含 grass/cao/lvhua/caoping = 草坪；含 road/lu/didao/asphalt = 路面。</summary>
    private static string ClassifyGround(Collider col, System.Collections.Generic.Dictionary<string, string> kinds)
    {
        string mat = "(no-renderer)";
        var r = col.GetComponent<Renderer>();
        if (r != null && r.sharedMaterial != null) mat = r.sharedMaterial.name;

        string low = (col.name + " " + mat).ToLowerInvariant();
        string kind;
        if (low.Contains("grass") || low.Contains("cao") || low.Contains("lvhua") || low.Contains("lawn")) kind = "G";
        else if (low.Contains("road") || low.Contains("didao") || low.Contains("asphalt") || low.Contains("lu_") || low.Contains("street")) kind = "R";
        else kind = "R"; // 默认按路面算，未知材质在明细里能看出来

        string key = col.name + " | " + mat;
        if (!kinds.ContainsKey(key)) kinds[key] = kind;
        return kinds[key];
    }

    private static void Run(string root)    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.name != "Exam Mode")
            scene = EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity", OpenSceneMode.Single);

        var sb = new StringBuilder();
        sb.AppendFormat("[ObstacleProbe] 区域 x[{0:F1},{1:F1}] z[{2:F1},{3:F1}] 命中 {4} 个碰撞体：\n",
            _x1, _x2, _z1, _z2, 0);
        int n = 0;
        foreach (var go in scene.GetRootGameObjects())
            Walk(go.transform, sb, ref n);
        sb.Replace("命中 0 个碰撞体", "命中 " + n + " 个碰撞体");
        Debug.Log(sb.ToString());
    }

    private static void Walk(Transform t, StringBuilder sb, ref int n)
    {
        // 地面瓦片（Plane*）满地都是，过滤掉，只看真正的障碍
        if (!t.name.StartsWith("Plane"))
        {
            foreach (var col in t.GetComponents<Collider>())
            {
                var b = col.bounds;
                if (b.max.x < _x1 || b.min.x > _x2 || b.max.z < _z1 || b.min.z > _z2) continue;
                n++;
                var box = col as BoxCollider;
                var cap = col as CapsuleCollider;
                var sph = col as SphereCollider;
                string desc;
                if (box != null)
                    desc = string.Format("Box size={0} center={1}", box.size, box.center);
                else if (cap != null)
                    desc = string.Format("Capsule r={0:F2} h={1:F2} dir={2} center={3}", cap.radius, cap.height, cap.direction, cap.center);
                else if (sph != null)
                    desc = string.Format("Sphere r={0:F2} center={1}", sph.radius, sph.center);
                else
                    desc = string.Format("Mesh bounds={0}", b.size);
                sb.AppendFormat("  [{0}] \"{1}\" pos=({2:F2},{3:F2}) {4} enabled={5} activeGO={6}\n",
                    col.GetType().Name, t.name, t.position.x, t.position.z, desc,
                    col.enabled, t.gameObject.activeInHierarchy);
            }
        }
        foreach (Transform c in t) Walk(c, sb, ref n);
    }
}
