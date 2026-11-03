using UnityEngine;

/// <summary>路线阶段（决定转向律与目标车速）</summary>
public enum RoutePhase { Entry, Turn, Exit, Overshoot }

/// <summary>一帧的路线决策结果</summary>
public struct RouteOutput
{
    public float steer;         // 归一化转向 -1..1（负=左）
    public float speed;         // 目标车速（KM/H）
    public RoutePhase phase;    // 当前阶段
    public float radiusReq;     // 按当前位置/航向算出的"落进口道中心"所需半径
    public float cte;           // 横向偏差（相对当前车道中心线，米；北/东为正）
    public float headingErr;    // 航向误差（度）
    public bool late;           // 打晚了（所需半径 < 最小转弯半径）
    public string note;         // 调整原因（遥测用）
}

/// <summary>
/// 【直角转弯·几何闭环路线】不再用"到某个 x 就打死"的固定提前量，
/// 而是每帧按车辆真实位姿反解：要多大的转弯半径才能正好落进出口道中心线。
///
/// 原理（左转 90°：向东 → 向北）：
///   从 (x,z,yaw) 出发、半径 R 的左转弧，转正（航向=北）时的落点 x = x + R·(1-cos yaw)。
///   要求落点 = 出口道中心 exitX，于是
///       R = (exitX - x) / (1 - cos yaw)
///   再由自行车模型 δ = atan(L/R) 反出方向盘转角。
/// 这个式子逐帧自洽：偏离越大，下一帧算出的 R 就把车拉回来，不需要任何固定提前量。
///
/// 约束：
/// - R 不能小于最小转弯半径（满舵）→ 小于说明"打晚了"，只能打满，出口段再纠；
/// - R 大于 Rmax 说明还早（或已经被顶偏）→ 沿入口道中线继续直行，等窗口到了再转，
///   避免早早画一个大弧撞向内侧直角。
/// </summary>
public class ZjzwRouteCfg
{
    public float entryZ = -33.5f;      // 入口车道中心线 z（场景压线墙 -35.35 / -31.6 的中线）
    public float exitX = 36.42f;       // 出口车道中心线 x（场景压线墙 34.41 / 38.44 的中线）
    public float exitEndZ = -19.2f;    // 出口终点 z（End 触发器前）
    public float wheelBase = 2.6f;     // 轴距（DrivingKernel.wheelBase）
    public float maxSteerDeg = 38f;    // 满舵车轮转角（DrivingKernel.steerAngle）
    public float radiusMax = 3.2f;     // 允许的最大转弯半径（再大就说明还不到转弯窗口）；实测 3.6 会早打 0.4m 内切
    public float vEntry = 6f;          // 入口直道车速（匀速原则：考试不主动加速，全程恒定弯速）
    public float vCorner = 6f;         // 弯中车速（= 标定弯速：满舵侧偏在此速度下标定，弧线可复现）
    public float vExit = 6f;           // 出口直道车速
    public float laneGain = 0.16f;     // 车道保持增益（度误差 → 归一化转向比例）
    public float turnDoneYaw = 30f;    // 航向小于该值视为已出弯（度）：交回出口道中线跟踪

    // ---- 实车标定量（物理模型和真车对不上的两个偏差，靠跑批扫出来）----
    // 满舵时车轮实际转角比自行车模型更紧，车身会向弯内切 ~0.4m，所以：
    public float entryLaneOffset = 0f; // 入口段贴外侧走（米，正=向南侧/外线偏移）
    public float turnBias = 0f;        // 落点向东留余量（米，正=少打一点方向、落点偏东）

    public float MinRadius
    {
        get { return wheelBase / Mathf.Tan(maxSteerDeg * Mathf.Deg2Rad); }
    }
}

