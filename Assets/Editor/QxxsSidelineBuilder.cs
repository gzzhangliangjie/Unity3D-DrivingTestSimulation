using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/*
 * 曲线行驶（S弯）压线墙生成器：
 * 把从考场俯视图提取的两条边线折线（世界坐标）生成为 2m 高的连续竖直墙网格，
 * 挂 Wall 脚本实现压线扣分。与其它科目（侧方/倒库/直角/半坡）的 collider_*.obj 同一套机制。
 *
 * 菜单：驾考工具 -> 1.生成曲线行驶压线墙（写入并保存场景）
 *       驾考工具 -> 2.渲染压线墙验证截图（不保存场景）
 *
 * 数据来源：.workbuddy/tmp/s_lines_final.json（对 Exam Mode 俯视截图做局部对比度分割 + 骨架化得到）
 */
public static class QxxsSidelineBuilder
{
    private const float WallHeight = 2.0f;   //与其它科目压线墙同高
    private const float YBase = 0.02f;       //路面高度

    //两条边线折线，格式：line1||line2，每条线内分号分隔的 x,z 对
    private const string LinesData = "10.45,150.40;10.45,149.40;10.45,148.40;10.45,147.40;10.45,147.15;10.45,146.15;10.45,145.15;10.45,144.15;10.45,143.15;10.50,142.15;10.65,141.20;10.85,140.25;11.15,139.35;11.55,138.50;12.05,137.65;12.65,136.85;13.35,136.10;14.10,135.40;14.90,134.85;15.75,134.40;16.65,134.00;17.55,133.75;18.50,133.55;19.50,133.45;20.50,133.45;21.50,133.55;22.40,133.80;23.30,134.10;24.20,134.50;25.05,135.00;25.85,135.60;26.60,136.30;27.30,137.05;27.80,137.85;28.30,138.70;28.70,139.60;28.95,140.50;29.15,141.45;29.20,142.45;29.30,143.45;29.50,144.40;29.90,145.25;30.45,146.05;31.15,146.80;31.95,147.35;32.85,147.75;33.80,147.95;34.80,148.00;35.75,147.85;36.65,147.50;37.50,147.05;38.25,146.40;38.90,145.60;39.30,144.75;39.60,143.85;39.70,142.85;39.70,141.85;39.70,140.85;39.70,139.85;39.70,138.85;39.70,138.35;39.70,138.10||14.65,148.50;14.66,149.50;14.68,150.30;14.70,151.00;14.65,147.45;14.65,147.20;14.65,146.20;14.65,145.20;14.65,144.20;14.65,143.20;14.65,142.20;14.90,141.25;15.25,140.35;15.75,139.55;16.45,138.85;17.25,138.30;18.10,137.90;19.00,137.65;20.00,137.60;21.00,137.70;21.90,138.00;22.75,138.45;23.55,139.10;24.20,139.85;24.65,140.70;24.95,141.60;25.10,142.60;25.15,143.60;25.30,144.55;25.55,145.50;25.80,146.40;26.25,147.25;26.75,148.05;27.40,148.85;28.05,149.60;28.80,150.25;29.60,150.75;30.45,151.25;31.35,151.60;32.25,151.90;33.20,152.05;34.20,152.15;35.20,152.10;36.20,152.00;37.15,151.80;38.05,151.45;38.90,151.05;39.70,150.55;40.50,149.95;41.25,149.25;41.90,148.50;42.45,147.70;42.90,146.85;43.30,146.00;43.55,145.10;43.75,144.15;43.85,143.15;43.85,142.15;43.85,141.15;43.85,140.15;43.85,139.15;43.85,138.40;43.85,138.15";
    // 注意：不要再加直线「延长墙」补齐北段——弯道顶端真实白线已向西弯折（line1/line2 的弧线段覆盖），
    // 直线延长会悬在走廊中间/外侧的空地上（无白线处），车正常过弯必撞，表现为「还没压线就扣分」。

    [MenuItem("驾考工具/1-生成曲线行驶压线墙")]
    public static void Build()
    {
        EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity");

        var old = GameObject.Find("Qxxs_WinWall");
        if (old != null)
            Object.DestroyImmediate(old);//重复执行时先清理

        var root = new GameObject("Qxxs_WinWall");

        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();
        int segments = 0;
        foreach (string lineStr in LinesData.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pairs = lineStr.Split(';');
            var pts = new Vector3[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] xz = pairs[i].Split(',');
                pts[i] = new Vector3(float.Parse(xz[0]), YBase, float.Parse(xz[1]));
            }
            int start = verts.Count;
            for (int i = 0; i < pts.Length; i++)
            {
                verts.Add(pts[i]);
                verts.Add(pts[i] + Vector3.up * WallHeight);
                if (i > 0)
                {
                    int b = start + (i - 1) * 2;
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                    tris.Add(b + 1); tris.Add(b + 3); tris.Add(b + 2);
                    segments++;
                }
            }
        }

        var mesh = new Mesh { name = "QxxsSidelineMesh" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        //网格存成资产，MeshCollider 引用才能跨场景保存
        const string assetPath = "Assets/Model/Car_DetectionRule/QxxsSidelineMesh.asset";
        AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(mesh, assetPath);

        var mf = root.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mc = root.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
        mc.convex = false;
        root.AddComponent<Wall>();
        //默认不渲染（游戏里不可见）；编辑器中勾选 MeshRenderer 可查看墙体
        var mr = root.AddComponent<MeshRenderer>();
        mr.enabled = false;
        mr.sharedMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");

        EditorSceneManager.MarkSceneDirty(root.scene);
        EditorSceneManager.SaveScene(root.scene);
        Debug.Log("QXXS_BUILD_DONE segments=" + segments + " verts=" + verts.Count);
    }

    [MenuItem("驾考工具/2-渲染压线墙验证截图")]
    public static void SnapshotWithWalls()
    {
        BatchSnapshot.HighlightWalls = true;
        BatchSnapshot.Take();//复用俯视截图（临时以红色渲染压线墙，不保存场景）
        BatchSnapshot.HighlightWalls = false;
        Debug.Log("QXXS_SNAPSHOT_DONE");
    }
}
