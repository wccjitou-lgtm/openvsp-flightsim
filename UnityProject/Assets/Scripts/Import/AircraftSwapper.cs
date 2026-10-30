using System.Collections.Generic;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型热替换 (Phase 7b): 用导入的 config + OBJ 在运行时重建飞机 GameObject。
    /// 组件清单与 FlightSimSceneSetup.CreateAircraft 一致 (Controller/输入/
    /// WindSettings/FlightHUD/AircraftVisual/DataLogger/FlightGuard/ModePilot),
    /// 但不使用 AssetDatabase (editor-only)。
    /// 关键时序: AddComponent 立即触发 Awake → 先建非激活 GO, 设完
    /// ConfigAbsolutePath/RuntimeObjPath 再 SetActive(true), Awake 才按导入
    /// 配置装配。
    /// 换机修复: 用 FindObjectsOfType<AircraftController> 替代按名查找 (多场景/
    /// 多次换机后名字可能不可靠); 旧机先 SetActive(false) 立即停用 HUD/输入/物理
    /// (Destroy 延迟到帧末, 同帧内新旧并存会导致 HUD 叠加和输入竞争), 再 Destroy。
    /// </summary>
    public static class AircraftSwapper
    {
        /// <summary>换机 (U10: partColorHex = 部件名(安全化前原名) → "#RRGGBB";
        /// 空则新机回落默认涂装)。其余语义同下述重载。</summary>
        public static GameObject Swap(string configPath, string objPath,
                                      Dictionary<string, string> partColorHex)
        {
            var colors = new Dictionary<string, Color>();
            if (partColorHex != null)
                foreach (var kv in partColorHex)
                    if (AircraftVisual.TryParseHex(kv.Value, out Color c))
                        // 键转 OBJ 安全化组名 (与 AircraftVisual.SanitizeGroupName 同规则)
                        colors[AircraftVisual.SanitizeGroupName(kv.Key)] = c;
            return Swap(configPath, objPath, colors);
        }

        public static GameObject Swap(string configPath, string objPath,
                                      Dictionary<string, Color> partColors = null)
        {
            // 按类型查找所有旧飞机 (不依赖名字, 多次换机/多场景后更可靠)
            var oldCtrls = Object.FindObjectsOfType<AircraftController>();
            AircraftController oldCtrl = null;
            FlightGuard oldGuard = null;
            WindSettings oldWind = null;
            bool wasAutoPilot = false;
            var chase = Camera.main != null ? Camera.main.GetComponent<ChaseCamera>() : null;
            var oldChaseTarget = chase != null ? chase.Target : null;

            foreach (var c in oldCtrls)
            {
                if (c == null) continue;
                oldCtrl = c;
                oldGuard = c.GetComponent<FlightGuard>();
                oldWind = c.GetComponent<WindSettings>();
                wasAutoPilot = c.GetComponent<AutoPilotInput>() != null;
                // 立即禁用旧机 (Destroy 延迟到帧末; SetActive(false) 立即停用
                // OnGUI/Update/FixedUpdate, 消除同帧 HUD 叠加和输入竞争)
                c.gameObject.SetActive(false);
                Object.Destroy(c.gameObject);
            }

            var go = new GameObject("TestUAV");
            go.SetActive(false);   // 暂缓 Awake: 先设字段再激活

            var controller = go.AddComponent<AircraftController>();
            controller.ConfigAbsolutePath = configPath;
            if (oldCtrl != null)
            {
                controller.InitialPositionNed = oldCtrl.InitialPositionNed;
                controller.InitialHeadingDeg = oldCtrl.InitialHeadingDeg;
                controller.InitialAirspeed = oldCtrl.InitialAirspeed;
            }
            // U8: 新机轮高与旧机不同 — 继承旧机出生高度会让新机从空中砸向跑道
            // (旧机轮 0.35m/新机 0.03m 实测落差 0.3m, 轻机弹跳翻覆) 或埋进地面。
            // 按两侧最大轮位差补偿 NED z, 让新机轮子恰好落在旧机轮的地面高度。
            // NED z 向下为正: 新机轮浅 (newWheelZ 小) → CG 应更低 → z 增大, 故为 +。
            // (2026-10-10 前为 "-", 方向相反: 换浅轮机会把模型埋进地里, 用户实测)
            if (oldCtrl != null && oldCtrl.Plane != null && oldCtrl.Plane.Gear != null)
            {
                float oldWheelZ = 0f, newWheelZ = 0f;
                foreach (var w in oldCtrl.Plane.Gear.Wheels)
                    oldWheelZ = Mathf.Max(oldWheelZ, (float)w.Position.Z);
                try
                {
                    var newLoaded = AircraftConfigLoader.Parse(System.IO.File.ReadAllText(configPath));
                    if (newLoaded.Gear != null)
                        foreach (var w in newLoaded.Gear.Wheels)
                            newWheelZ = Mathf.Max(newWheelZ, (float)w.Position.Z);
                }
                catch { /* FinishSwap/库换上已预检过; 这里失败按同高处理 */ }
                var ip = controller.InitialPositionNed;
                controller.InitialPositionNed =
                    new Vector3(ip.x, ip.y, ip.z + (oldWheelZ - newWheelZ));
            }

            var visual = go.AddComponent<AircraftVisual>();
            if (!string.IsNullOrEmpty(objPath)) visual.RuntimeObjPath = objPath;
            // U10: 工坊每部件颜色 → 试飞涂装 (与 3D 预览一致)
            if (partColors != null && partColors.Count > 0)
                visual.PartColors = new Dictionary<string, Color>(partColors);

            if (wasAutoPilot) go.AddComponent<AutoPilotInput>();
            else go.AddComponent<KeyboardFlightInput>();
            // U7: 风况继承 — 场景风 (SceneSwitcher 只在切场景时下发) 和用户在
            // HUD 风况面板的调整不随换机丢掉 (否则换机后静默回到无风默认)
            var wind = go.AddComponent<WindSettings>();
            if (oldWind != null)
            {
                wind.WindSpeed = oldWind.WindSpeed;
                wind.WindDirectionDeg = oldWind.WindDirectionDeg;
                wind.EnableShear = oldWind.EnableShear;
                wind.ShearExponent = oldWind.ShearExponent;
                wind.ShearRefAltitude = oldWind.ShearRefAltitude;
                wind.Turbulence = oldWind.Turbulence;
                wind.TurbulenceSeed = oldWind.TurbulenceSeed;
                wind.EnableTurbulence = oldWind.EnableTurbulence;
                wind.Rebuild();
            }
            go.AddComponent<FlightHUD>();
            go.AddComponent<DataLogger>();
            var guard = go.AddComponent<FlightGuard>();
            if (oldGuard != null)
            {
                // 场地边界随场景 (4km 森林场地 1800m); 不继承则新机回默认 1200m
                guard.BoundaryRadius = oldGuard.BoundaryRadius;
                guard.BoundaryMargin = oldGuard.BoundaryMargin;
            }
            go.AddComponent<ModePilot>();

            go.SetActive(true);

            if (chase != null)
            {
                chase.Target = go.transform;
                chase.SnapToTarget();
            }

            Debug.Log("[AircraftSwapper] 已换机: " + configPath +
                      (string.IsNullOrEmpty(objPath) ? " (无视觉模型)" : " + " + objPath));
            return go;
        }
    }
}
