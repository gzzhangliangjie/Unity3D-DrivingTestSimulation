using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/*
 * 批处理工具：打开 Exam Mode 场景，对 S 弯区域拍一张正俯视截图，供对齐分析。
 * 命令行：Unity.exe -batchmode -quit -projectPath <proj> -executeMethod BatchSnapshot.Take
 */
public static class BatchSnapshot
{
    // 大范围俯视：X -7..77，Z 93..185（覆盖S弯完整走向）
    private const float CenterX = 35f;
    private const float CenterZ = 139f;
    private const float SizeZ = 46f;
    private const float Aspect = 84f / 92f;

    //置 true 时：打开场景后临时以红色渲染 Qxxs_WinWall 压线墙（仅影响本次截图，不保存场景）
    public static bool HighlightWalls = false;

    public static void Take()
    {
        EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity");

        MeshRenderer wallMr = null;
        Material wallOldMat = null;
        if (HighlightWalls)
        {
            var wall = GameObject.Find("Qxxs_WinWall");
            if (wall != null)
            {
                wallMr = wall.GetComponent<MeshRenderer>();
                wallOldMat = wallMr.sharedMaterial;
                var mat = new Material(Shader.Find("Unlit/Color")) { color = new Color(1f, 0.1f, 0.1f) };
                wallMr.sharedMaterial = mat;
                wallMr.enabled = true;
            }
        }

        var go = new GameObject("SnapshotCam");
        var cam = go.AddComponent<Camera>();
        go.transform.position = new Vector3(CenterX, 80f, CenterZ);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);//朝下，上方为 +Z
        cam.orthographic = true;
        cam.orthographicSize = SizeZ;
        cam.aspect = Aspect;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.3f, 0.5f, 0.9f);

        int w = 1680, h = 1840;
        var rt = new RenderTexture(w, h, 24);
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        System.IO.Directory.CreateDirectory("E:/DrivingTestSimulation/.workbuddy/tmp");
        System.IO.File.WriteAllBytes("E:/DrivingTestSimulation/.workbuddy/tmp/swan_snapshot.png", tex.EncodeToPNG());
        Debug.Log("SNAPSHOT_DONE " + w + "x" + h);

        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);

        if (wallMr != null)
        {
            wallMr.sharedMaterial = wallOldMat;
            wallMr.enabled = false;
        }
    }
}
