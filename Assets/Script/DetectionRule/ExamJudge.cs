/// <summary>
/// 考试判定总闸（全局静态，所有 Det_* 共用）。
///
/// 用途：演示驾驶走到路线终点正常收车时，车必然要停稳——这不是"中途停车"。
/// 但检测器只看 Speed==0，会在收车那 1 秒后判中途停车（实测直角转弯单科
/// 演示跑完后被扣 100 分）。路线走完即视为行驶结束，关掉这一类与行驶状态
/// 相关的判定；倒库/侧方/半坡的位姿判定不受影响（它们靠位姿窗，不靠停车）。
///
/// 由 DemoDriver 在开始演示时置 false、在终点收车（BeginStop finish）时置 true。
/// 每次场景重载/重新演示都会重置，不存在跨轮残留。
/// </summary>
public static class ExamJudge
{
    /// <summary>演示行驶已到终点收车：不再判"中途停车"。</summary>
    public static bool DrivingDone;
}
