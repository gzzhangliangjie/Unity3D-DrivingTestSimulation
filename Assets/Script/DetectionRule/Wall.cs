using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/*
 * 压线判定：车辆碰到本碰撞体（车身或任一车轮碰撞体接触都算）即扣100分
 *
 * 判定依据 collision.rigidbody（碰撞体所属刚体 = 整车根节点，tag = Player），
 * 不能用 collision.gameObject.tag —— 车轮 WheelCollider 挂在未打 tag 的子物体上，
 * 车轮先压线时旧写法不会触发。
 *
 * 曲线行驶的压线墙由多段碰撞体沿边线排布（Qxxs_WinWall，单一 MeshCollider），
 * 同一次压线会连续接触：用静态时间戳做全局冷却，保证一次压线只扣一次 100 分。
 */
public class Wall : MonoBehaviour
{
    private static float s_lastDeductTime = -999f;
    private const float DeductCooldown = 1.5f;

    private bool hasDeducted = false;//同一段线只扣一次

    private void OnCollisionEnter(Collision collision)
    {
        if (hasDeducted)
            return;
        if (collision.rigidbody == null || !collision.rigidbody.CompareTag("Player"))
            return;

        hasDeducted = true;

        bool firstHit = Time.time - s_lastDeductTime >= DeductCooldown;
        s_lastDeductTime = Time.time;

        if (firstHit)
        {
            Car_Information.DeductScore(100);

            //压线语音提示（VoicePromptAudio 挂在车根节点上）。
            //车上 OnCollisionEnter 会先播碰撞音，同一个 AudioSource 会互相覆盖，延迟 0.6s 再播压线提示
            VoicePromptAudio vpa = collision.rigidbody.GetComponent<VoicePromptAudio>();
            if (vpa != null)
                vpa.StartCoroutine(PlayYaXianDelayed(vpa));
        }

        //销毁碰撞体，压线后不再阻挡车辆
        Destroy(gameObject);
    }

    private static IEnumerator PlayYaXianDelayed(VoicePromptAudio vpa)
    {
        yield return new WaitForSeconds(0.6f);
        vpa.Play_YaXian_100();
    }

    //编辑器中选中时画出本物体上所有 BoxCollider 的线框，方便把压线墙对齐到路边线
    private void OnDrawGizmosSelected()
    {
        BoxCollider[] boxes = GetComponents<BoxCollider>();
        if (boxes.Length == 0)
            return;
        Gizmos.color = Color.yellow;
        Gizmos.matrix = transform.localToWorldMatrix;
        for (int i = 0; i < boxes.Length; i++)
        {
            Gizmos.DrawWireCube(boxes[i].center, boxes[i].size);
        }
    }
}
