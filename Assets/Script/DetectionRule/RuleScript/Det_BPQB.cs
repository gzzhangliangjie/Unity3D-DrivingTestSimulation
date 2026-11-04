using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;

//半坡起步实时监测脚本
public class Det_BPQB : MonoBehaviour
{
    [Header("前后保险杠检测")]
    public float Ab = 0;
    private const float Ab_Max = 2.9f;
    private const float Ab_Min = 1.7f;

    [Header("边距30公分")]
    public float Bc = 0;
    private const float Bc_Min = 2.4f;
    private const float Bc_Max = 2.7f;

    [Header("停车点")]
    public Transform point_carbarn;

    [Header("空气墙")]
    public GameObject AirWall;

    [Tooltip("考试倒计时")]
    private float timer = 30f;
    public Text textTimer;

    [Header("考试流程（全图考试由 ExamFlow 逐项激活）")]
    [Tooltip("未激活时：不计时、不判中途停车、不做定点判定。单科练习恒为 true")]
    public bool armed = true;
    /// <summary>本项目已判定完成（定点停车到位），供 ExamFlow 推进下一项目。</summary>
    public bool IsCompleted { get; private set; }
    private float _timerMax = 30f;

    public bool detIsStop = true;//车辆在运行时停止一次仅扣一次中途停车的分
    private Transform targetPlayer;

    private void Start()
    {
        targetPlayer = GameObject.FindGameObjectWithTag("Player").transform;
    }

    /// <summary>由 ExamFlow 切换：切入本项时重置倒计时与中途停车状态，保证每项独立限时。</summary>
    public void SetArmed(bool value)
    {
        if (armed == value) return;
        armed = value;
        if (!value)
        {
            // 兜底：本项已判到位却被休眠（ExamFlow 推进），空气墙不能留到全场
            if (IsCompleted && AirWall != null) Destroy(AirWall);
            return;
        }
        timer = _timerMax;
        detIsStop = true;
    }

    enum Step
    {
        Default, Carbarn
    }
    Step step = Step.Carbarn;
    private void Update()
    {
        if (!armed) return;
        //X轴度数基准向量
        Vector3 carbarnWord_a = point_carbarn.TransformPoint(Vector3.forward);
        carbarnWord_a = carbarnWord_a - point_carbarn.position;
        //库点指向车辆
        Vector3 carbarnWord_b = targetPlayer.position - point_carbarn.position;
        //度数
        float Angle = Mathf.Acos(Vector3.Dot(carbarnWord_a.normalized, carbarnWord_b.normalized));
        //车与库点距离
        float JuLi = Vector3.Distance(targetPlayer.position, point_carbarn.position);
        //Ab用于判断车辆是否在停车位内
        Ab = Mathf.Sin(Angle) * JuLi;
        Bc = Mathf.Cos(Angle) * JuLi;

        Debug.DrawLine(targetPlayer.position, point_carbarn.position, Color.red);

        switch (step)
        {
            case Step.Carbarn:
                if (Car_Information.isStop
                    && Ab <= Ab_Max && Ab >= Ab_Min
                    && Bc <= Bc_Max && Bc >= Bc_Min)
                {
                    targetPlayer.GetComponent<VoicePromptAudio>().Play_TingCheYiDaoWei();
                    step = Step.Default;
                    IsCompleted = true;
                    // 坡顶空气墙必须当场撤掉：ExamFlow 在本项判定的同一帧就会休眠本检测器
                    // （SetArmed(false) → Update 直接 return），原「下一帧 Step.Default 再 Destroy」
                    // 永远等不到 → 全图考试里车开向曲线行驶时一头撞墙扣 100（2026-11-05 实测）。
                    if (AirWall != null) Destroy(AirWall);
                    // 定点停稳本身就是考试要求的停车，不是"中途停车"。
                    // 不清这个标志时，若车是在进入判定窗的同一帧就停稳（没有"边开边进窗"的帧把
                    // detIsStop 置 false），下面那条 Speed==0 判定会立刻扣 100（2026-11-05 实测）。
                    detIsStop = false;
                }else if(Ab <= Ab_Max && Ab >= Ab_Min && Bc <= Bc_Max && Bc >= Bc_Min)
                {
                    detIsStop = false;
                }
                break;
            case Step.Default:
                Destroy(AirWall);
                break;
        }

        if(Car_Information.Speed == 0 && detIsStop && !ExamJudge.DrivingDone)
        {
            print(string.Format("【半坡起步】中途停车，扣100分  Ab={0:F2}[{1},{2}] Bc={3:F2}[{4},{5}] pos=({6:F2},{7:F2})",
                Ab, Ab_Min, Ab_Max, Bc, Bc_Min, Bc_Max, targetPlayer.position.x, targetPlayer.position.z));
            targetPlayer.GetComponent<VoicePromptAudio>().Play_ZhongTuTingChe();
            Car_Information.DeductScore(100);
            detIsStop = false;
        }

        //项目倒计时
        if (timer <= 0)
        {
            //考试超时
            targetPlayer.GetComponent<VoicePromptAudio>().Play_KaoShiChaoShi();
            Car_Information.DeductScore(100);
            timer = 0f;
            Destroy(gameObject);
        }
        else
        {
            timer -= Time.deltaTime;
            textTimer.text = "时间：" + Mathf.Round(timer) + "s";
        }
    }
    private void OnDestroy()
    {
        textTimer.text = "时间：--";
    }
}
