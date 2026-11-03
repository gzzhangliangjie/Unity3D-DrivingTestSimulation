// 由 Tuning/stamp.py 自动生成——不要手改版本号，改完代码后运行 python Tuning/stamp.py [备注]
public static class BuildStamp
{
    public const string Version = "v1104-0105-lock65-zjzw";

    // 每次 Play 进场景时打印，用于确认运行中的二进制版本
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void LogRuntime()
    {
        UnityEngine.Debug.Log("[BuildStamp] 运行版本 " + Version);
    }
}
