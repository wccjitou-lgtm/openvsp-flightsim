using System;
using System.Collections.Generic;
using FlightDynamics.Math;

namespace FlightDynamics.IO
{
    /// <summary>
    /// AircraftConfig.json 加载器 (Phase 1 独立测试用; Unity 侧将用各自的 JSON 方案,
    /// 此文件不随核心库导入 Assets)。
    /// 使用 System.Web.Extensions 的 JavaScriptSerializer (net4.x 自带, 无第三方依赖)。
    ///
    /// 坐标/符号转换注意 (与生成器 Tools/generate_aircraft_config.py 约定一致):
    ///   - JSON 惯量张量为 OpenVSP 机体轴 (X 后, Y 右, Z 上), 此处转换为
    ///     标准机体轴 (x 前, y 右, z 下): Ixy/Iyz 变号, 其余不变。
    ///   - 气动导数已是标准稳定性系数约定, 直接使用。
    /// </summary>
    public static class AircraftConfigJson
    {
        public class LoadedAircraft
        {
            public string Name;
            public MassData Mass = new MassData();
            public AeroData Aero = new AeroData();
            public EngineData Engine = new EngineData();
            public GroundContactModel Gear;         // 可能为 null
            public double[] CgOvsp = new double[3]; // OpenVSP 坐标系重心 (仅 Unity 摆放网格用)
        }

        public static LoadedAircraft Load(string path)
        {
            string json = System.IO.File.ReadAllText(path);
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null)
                throw new InvalidOperationException("JSON 解析失败: " + path);

            var result = new LoadedAircraft();
            result.Name = GetStr(root, "name");

            // ---- reference ----
            var reference = GetDict(root, "reference");
            result.Aero.Sref = GetDouble(reference, "Sref_m2", 1);
            result.Aero.Bref = GetDouble(reference, "bref_m", 1);
            result.Aero.Cref = GetDouble(reference, "cref_m", 1);
            if (reference != null && reference.ContainsKey("cg_m"))
            {
                var cg = reference["cg_m"] as System.Collections.ArrayList;
                if (cg != null && cg.Count == 3)
                    result.CgOvsp = new[] { Convert.ToDouble(cg[0]), Convert.ToDouble(cg[1]), Convert.ToDouble(cg[2]) };
            }

            // ---- mass (OpenVSP 轴系 → 标准体轴系: Ixy/Iyz 变号) ----
            var mass = GetDict(root, "mass");
            result.Mass.MassKg = GetDouble(mass, "mass_kg", 1);
            if (mass != null && mass.ContainsKey("inertia_kgm2"))
            {
                // JSON 对象形式: {"Ixx": v, "Iyy": v, ...}
                var inertia = mass["inertia_kgm2"] as Dictionary<string, object>;
                if (inertia == null)
                    throw new InvalidOperationException("inertia_kgm2 应为对象形式 {Ixx, Iyy, Izz, Ixy, Ixz, Iyz}");
                foreach (var kv in inertia)
                {
                    if (kv.Value == null) continue;
                    double v = Convert.ToDouble(kv.Value);
                    bool flip = kv.Key == "Ixy" || kv.Key == "Iyz"; // 轴系转换
                    SetInertia(result.Mass, kv.Key, flip ? -v : v);
                }
            }

            // ---- aero ----
            var aero = GetDict(root, "aero");
            result.Aero.CL0 = GetDouble(aero, "CL0");
            result.Aero.CLalpha = GetDouble(aero, "CLalpha");
            result.Aero.CLq = GetDouble(aero, "CLq");
            result.Aero.CD0 = GetDouble(aero, "CD0");
            result.Aero.KInduced = GetDouble(aero, "k_induced");
            result.Aero.Cm0 = GetDouble(aero, "Cm0");
            result.Aero.Cmalpha = GetDouble(aero, "Cmalpha");
            result.Aero.Cmq = GetDouble(aero, "Cmq");
            result.Aero.CYbeta = GetDouble(aero, "CYbeta");
            result.Aero.Clbeta = GetDouble(aero, "Clbeta");
            result.Aero.Cnbeta = GetDouble(aero, "Cnbeta");
            result.Aero.CYp = GetDouble(aero, "CYp");
            result.Aero.Clp = GetDouble(aero, "Clp");
            result.Aero.Cnp = GetDouble(aero, "Cnp");
            result.Aero.CYr = GetDouble(aero, "CYr");
            result.Aero.Clr = GetDouble(aero, "Clr");
            result.Aero.Cnr = GetDouble(aero, "Cnr");

