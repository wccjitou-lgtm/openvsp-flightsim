using UnityEngine;
using UnityEngine.InputSystem;

namespace FlightSim
{
    /// <summary>
    /// 飞行相机 (Phase 5 完整版, 开发提示词 7 节): 三种模式, V 键循环切换。
    ///   Chase    追尾: 位置指数平滑 + 朝向看向飞机 (含速度前瞻)
    ///   Cockpit  座舱: 相机刚连于机身, 随姿态 (第一人称)
    ///   Free     自由观察: 围绕飞机轨道 (RMB 拖动旋转, 滚轮缩放),
    ///             拉远可作地面观察者/试飞员视角
    /// 类名沿用 Phase 2 的 ChaseCamera 以保持场景序列化引用。
    /// </summary>
    public class ChaseCamera : MonoBehaviour
    {
        public enum CameraMode { Chase = 0, Cockpit = 1, Free = 2 }

        public Transform Target;

        [Header("追尾模式 (Unity 轴)")]
        public Vector3 Offset = new Vector3(0f, 2.5f, -9f);
        public float FollowSmoothing = 6f;
        [Range(0f, 1f)]
        public float VelocityLead = 0.5f;

        [Header("座舱模式 (相对飞机原点, Unity 轴)")]
        public Vector3 CockpitOffset = new Vector3(0f, 0.35f, 1.1f);

        [Header("自由观察模式")]
        public float FreeDistance = 18f;
        public float FreeAzimuthDeg = 35f;    // 绕飞机方位角
        public float FreeElevationDeg = 15f;  // 仰角
        public float FreeDistanceMin = 5f;
        public float FreeDistanceMax = 400f;

        /// <summary>当前相机模式 (公开供测试/外部切换)。</summary>
        public CameraMode Mode { get; private set; }

        private bool _snapped;   // 追尾模式首帧直接吸附
        private float _sizeFactor = 1f;   // U17: 目标尺寸因子 (4m 基准), 换机时刷新

        private void OnEnable() => _snapped = false;

        /// <summary>下一次追尾模式更新直接吸附到目标位置, 不做平滑插值
        /// (配合 AircraftController.ResetToInitial(): 飞机瞬间跳回起点时,
        /// 避免追尾相机从旧位置慢慢 Lerp 过去在地图上"飞扫"一遍)。
        /// U17: 同时刷新目标尺寸因子 — 小型机型 (如 1.2m 飞翼) 的追尾/座舱/自由
        /// 取景按比例拉近, 取代已删除的"视觉放大" hack。</summary>
        public void SnapToTarget()
        {
            _snapped = false;
            RefreshSizeFactor();
        }

        /// <summary>目标尺寸因子 = 包围盒最大展长 / 4m 基准, 钳 [0.3, 2.5]。
        /// 4m (TestUAV) = 1; 1.2m 飞翼 ≈ 0.3; 8m 大机型 = 2。</summary>
        public float SizeFactor => _sizeFactor;

        private void RefreshSizeFactor()
        {
            if (Target == null) return;
            var rends = Target.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            float span = Mathf.Max(b.size.x, b.size.z, 0.1f);
            _sizeFactor = Mathf.Clamp(span / 4f, 0.3f, 2.5f);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (WorkshopUI.IsOpen) return;   // R13
            if (kb != null && kb.vKey.wasPressedThisFrame) CycleMode();
        }

        /// <summary>循环切换相机模式 (V 键调用)。</summary>
        public void CycleMode()
        {
            Mode = (CameraMode)(((int)Mode + 1) % 3);
            _snapped = false;   // 切回追尾时重新吸附, 避免长距离 lerp
            if (Mode == CameraMode.Free)
                FreeDistance = Mathf.Clamp(18f * _sizeFactor, FreeDistanceMin, FreeDistanceMax);   // U17: 按机型尺寸给默认取距
        }

        private void LateUpdate()
        {
            if (Target == null) return;
            switch (Mode)
            {
                case CameraMode.Cockpit: UpdateCockpit(); break;
                case CameraMode.Free: UpdateFree(); break;
                default: UpdateChase(); break;
            }
        }

        private void UpdateChase()
        {
            var controller = Target.GetComponent<AircraftController>();
            Vector3 lead = Vector3.zero;
            if (controller != null && controller.State != null && VelocityLead > 0)
            {
                // 用核心库速度 (NED) 转 Unity: (东, 上, 北)
                var v = controller.State.VelocityNed;
                lead = new Vector3((float)v.Y, (float)-v.Z, (float)v.X) * VelocityLead * 0.3f;
            }

            Vector3 desired = Target.position + Target.rotation * (Offset * _sizeFactor) + lead;
            if (!_snapped)
            {
                // 首帧直接吸附: 场景里保存的相机位姿可能在山体内部, lerp 过去会穿山闪烁
                transform.position = desired;
                _snapped = true;
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, desired,
                                                  1f - Mathf.Exp(-FollowSmoothing * Time.deltaTime));
            }
            transform.LookAt(Target.position + lead);
        }

        private void UpdateCockpit()
        {
            // 刚连于机身: 位置/姿态直接取飞机 (渲染插值由飞机 Transform 自身完成)
            transform.position = Target.position + Target.rotation * (CockpitOffset * _sizeFactor);
            transform.rotation = Target.rotation;
        }

        private void UpdateFree()
        {
            // 鼠标轨道 (仅自由模式响应): RMB 拖动转视角, 滚轮缩放
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue() * 0.25f;
                    FreeAzimuthDeg += delta.x;
                    FreeElevationDeg = Mathf.Clamp(FreeElevationDeg - delta.y, -10f, 85f);
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    FreeDistance = Mathf.Clamp(FreeDistance * (scroll > 0 ? 0.9f : 1.1f),
                                               FreeDistanceMin, FreeDistanceMax);
            }

            float az = FreeAzimuthDeg * Mathf.Deg2Rad;
            float el = FreeElevationDeg * Mathf.Deg2Rad;
            // 以飞机航向为参考系的球面偏移 (跟随飞机转弯, 试飞员视线不丢失目标)
            float rel = az;
            if (Target != null)
            {
                var controller = Target.GetComponent<AircraftController>();
                if (controller != null) rel += controller.HeadingDeg * Mathf.Deg2Rad;
            }
            Vector3 dir = new Vector3(
                Mathf.Sin(rel) * Mathf.Cos(el),
                Mathf.Sin(el),
                Mathf.Cos(rel) * Mathf.Cos(el));
            transform.position = Target.position + dir * FreeDistance;
            transform.LookAt(Target.position);
        }
    }
}
