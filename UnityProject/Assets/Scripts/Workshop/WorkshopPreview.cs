using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W1: 栏③ 3D 预览场景 (独立相机 + RenderTexture + 槽位网格)。
    /// 隔离策略 (提示词 §7 "不得影响机场场景与 HDRP 全局设置"):
    ///   - 预览物体放在 y=-5000 远处, 专用相机只看该处 (场景物体天然在视锥外);
    ///   - 光照用有限 Range 的点光源 (不影响 y=0 场景), 不加全局 Directional/Volume;
    ///   - 相机/灯/网格/RT 均为运行时创建, Dispose 全部销毁, 无泄漏。
    /// 视角: 三维轨道 (左键拖动旋转 / 滚轮缩放), 自动旋转 ~6°/s, 交互后 3s 恢复
    /// (W3 再扩 4 视角/平移/线框)。
    /// </summary>
    public class WorkshopPreview : System.IDisposable
    {
        private const float YBase = -5000f;
        private const float AutoSpinDegPerSec = 6f;
        private const float ResumeDelaySec = 3f;

        public RenderTexture Rt { get; private set; }
        public Camera Cam { get; private set; }
        public bool HasContent { get; private set; }

        private GameObject _root, _camGo, _lightGo, _light2Go, _cgGo;
        private readonly Dictionary<string, GameObject> _slotObjs = new Dictionary<string, GameObject>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private Material _cgMat;

        // 轨道状态
        private float _yaw = 35f, _pitch = 18f, _dist = 6f;
        private Vector3 _center = new Vector3(0f, YBase, 0f);
        private float _lastInteract = -999f;
        private float _contentRadius = 1.5f;

        /// <summary>创建预览场景 (相机 + 点光源, y=-5000)。rtW/rtH = 输出纹理像素。</summary>
        public void Create(int rtW, int rtH)
        {
            Dispose();
            _root = new GameObject("WorkshopPreviewRoot") { hideFlags = HideFlags.DontSave };
            _root.transform.position = new Vector3(0f, YBase, 0f);

            _camGo = new GameObject("WorkshopPreviewCam") { hideFlags = HideFlags.DontSave };
            Cam = _camGo.AddComponent<Camera>();
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = FluentTheme.DarkPanel;   // 与 HUD 深色 Fluent 呼应
            Cam.cullingMask = ~0;                 // 距离隔离 (场景在视锥外)
            Cam.nearClipPlane = 0.01f;
            Cam.farClipPlane = 200f;
            Cam.enabled = false;                  // 只手动 Render, 不参与主相机链
            Cam.usePhysicalProperties = false;

            // 点光源: Range 有限, 照不到 y=0 的场景 (HDRP 物理光强, 单位 lm)。
            // 两盏灯各挂一个 GO (同 GO 双 Light 组件不可靠, 实测返回 null)
            _lightGo = new GameObject("WorkshopPreviewLight") { hideFlags = HideFlags.DontSave };
            var l = _lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 80f;
            l.intensity = 30000f;
            l.color = new Color(1f, 0.98f, 0.92f);
            _lightGo.transform.position = new Vector3(4f, YBase + 6f, 4f);
            _light2Go = new GameObject("WorkshopPreviewLight2") { hideFlags = HideFlags.DontSave };
            var l2 = _light2Go.AddComponent<Light>();   // 补光 (背侧, 冷色)
            l2.type = LightType.Point;
            l2.range = 80f;
            l2.intensity = 12000f;
            l2.color = new Color(0.7f, 0.8f, 1f);
            _light2Go.transform.position = new Vector3(-5f, YBase + 3f, -4f);

            RebuildRt(Mathf.Max(64, rtW), Mathf.Max(64, rtH));
            ApplyCameraPose();
        }

        public void RebuildRt(int rtW, int rtH)
        {
            // 量化到 32px 步进: OnGUI layout/repaint 间 1~2px 的矩形抖动不再触发销毁重建
            // (重建帧 DrawTexture 采到未初始化纹理 = 用户看到的背景闪烁)
            rtW = Mathf.CeilToInt(rtW / 32f) * 32;
            rtH = Mathf.CeilToInt(rtH / 32f) * 32;
            if (Rt != null && Rt.width == rtW && Rt.height == rtH) return;
            if (Rt != null) { Cam.targetTexture = null; Rt.Release(); Object.Destroy(Rt); }
            Rt = new RenderTexture(rtW, rtH, 24) { name = "WorkshopPreviewRT" };
            Cam.targetTexture = Rt;
            Render();   // 新 RT 立即填充, 不留空白帧
        }

        /// <summary>按槽位重建网格对象 (slot → mesh + 颜色); mesh 传 null 表示清空该槽。</summary>
        public void SetSlotMesh(string slot, Mesh mesh, Color color, string objName)
        {
            if (_slotObjs.TryGetValue(slot, out var go) && go != null)
            {
                Object.Destroy(go);
                _slotObjs.Remove(slot);
            }
            if (mesh == null)
            {
                HasContent = _slotObjs.Count > 0;
                return;
            }
            var child = new GameObject(string.IsNullOrEmpty(objName) ? slot : objName);
            child.transform.SetParent(_root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = RenderMaterials.CreateUnlit(color);   // U12: 平涂免疫曝光/灯光问题
            _materials.Add(mat);
            child.AddComponent<MeshRenderer>().sharedMaterial = mat;
            _slotObjs[slot] = child;
            _meshes.Add(mesh);
            Recenter();
            HasContent = true;
        }

        /// <summary>设置重心球 (OpenVSP 坐标经 WorkshopStlLoader.ToUnity 后的预览坐标)。</summary>
        public void SetCgMarker(Vector3 unityPos, bool visible)
        {
            if (_cgGo == null)
            {
                _cgGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(_cgGo.GetComponent<Collider>());   // 预览物体不参与物理
                _cgGo.name = "WorkshopCG";
                _cgGo.transform.SetParent(_root.transform, false);
                _cgMat = RenderMaterials.CreateUnlit(new Color(1f, 0.22f, 0.22f));   // U12
                _materials.Add(_cgMat);
                _cgGo.GetComponent<MeshRenderer>().sharedMaterial = _cgMat;
            }
            _cgGo.SetActive(visible);
            _cgGo.transform.localPosition = unityPos;
            float size = Mathf.Max(0.01f, _contentRadius * 0.045f);
            _cgGo.transform.localScale = new Vector3(size, size, size);
        }

        /// <summary>定向改槽位外观颜色 (U3/A4): 每槽复用同一材质实例, 只改 color —
        /// 不新建材质/不重导 STL/不跑质量, 连续切色资源数不增长 (UC07)。</summary>
        public void SetSlotColor(string slot, Color c)
        {
            GameObject go;
            if (!_slotObjs.TryGetValue(slot, out go) || go == null) return;
            var r = go.GetComponent<MeshRenderer>();
            if (r == null) return;
            if (r.sharedMaterial == null)
                r.sharedMaterial = RenderMaterials.CreateUnlit(c);
            else
                r.sharedMaterial.color = c;
        }

        /// <summary>该槽位的预览网格对象是否存在 (U11: 选色时预览未装载则
        /// 上层应触发网格刷新, 否则选色是静默空操作 — 用户看到"颜色不更新")。</summary>
        public bool HasSlot(string slot)
        {
            GameObject go;
            return _slotObjs.TryGetValue(slot, out go) && go != null;
        }

        /// <summary>设置某槽位的操纵面高亮覆盖网格 (W2; 空列表=清除)。</summary>
        public void SetControlOverlays(string slot, List<Mesh> overlays)
        {
            // 清旧 (命名约定 Overlay_*)
            if (_slotObjs.TryGetValue(slot, out var parent) && parent != null)
                for (int i = parent.transform.childCount - 1; i >= 0; i--)
                {
                    var ch = parent.transform.GetChild(i);
                    if (ch.name.StartsWith("Overlay_"))
                    {
                        var oldMesh = ch.GetComponent<MeshFilter>()?.sharedMesh;
                        if (oldMesh != null) Object.Destroy(oldMesh);
                        Object.Destroy(ch.gameObject);
                    }
                }
            if (overlays == null || overlays.Count == 0 || parent == null) return;
            if (_overlayMat == null)
            {
                _overlayMat = RenderMaterials.CreateUnlit(new Color(1f, 0.85f, 0.15f, 0.85f));   // U12
                _materials.Add(_overlayMat);
            }
            for (int i = 0; i < overlays.Count; i++)
            {
                var go = new GameObject("Overlay_" + i);
                go.transform.SetParent(parent.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = overlays[i];
                _meshes.Add(overlays[i]);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _overlayMat;
            }
        }

        private Material _overlayMat;

        private void Recenter()
        {
            var bounds = new Bounds();
            bool any = false;
            foreach (var go in _slotObjs.Values)
            {
                var r = go.GetComponent<MeshRenderer>();
                if (r == null) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!any) return;
            _center = bounds.center;
            _contentRadius = Mathf.Max(0.3f, bounds.extents.magnitude);
            _dist = Mathf.Clamp(_contentRadius * 3.2f, 0.5f, 400f);
        }

        // ---------- 交互 (由 UI 转发预览区域内的鼠标事件) ----------
        public void Orbit(float dYaw, float dPitch)
        {
            _yaw = Mathf.Repeat(_yaw + dYaw, 360f);
            _pitch = Mathf.Clamp(_pitch + dPitch, -85f, 85f);
            _lastInteract = Time.realtimeSinceStartup;
            ApplyCameraPose();
        }

        public void Zoom(float factor)
        {
            _dist = Mathf.Clamp(_dist * factor, _contentRadius * 0.4f, _contentRadius * 40f);
            _lastInteract = Time.realtimeSinceStartup;
            ApplyCameraPose();
        }

        private void ApplyCameraPose()
        {
            if (Cam == null) return;
            float yr = _yaw * Mathf.Deg2Rad, pr = _pitch * Mathf.Deg2Rad;
            var dir = new Vector3(
                Mathf.Cos(pr) * Mathf.Sin(yr), Mathf.Sin(pr), Mathf.Cos(pr) * Mathf.Cos(yr));
            Cam.transform.position = _center + dir * _dist;
            Cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
        }

        /// <summary>每帧: 自动旋转 + 手动渲染 (工坊打开时由 UI 调用)。</summary>
        public void Render()
        {
            if (Cam == null || Rt == null) return;
            if (Time.realtimeSinceStartup - _lastInteract > ResumeDelaySec)
            {
                _yaw = Mathf.Repeat(_yaw + AutoSpinDegPerSec * Time.unscaledDeltaTime, 360f);
                ApplyCameraPose();
            }
            Cam.Render();
        }

        /// <summary>世界点 → RT 像素坐标 (x/y 像素, y 向上; z=视深, &gt;0 在相机前方)。</summary>
        public Vector3 WorldToRtPixels(Vector3 worldPos)
        {
            if (Cam == null || Rt == null) return Vector3.zero;
            return Cam.WorldToScreenPoint(worldPos);
        }

        public void Dispose()
        {
            foreach (var go in _slotObjs.Values)
                if (go != null) Object.Destroy(go);
            _slotObjs.Clear();
            foreach (var m in _meshes) if (m != null) Object.Destroy(m);
            _meshes.Clear();
            foreach (var m in _materials) if (m != null) Object.Destroy(m);
            _materials.Clear();
            _cgMat = null;
            if (_cgGo != null) { Object.Destroy(_cgGo); _cgGo = null; }
            if (_root != null) { Object.Destroy(_root); _root = null; }
            if (Rt != null)
            {
                if (Cam != null) Cam.targetTexture = null;
                Rt.Release();
                Object.Destroy(Rt);
                Rt = null;
            }
            if (_camGo != null) { Object.Destroy(_camGo); _camGo = null; }
            if (_lightGo != null) { Object.Destroy(_lightGo); _lightGo = null; }
            if (_light2Go != null) { Object.Destroy(_light2Go); _light2Go = null; }
            Cam = null;
            HasContent = false;
        }
    }
}