            if (aero != null && aero.ContainsKey("controls"))
            {
                var controls = aero["controls"] as Dictionary<string, object>;
                if (controls != null)
                {
                    var elev = controls.ContainsKey("elevator") ? controls["elevator"] as Dictionary<string, object> : null;
                    if (elev != null)
                    {
                        result.Aero.CLde = GetDouble(elev, "CLde");
                        result.Aero.Cmde = GetDouble(elev, "Cmde");
                    }
                    var ail = controls.ContainsKey("aileron") ? controls["aileron"] as Dictionary<string, object> : null;
                    if (ail != null)
                    {
                        result.Aero.CYda = GetDouble(ail, "CYda");
                        result.Aero.Clda = GetDouble(ail, "Clda");
                        result.Aero.Cnda = GetDouble(ail, "Cnda");
                    }
                    var rud = controls.ContainsKey("rudder") ? controls["rudder"] as Dictionary<string, object> : null;
                    if (rud != null)
                    {
                        result.Aero.CYdr = GetDouble(rud, "CYdr");
                        result.Aero.Cldr = GetDouble(rud, "Cldr");
                        result.Aero.Cndr = GetDouble(rud, "Cndr");
                    }
                }
            }

            // ---- engine ----
            var engine = GetDict(root, "engine");
            result.Engine.MaxThrustN = GetDouble(engine, "maxThrust_N", 1);

            // ---- gear (标准体轴 x 前/y 右/z 下, 原点=重心) ----
            var gear = GetDict(root, "gear");
            if (gear != null && gear.ContainsKey("wheels"))
            {
                var wheels = gear["wheels"] as System.Collections.ArrayList;
                var friction = GetDict(gear, "friction");
                var model = new GroundContactModel();
                if (friction != null)
                {
                    model.Params.MuLateral = GetDouble(friction, "mu_lateral", 0.7);
                    model.Params.MuRolling = GetDouble(friction, "mu_rolling", 0.02);
                    model.Params.MuBrake = GetDouble(friction, "mu_brake", 0.45);
                    model.Params.MaxSteerRad = GetDouble(friction, "maxSteerDeg", 25.0) * Mathd.Deg2Rad;
                }
                if (wheels != null)
                {
                    foreach (object wo in wheels)
                    {
                        var wd = wo as Dictionary<string, object>;
                        if (wd == null) continue;
                        var posArr = wd.ContainsKey("position_m") ? wd["position_m"] as System.Collections.ArrayList : null;
                        if (posArr == null || posArr.Count != 3) continue;
                        var wheel = new GearWheel(
                            GetStr(wd, "name"),
                            new Vec3(Convert.ToDouble(posArr[0]),
                                     Convert.ToDouble(posArr[1]),
                                     Convert.ToDouble(posArr[2])),
                            GetDouble(wd, "kN_per_m", 900.0),
                            GetDouble(wd, "cNs_per_m", 80.0),
                            GetInt(wd, "steerable") != 0,
                            GetInt(wd, "brake") != 0);
                        model.AddWheel(wheel);
                    }
                }
                result.Gear = model;
            }

            return result;
        }

        private static int GetInt(Dictionary<string, object> parent, string key, int def = 0)
        {
            if (parent != null && parent.ContainsKey(key) && parent[key] != null)
            {
                if (parent[key] is bool) return (bool)parent[key] ? 1 : 0;
                try { return Convert.ToInt32(parent[key]); } catch (FormatException) { return def; }
            }
            return def;
        }

        private static void SetInertia(MassData m, string key, double v)
        {
            switch (key)
            {
                case "Ixx": m.Ixx = v; break;
                case "Iyy": m.Iyy = v; break;
                case "Izz": m.Izz = v; break;
                case "Ixy": m.Ixy = v; break;
                case "Ixz": m.Ixz = v; break;
                case "Iyz": m.Iyz = v; break;
            }
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> parent, string key)
        {
            if (parent != null && parent.ContainsKey(key))
                return parent[key] as Dictionary<string, object>;
            return null;
        }

        private static string GetStr(Dictionary<string, object> parent, string key)
        {
            if (parent != null && parent.ContainsKey(key))
                return Convert.ToString(parent[key]);
            return "";
        }

        private static double GetDouble(Dictionary<string, object> parent, string key, double def = 0)
        {
            if (parent != null && parent.ContainsKey(key) && parent[key] != null)
                return Convert.ToDouble(parent[key]);
            return def;
        }
    }
}
