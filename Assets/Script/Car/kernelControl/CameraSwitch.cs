using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 【镜头切换】
/// </summary>
public class CameraSwitch : MonoBehaviour
{
    public GameObject[] _camera;
    private int i = 0;

    private void Start()
    {
        _camera[0].SetActive(true);
    }

    private void Update()
    {
        //按下V键-切换视觉（原作者的这段被 /* */ 注释掉了，键盘只能靠 UI 按钮切）
        if (Input.GetKeyDown(KeyCode.V))
        {
            SwitchCamera();
        }
    }

    /// <summary>
    /// 循环切换到下一个相机。
    /// 注意：FixedUpdate 里读输入会丢按键（渲染帧率下按一下就过），所以放在 Update。
    /// </summary>
    private void SwitchCamera()
    {
        if (_camera == null || _camera.Length == 0)
            return;

        _camera[i % _camera.Length].SetActive(false);
        i++;
        _camera[i % _camera.Length].SetActive(true);
    }

    /// <summary>
    /// 移动端相机切换（场景里 UI 按钮绑定的就是这个方法名，不要改）
    /// </summary>
    public void MobileTerminal_SwitchCamera()
    {
        SwitchCamera();
    }
}