public static class ZjzwRoute
{
    /// <summary>按当前位姿算这一帧的转向/车速/阶段（纯函数，无状态，可逐帧调用）</summary>
    public static RouteOutput Plan(Vector3 pos, float yawDeg, ZjzwRouteCfg c)
    {
        RouteOutput o = new RouteOutput();
        float yaw = NormYaw(yawDeg);
        float rMin = c.MinRadius;
        float dx = c.exitX - pos.x;
        float laneZ = c.entryZ - c.entryLaneOffset;   // 入口段实际跟的车道 z

        if (yaw > c.turnDoneYaw)
        {
            // 按"落点几何"反解半径（turnBias 让落点略微偏东，补偿满舵内切）
            float oneMinusCos = Mathf.Max(1f - Mathf.Cos(yaw * Mathf.Deg2Rad), 0.08f);
            float rReq = (dx + c.turnBias) / oneMinusCos;
            o.radiusReq = rReq;

            if (rReq <= c.radiusMax + c.turnBias)
            {
                // 到了转弯窗口：按反解半径打方向
                o.phase = RoutePhase.Turn;
                o.late = (dx / oneMinusCos) < rMin - 0.05f;
                if (o.late) { o.phase = RoutePhase.Overshoot; o.note = "打晚了：打满，出口段再纠正"; }
                o.steer = -SteerForRadius(Mathf.Max(rReq, rMin), c);   // 左转为负
                o.speed = c.vCorner;
                o.cte = pos.z - laneZ;
            }
            else if (yaw > 60f)
            {
                // 半径要得太大 = 还没到转弯窗口（或已被顶偏）：沿入口道中线走，别提前画弧
                o.phase = RoutePhase.Entry;
                o.cte = pos.z - laneZ;
                o.headingErr = yaw - 90f;
                Vector3 aim = new Vector3(pos.x + 8f, pos.z, laneZ);
                o.steer = PurePursuitSteer(pos, aim, yaw, c);
                o.speed = dx < 7f ? Mathf.Lerp(c.vCorner, c.vEntry, Mathf.Clamp01((dx - 3f) / 4f)) : c.vEntry;
                if (Mathf.Abs(o.cte) > 0.8f) o.note = Mathf.Abs(o.cte) > 1.4f ? "偏离车道，回中线" : "微偏修正";
            }
            else
            {
                // 已经转过 30°以上：几何上"落点弧"已不成立，只能把这一把方向打完，
                // 出弯后再按出口道中线纠偏——不要为了凑落点回正方向（那是原地画龙）
                o.phase = RoutePhase.Turn;
                o.steer = -SteerForRadius(rMin, c);
                o.speed = c.vCorner;               // 弯中匀速：不再压到 5（低侧偏内切撞墙的元凶之一）
                o.cte = pos.x - c.exitX;
                o.note = "弯中改出口优先：继续打满，出弯收线";
            }
        }
        else
        {
            // 已转正：沿出口道中线收口（横向偏差由速度方向修正）
            o.phase = RoutePhase.Exit;
            o.cte = pos.x - c.exitX;
            o.headingErr = yaw;
            Vector3 aim = new Vector3(c.exitX, pos.z, pos.z + 5f);   // 前瞻 5m：收口快，出弯不贴外侧墙
            o.steer = PurePursuitSteer(pos, aim, yaw, c);
            o.speed = c.vExit;
            if (Mathf.Abs(o.cte) > 0.6f) o.note = "出口横向偏差，纠偏";
        }
        return o;
    }

    /// <summary>按目标转弯半径反解方向盘转角（归一化 0..1，恒正；左转时取负）</summary>
    public static float SteerForRadius(float radius, ZjzwRouteCfg c)
    {
        float deg = Mathf.Atan(c.wheelBase / Mathf.Max(radius, 0.5f)) * Mathf.Rad2Deg;
        return Mathf.Clamp01(deg / c.maxSteerDeg);
    }

    /// <summary>
    /// 纯跟踪转向：δ = atan(2·L·sin(α) / Ld)，α = 目标点方位与车头夹角，Ld = 到目标点距离。
    /// 符号与 DrivingKernel 一致：正=右打，负=左打。
    /// </summary>
    public static float PurePursuitSteer(Vector3 pos, Vector3 aim, float yawDeg, ZjzwRouteCfg c)
    {
        float yaw = NormYaw(yawDeg);
        float aimYaw = AimYaw(pos, aim, yaw);
        float alpha = AngleDiff(aimYaw, yaw) * Mathf.Deg2Rad;
        float ld = Mathf.Max(FlatDist(pos, aim), 1.0f);
        float deltaDeg = Mathf.Atan(2f * c.wheelBase * Mathf.Sin(alpha) / ld) * Mathf.Rad2Deg;
        return Mathf.Clamp(deltaDeg / c.maxSteerDeg, -1f, 1f);
    }

    private static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>车辆到目标点的期望航向（度，本场景 东=90 北=0）</summary>
    private static float AimYaw(Vector3 pos, Vector3 target, float curYaw)
    {
        float dx = target.x - pos.x;
        float dz = target.z - pos.z;
        if (Mathf.Abs(dx) < 0.001f && Mathf.Abs(dz) < 0.001f) return curYaw;
        return Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
    }

    /// <summary>航向差 a-b 归一到 [-180,180]</summary>
    public static float AngleDiff(float a, float b)
    {
        float d = (a - b) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return d;
    }

    public static float NormYaw(float y)
    {
        y %= 360f;
        if (y > 180f) y -= 360f;
        if (y < -180f) y += 360f;
        return y;
    }
}
