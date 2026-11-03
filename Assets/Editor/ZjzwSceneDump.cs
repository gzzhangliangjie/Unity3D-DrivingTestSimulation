using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 【直角转弯场景几何导出】批处理调用：
///   Unity.exe -batchmode -quit -projectPath &lt;工程&gt; -executeMethod ZjzwSceneDump.Dump
/// 打开 Exam Mode 场景，把直角转弯区域的压线墙（Wall/BoxCollider）、
/// 起点点位、检测器的世界坐标与包围盒写到 Tuning/zjzw_scene_dump.txt。
/// </summary>
public static class ZjzwSceneDump
{
    // 直角转弯区域粗略包围盒（世界坐标，含车道与转角）
    private const float X0 = 5f, X1 = 48f, Z0 = -42f, Z1 = -10f;

    public static void Dump()
    {
        OpenSceneMode mode = OpenSceneMode.Single;
        EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity", mode);
        StringBuilder sb = new StringBuilder();

        // 1) 所有 Wall 压线墙（含未激活）
        Wall[] walls = Object.FindObjectsOfType<Wall>(true);
        sb.AppendLine("=== Wall 压线墙 ===");
        foreach (Wall w in walls)
        {
            Transform t = w.transform;
            Vector3 p = t.position;
            sb.AppendLine(string.Format("Wall {0,-26} pos=({1:F2},{2:F2}) rotY={3:F0} active={4}",
                t.name, p.x, p.z, t.eulerAngles.y, t.gameObject.activeInHierarchy));
        }

        // 2) 直角转弯区域内的所有 BoxCollider（8 顶点变换后的真实世界 AABB）
        sb.AppendLine();
        sb.AppendLine("=== 区域内 BoxCollider（真实世界 AABB，8 顶点变换）===");
        BoxCollider[] boxes = Object.FindObjectsOfType<BoxCollider>(true);
        foreach (BoxCollider bc in boxes)
        {
            Vector3 mn, mx;
            WorldAabb(bc, out mn, out mx);
            if (mx.x < X0 || mn.x > X1 || mx.z < Z0 || mn.z > Z1) continue;
            sb.AppendLine(string.Format("{0,-26} AABB=({1:F2},{2:F2})~({3:F2},{4:F2})  h={5:F1}..{6:F1}  rotY={7:F0}",
                bc.gameObject.name, mn.x, mn.z, mx.x, mx.z, mn.y, mx.y, bc.transform.eulerAngles.y));
        }

        // 2b) 所有 Wall 压线墙的碰撞体（含子物体，精确 AABB + 网格顶点）
        sb.AppendLine();
        sb.AppendLine("=== Wall 压线墙碰撞体（含子物体）===");
        foreach (Wall w in walls)
        {
            Collider[] cols = w.GetComponentsInChildren<Collider>(true);
            foreach (Collider c in cols)
            {
                BoxCollider bc = c as BoxCollider;
                if (bc != null)
                {
                    Vector3 mn, mx;
                    WorldAabb(bc, out mn, out mx);
                    sb.AppendLine(string.Format("{0,-26} BOX AABB=({1:F2},{2:F2})~({3:F2},{4:F2})  rotY={5:F0}",
                        w.name + "/" + c.name, mn.x, mn.z, mx.x, mx.z, c.transform.eulerAngles.y));
                }
                else
                {
                    sb.AppendLine(string.Format("{0,-26} {1} bounds=({2:F2},{3:F2})~({4:F2},{5:F2})",
                        w.name + "/" + c.name, c.GetType().Name, c.bounds.min.x, c.bounds.min.z, c.bounds.max.x, c.bounds.max.z));
                }

                // 网格碰撞体：导出世界坐标顶点（去重），看清每条线的实际位置
                MeshCollider mc = c as MeshCollider;
                if (mc != null && mc.sharedMesh != null)
                {
                    Vector3[] verts = mc.sharedMesh.vertices;
                    sb.AppendLine("    顶点(" + verts.Length + "):");
                    for (int i = 0; i < verts.Length; i += 3)
                    {
                        Vector3 a = c.transform.TransformPoint(verts[i]);
                        Vector3 b = i + 1 < verts.Length ? c.transform.TransformPoint(verts[i + 1]) : a;
                        Vector3 d = i + 2 < verts.Length ? c.transform.TransformPoint(verts[i + 2]) : a;
                        sb.AppendLine(string.Format("      ({0:F2},{1:F2}) ({2:F2},{3:F2}) ({4:F2},{5:F2})",
                            a.x, a.z, b.x, b.z, d.x, d.z));
                    }
                }
            }
        }

        // 3) 关键点位：Zjzw 起点 / End / Victory / JinDu / Det_ZJZW
        sb.AppendLine();
        sb.AppendLine("=== 关键对象 ===");
        DumpNamed(sb, "Zjzw");
        foreach (string n in new string[] { "End", "Victory", "JinDu" })
        {
            GameObject g = GameObject.Find(n);
            if (g != null) sb.AppendLine(n + "  pos=" + Fmt(g.transform.position) + " rotY=" + g.transform.eulerAngles.y.ToString("F0"));
        }
        Det_ZJZW det = Object.FindObjectOfType<Det_ZJZW>(true);
        if (det != null) sb.AppendLine("Det_ZJZW  pos=" + Fmt(det.transform.position));

        // 4) 玩家车碰撞体尺寸（估算转弯扫掠用）
        sb.AppendLine();
        sb.AppendLine("=== 玩家车碰撞体 ===");
        DrivingKernel car = Object.FindObjectOfType<DrivingKernel>(true);
        if (car != null)
        {
            sb.AppendLine("car root: " + car.name + " pos=" + Fmt(car.transform.position));
            Collider[] ccs = car.GetComponentsInChildren<Collider>(true);
            foreach (Collider c in ccs)
            {
                BoxCollider b2 = c as BoxCollider;
                if (b2 != null)
                {
                    Vector3 mn, mx;
                    WorldAabb(b2, out mn, out mx);
                    sb.AppendLine(string.Format("  {0,-22} BOX localCenter={1} localSize={2} worldAABB=({3:F2},{4:F2})~({5:F2},{6:F2})",
                        c.name, b2.center, b2.size, mn.x, mn.z, mx.x, mx.z));
                }
                else
                {
                    sb.AppendLine("  " + c.name + " " + c.GetType().Name + " bounds=" + c.bounds);
                }
            }
        }

        string dir = Path.Combine(Application.dataPath, "..", "Tuning");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "zjzw_scene_dump.txt");
        File.WriteAllText(path, sb.ToString());
        Debug.Log("[ZjzwSceneDump] 已导出 " + walls.Length + " 堵墙 → " + path);
        EditorApplication.Exit(0);
    }

    /// <summary>BoxCollider 8 顶点变换到世界坐标后的真实 AABB（对任意旋转/缩放都正确）</summary>
    private static void WorldAabb(BoxCollider bc, out Vector3 mn, out Vector3 mx)
    {
        Transform t = bc.transform;
        Vector3 c = bc.center, h = bc.size * 0.5f;
        mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = c + new Vector3(
                (i & 1) == 0 ? -h.x : h.x,
                (i & 2) == 0 ? -h.y : h.y,
                (i & 4) == 0 ? -h.z : h.z);
            Vector3 wp = t.TransformPoint(corner);
            mn = Vector3.Min(mn, wp);
            mx = Vector3.Max(mx, wp);
        }
    }

    private static void DumpNamed(StringBuilder sb, string name)
    {
        GameObject g = GameObject.Find(name);
        if (g != null) sb.AppendLine(name + "  pos=" + Fmt(g.transform.position) + " rotY=" + g.transform.eulerAngles.y.ToString("F0"));
        else sb.AppendLine(name + "  <场景中未找到（可能未激活）>");
    }

    private static string Fmt(Vector3 v)
    {
        return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2") + ")";
    }
}
