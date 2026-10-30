using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// PAPI 精密进近航道指示灯单元 (S1, 纯视觉): 按相机相对本单元的仰角决定红/白。
    /// 观察仰角高于本单元设定角 → 白, 低于 → 红 (四个单元设定角围绕下滑角分布:
    /// 4 白=过高, 2 白 2 红=在航道上, 0 白=过低)。
    /// 仰角取相机到本单元的 3D 向量与水平面夹角; 每帧只算一次, 开销可忽略。
    /// </summary>
    public class PapiLight : MonoBehaviour
    {
        /// <summary>本单元设定角 (度)。</summary>
        public float SettingDeg = 3.5f;
        public Renderer LampRenderer;
        public Material WhiteMat, RedMat;

        private bool _white;
        private bool _init;

        /// <summary>当前是否显示白 (供快照/测试读取)。</summary>
        public bool IsWhite { get { return _white; } }

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Refresh(cam.transform.position);
        }

        /// <summary>按观察点位置刷新红/白 (LateUpdate 每帧调用; 编辑模式快照工具无 LateUpdate, 手动调用)。</summary>
        public void Refresh(Vector3 viewerPos)
        {
            if (LampRenderer == null) return;
            Vector3 d = viewerPos - transform.position;
            float horiz = new Vector2(d.x, d.z).magnitude;
            float elevDeg = Mathf.Atan2(d.y, Mathf.Max(horiz, 0.01f)) * Mathf.Rad2Deg;
            bool white = elevDeg > SettingDeg;
            if (_init && white == _white) return;
            _init = true;
            _white = white;
            LampRenderer.sharedMaterial = white ? WhiteMat : RedMat;
        }
    }
}
