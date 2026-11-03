using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 档位控制类
/// </summary>
public class GearsDangWei_Control : MonoBehaviour
{
    [Tooltip("四个档位按钮")]
    public GameObject GearLever;
    [Tooltip("四个档位按钮")]
    public Transform P_point, R_point, N_point, D_point;
    [Tooltip("四个档位字体text")]
    public Text P_text, R_text, N_text, D_text;
    public iTween.EaseType type;
    [Tooltip("档位回档速度")]
    public float Sleep = 1;

    public enum currentGears
    {
        Default,P, R, N, D
    }
    public currentGears gears = currentGears.Default;
    private bool flag = false;//防止重复点第二次

    private void Start()
    {
        P_text.color = Color.green;//默认P档绿色
    }

    public void P()
    {
        SwitchTo(currentGears.P, P_point);
    }
    public void R()
    {
        SwitchTo(currentGears.R, R_point);
    }
    public void N()
    {
        SwitchTo(currentGears.N, N_point);
    }
    public void D()
    {
        SwitchTo(currentGears.D, D_point);
    }

    /// <summary>
    /// 切换档位。原来四个方法里各有一句 "if (flag) return;"，
    /// 换档动画（默认 1 秒）播放期间的所有点击/按键会被直接吞掉，
    /// 键盘 1~4 连按时体感就是"按了没反应"。
    /// 这里改成打断上一次动画、以最后一次输入为准。
    /// </summary>
    private void SwitchTo(currentGears target, Transform point)
    {
        gears = target;
        flag = true;
        iTween.Stop(GearLever);
        iTween.MoveTo(GearLever, iTween.Hash(
            "position", point.position,
            "time", Sleep,
            "easetype", type,
            "oncomplete", "Fun",//Fun回调函数名字
            "oncompletetarget", gameObject
        ));
    }

    private void Fun()
    {
        SetTextColor();
        flag = false;
    }

    private void SetTextColor()
    {
        P_text.color = Color.white;
        D_text.color = Color.white;
        R_text.color = Color.white;
        N_text.color = Color.white;
        switch (gears)
        {
            case currentGears.P:
                P_text.color = Color.green;
                break;
            case currentGears.D:
                D_text.color = Color.green;
                break;
            case currentGears.R:
                R_text.color = Color.green;
                break;
            case currentGears.N:
                N_text.color = Color.green;
                break;
        }
    }
}
