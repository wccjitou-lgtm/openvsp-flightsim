using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FlightDynamics;
using UnityEngine;

namespace FlightSim
{
    /// <summary>舵面最大偏角 (度), 来自 AircraftConfig.json 的 controls。</summary>
    [System.Serializable]
    public class MaxDeflection
    {
        public double Elevator = 25;
        public double Aileron = 20;
        public double Rudder = 25;
    }

    /// <summary>
    /// Unity 侧 AircraftConfig.json 加载器 (开发提示词 9 节 DataPipeline)。
    /// 用内置 JSON reader 校验原始字段后交给 JsonUtility (无第三方依赖)。
    /// 惯量张量在此从 OpenVSP 轴系转换到标准体轴 (Ixy/Iyz 变号)。
    /// </summary>
    public static class AircraftConfigLoader
    {
        [System.Serializable] private class RefJson
        {
            public double Sref_m2 = 1, bref_m = 1, cref_m = 1;
            public double[] cg_m;
        }
        [System.Serializable] private class MassJson
        {
            public double mass_kg = 1;
            public InertiaJson inertia_kgm2;
        }
        [System.Serializable] private class InertiaJson
        {
            public double Ixx, Iyy, Izz, Ixy, Ixz, Iyz;
        }
        [System.Serializable] private class AeroJson
        {
            public double CL0, CLalpha, CLq, CD0, k_induced;
            public double Cm0, Cmalpha, Cmq;
            public double CYbeta, Clbeta, Cnbeta;
            public double CYp, Clp, Cnp, CYr, Clr, Cnr;
            public ControlsJson controls;
        }
        [System.Serializable] private class ControlsJson
        {
            public ControlDefJson elevator, aileron, rudder;
        }
        [System.Serializable] private class ControlDefJson
        {
            public double CLde, Cmde, CYda, Clda, Cnda, CYdr, Cldr, Cndr;
            public double maxDeflectionDeg = 25;
        }
        [System.Serializable] private class EngineJson
        {
            public double maxThrust_N = 1;
        }
        [System.Serializable] private class GearFrictionJson
        {
            public double mu_lateral = 0.7, mu_rolling = 0.02, mu_brake = 0.45, maxSteerDeg = 25;
        }
        [System.Serializable] private class WheelJson
        {
            public string name;
            public double[] position_m;
            public double kN_per_m = 900, cNs_per_m = 80;
            public bool steerable, brake;
        }
        [System.Serializable] private class GearJson
        {
            public WheelJson[] wheels;
            public GearFrictionJson friction;
        }
        [System.Serializable] private class DesignJson
        {
            public double htailHalfSpan_m = 0.7, htailRootChord_m = 0.30, htailTaper = 0.7;
            public double vtailHeight_m = 0.55, vtailRootChord_m = 0.35, vtailTaper = 0.6;
            public double tailX_m = 2.45, htailArm_m = 1.065;
        }
        [System.Serializable] private class MetaJson
        {
            public DesignJson design;
            public double neutralPoint_m = double.NaN;
        }
        [System.Serializable] private class ConfigJson
        {
            public string name;
            public RefJson reference;
            public MassJson mass;
            public AeroJson aero;
            public EngineJson engine;
            public GearJson gear;
            public MetaJson meta;
        }

        /// <summary>尾翼设计几何 (来自 AircraftConfig.json meta.design, 数据源 build_model.as)。</summary>
        public class DesignData
        {
            public double HtailHalfSpanM = 0.7, HtailRootChordM = 0.30, HtailTaper = 0.7;
            public double VtailHeightM = 0.55, VtailRootChordM = 0.35, VtailTaper = 0.6;
            public double TailXM = 2.45, HtailArmM = 1.065;
            public double NeutralPointM = double.NaN;
            public double CgXM = double.NaN;
        }

        public class LoadedAircraft
        {
            public string Name;
            public MassData Mass = new MassData();
            public AeroData Aero = new AeroData();
            public EngineData Engine = new EngineData();
            public GroundContactModel Gear;
            public MaxDeflection MaxDeflectionDeg = new MaxDeflection();
            public DesignData Design = new DesignData();
        }

        /// <summary>最近一次 Parse 的 design 数据 (HUD 性能面板等消费)。</summary>
        public static DesignData LastDesign { get; private set; }

        public static MaxDeflection DefaultMaxDeflection { get { return new MaxDeflection(); } }

        public static LoadedAircraft LoadFromStreamingAssets(string fileName)
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string json = System.IO.File.ReadAllText(path);
#else
            // Android 等平台需 UnityWebRequest; Phase 2 仅桌面
            string json = System.IO.File.ReadAllText(path);
#endif
            return Parse(json);
        }

        /// <summary>从绝对路径加载 (Phase 7 导入机型, VspImports/.../AircraftConfig.json);
        /// 解析逻辑与 StreamingAssets 路径完全一致。</summary>
        public static LoadedAircraft LoadFromAbsolutePath(string path)
        {
            return Parse(System.IO.File.ReadAllText(path));
        }

        public static LoadedAircraft Parse(string json)
        {
            XElement validated = ValidateConfig(json);
            ConfigJson j;
            try
            {
                j = JsonUtility.FromJson<ConfigJson>(json);
            }
            catch (System.ArgumentException error)
            {
                throw new System.InvalidOperationException("AircraftConfig.json: deserialization failed", error);
            }
            if (j == null || j.reference == null || j.mass == null || j.mass.inertia_kgm2 == null
                || j.aero == null || j.aero.controls == null || j.aero.controls.elevator == null
                || j.aero.controls.aileron == null || j.aero.controls.rudder == null || j.engine == null)
                throw ConfigError("config", "required physical objects could not be deserialized");
            ValidateDecodedGear(j.gear, validated.Element("gear"));

            var r = new LoadedAircraft();
            r.Name = j.name;

            r.Aero.Sref = j.reference.Sref_m2;
            r.Aero.Bref = j.reference.bref_m;
            r.Aero.Cref = j.reference.cref_m;

            r.Mass.MassKg = j.mass.mass_kg;
            var i = j.mass.inertia_kgm2;
            if (i != null)
            {
                // OpenVSP 轴系(X后,Z上) → 标准体轴(x前,z下): Ixy/Iyz 变号
                r.Mass.Ixx = i.Ixx; r.Mass.Iyy = i.Iyy; r.Mass.Izz = i.Izz;
                r.Mass.Ixy = -i.Ixy; r.Mass.Ixz = i.Ixz; r.Mass.Iyz = -i.Iyz;
            }

            var a = j.aero;
            if (a != null)
            {
                r.Aero.CL0 = a.CL0; r.Aero.CLalpha = a.CLalpha; r.Aero.CLq = a.CLq;
                r.Aero.CD0 = a.CD0; r.Aero.KInduced = a.k_induced;
                r.Aero.Cm0 = a.Cm0; r.Aero.Cmalpha = a.Cmalpha; r.Aero.Cmq = a.Cmq;
                r.Aero.CYbeta = a.CYbeta; r.Aero.Clbeta = a.Clbeta; r.Aero.Cnbeta = a.Cnbeta;
                r.Aero.CYp = a.CYp; r.Aero.Clp = a.Clp; r.Aero.Cnp = a.Cnp;
                r.Aero.CYr = a.CYr; r.Aero.Clr = a.Clr; r.Aero.Cnr = a.Cnr;

                var ctl = a.controls;
                if (ctl != null)
                {
                    if (ctl.elevator != null)
                    {
                        r.Aero.CLde = ctl.elevator.CLde;
                        r.Aero.Cmde = ctl.elevator.Cmde;
                        r.MaxDeflectionDeg.Elevator = ctl.elevator.maxDeflectionDeg;
                    }
                    if (ctl.aileron != null)
                    {
                        r.Aero.CYda = ctl.aileron.CYda;
                        r.Aero.Clda = ctl.aileron.Clda;
                        r.Aero.Cnda = ctl.aileron.Cnda;
                        r.MaxDeflectionDeg.Aileron = ctl.aileron.maxDeflectionDeg;
                    }
                    if (ctl.rudder != null)
                    {
                        r.Aero.CYdr = ctl.rudder.CYdr;
                        r.Aero.Cldr = ctl.rudder.Cldr;
                        r.Aero.Cndr = ctl.rudder.Cndr;
                        r.MaxDeflectionDeg.Rudder = ctl.rudder.maxDeflectionDeg;
                    }
                }
            }

            if (j.engine != null) r.Engine.MaxThrustN = j.engine.maxThrust_N;

            // 尾翼设计几何 + 中立点 (性能面板用)
            var d = r.Design;
            if (j.meta != null && j.meta.design != null)
            {
                var dj = j.meta.design;
                d.HtailHalfSpanM = dj.htailHalfSpan_m; d.HtailRootChordM = dj.htailRootChord_m;
                d.HtailTaper = dj.htailTaper; d.VtailHeightM = dj.vtailHeight_m;
                d.VtailRootChordM = dj.vtailRootChord_m; d.VtailTaper = dj.vtailTaper;
                d.TailXM = dj.tailX_m; d.HtailArmM = dj.htailArm_m;
                if (!double.IsNaN(j.meta.neutralPoint_m)) d.NeutralPointM = j.meta.neutralPoint_m;
            }
            if (j.reference.cg_m != null && j.reference.cg_m.Length > 0)
                d.CgXM = j.reference.cg_m[0];
            if (j.gear != null && j.gear.wheels != null && j.gear.wheels.Length > 0)
            {
                var g = new GroundContactModel();
                if (j.gear.friction != null)
                {
                    g.Params.MuLateral = j.gear.friction.mu_lateral;
                    g.Params.MuRolling = j.gear.friction.mu_rolling;
                    g.Params.MuBrake = j.gear.friction.mu_brake;
                    g.Params.MaxSteerRad = j.gear.friction.maxSteerDeg * FlightDynamics.Math.Mathd.Deg2Rad;
                }
                foreach (var w in j.gear.wheels)
                {
                    g.AddWheel(new GearWheel(w.name,
                        new FlightDynamics.Math.Vec3(w.position_m[0], w.position_m[1], w.position_m[2]),
                        w.kN_per_m, w.cNs_per_m, w.steerable, w.brake));
                }
                r.Gear = g;
            }

            LastDesign = d;
            return r;
        }

        private static System.InvalidOperationException ConfigError(string path, string reason)
        {
            return new System.InvalidOperationException("AircraftConfig.json " + path + ": " + reason);
        }

        private static XElement RequireObject(XElement node, string path)
        {
            RequireType(node, "object", path);
            var names = new HashSet<string>();
            foreach (var child in node.Elements())
            {
                string name = (string)child.Attribute("item") ?? child.Name.LocalName;
                if (!names.Add(name))
                    throw ConfigError(path + "." + name, "duplicate field");
            }
            return node;
        }

        private static void RequireType(XElement node, string type, string path)
        {
            if (node == null || (string)node.Attribute("type") != type)
                throw ConfigError(path, "must be " + type);
        }

        private static double Number(XElement node, string path,
                                     bool positive = false, bool nonnegative = false, bool nonzero = false)
        {
            double value;
            if (node == null || (string)node.Attribute("type") != "number"
                || !double.TryParse(node.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.IsNaN(value) || double.IsInfinity(value))
                throw ConfigError(path, "must be a finite number");
            if (positive && value <= 0)
                throw ConfigError(path, "must be positive");
            if (nonnegative && value < 0)
                throw ConfigError(path, "must be nonnegative");
            if (nonzero && value == 0)
                throw ConfigError(path, "primary control effectiveness must be nonzero");
            return value;
        }

        private static void Vector(XElement node, string path)
        {
            RequireType(node, "array", path);
            int count = 0;
            foreach (var component in node.Elements())
                Number(component, path + "[" + count++ + "]");
            if (count != 3)
                throw ConfigError(path, "must contain three finite numbers");
        }

        private static XElement ValidateConfig(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw ConfigError("config", "must be a JSON object");
            XElement root;
            try
            {
                // The BCL reader preserves JSON types and presence that JsonUtility
                // can hide by coercing values or supplying field defaults.
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(
                    Encoding.UTF8.GetBytes(json), new XmlDictionaryReaderQuotas
                    {
                        MaxDepth = 64, MaxStringContentLength = 1024 * 1024,
                        MaxArrayLength = 1024 * 1024
                    }))
                    root = XElement.Load(reader);
            }
            catch (XmlException error)
            {
                throw new System.InvalidOperationException("AircraftConfig.json: malformed JSON", error);
            }
            catch (System.ArgumentException error)
            {
                throw new System.InvalidOperationException("AircraftConfig.json: malformed JSON", error);
            }

            RequireObject(root, "config");
            var reference = RequireObject(root.Element("reference"), "reference");
            foreach (string key in new[] { "Sref_m2", "bref_m", "cref_m" })
                Number(reference.Element(key), "reference." + key, positive: true);
            if (reference.Element("cg_m") != null)
                Vector(reference.Element("cg_m"), "reference.cg_m");

            var mass = RequireObject(root.Element("mass"), "mass");
            Number(mass.Element("mass_kg"), "mass.mass_kg", positive: true);
            ValidateInertia(RequireObject(mass.Element("inertia_kgm2"), "mass.inertia_kgm2"));

            var aero = RequireObject(root.Element("aero"), "aero");
            foreach (string key in new[] {
                "CL0", "CLalpha", "CLq", "CD0", "k_induced", "Cm0", "Cmalpha", "Cmq",
                "CYbeta", "Clbeta", "Cnbeta", "CYp", "Clp", "Cnp", "CYr", "Clr", "Cnr" })
                Number(aero.Element(key), "aero." + key, nonnegative: key == "CD0" || key == "k_induced");
            var controls = RequireObject(aero.Element("controls"), "aero.controls");
            ValidateControl(controls, "elevator", new[] { "CLde", "Cmde" }, "Cmde");
            ValidateControl(controls, "aileron", new[] { "CYda", "Clda", "Cnda" }, "Clda");
            ValidateControl(controls, "rudder", new[] { "CYdr", "Cldr", "Cndr" }, "Cndr");

            var engine = RequireObject(root.Element("engine"), "engine");
            Number(engine.Element("maxThrust_N"), "engine.maxThrust_N", nonnegative: true);
            var gear = root.Element("gear");
            if (gear != null)
                ValidateGear(gear);
            return root;
        }

        private static void ValidateControl(XElement controls, string surface, string[] fields, string primary)
        {
            string path = "aero.controls." + surface;
            var control = RequireObject(controls.Element(surface), path);
            foreach (string key in fields)
                Number(control.Element(key), path + "." + key, nonzero: key == primary);
            Number(control.Element("maxDeflectionDeg"), path + ".maxDeflectionDeg", positive: true);
        }

        private static void ValidateInertia(XElement inertia)
        {
            string path = "mass.inertia_kgm2";
            var keys = new[] { "Ixx", "Iyy", "Izz", "Ixy", "Ixz", "Iyz" };
            var values = new double[keys.Length];
            double scale = 0;
            for (int n = 0; n < keys.Length; n++)
            {
                values[n] = Number(inertia.Element(keys[n]), path + "." + keys[n]);
                scale = System.Math.Max(scale, System.Math.Abs(values[n]));
            }
            if (scale == 0)
                throw ConfigError(path, "tensor must be positive definite");
            for (int n = 0; n < values.Length; n++)
                values[n] /= scale;
            if (values[0] <= 0 || values[1] <= 0 || values[2] <= 0)
                throw ConfigError(path, "tensor must be positive definite");

            // Scaled Cholesky avoids overflow in determinant products. The tensor
            // has negative product terms; the Ixy/Iyz axis flip preserves SPD.
            double l00 = System.Math.Sqrt(values[0]);
            double l10 = -values[3] / l00, l20 = -values[4] / l00;
            double pivot1 = values[1] - l10 * l10;
            if (!(pivot1 > 0))
                throw ConfigError(path, "tensor must be positive definite");
            double l21 = (-values[5] - l10 * l20) / System.Math.Sqrt(pivot1);
            double pivot2 = values[2] - l20 * l20 - l21 * l21;
            if (!(pivot2 > 0))
                throw ConfigError(path, "tensor must be positive definite");
        }

        private static void ValidateGear(XElement gear)
        {
            RequireObject(gear, "gear");
            var wheels = gear.Element("wheels");
            RequireType(wheels, "array", "gear.wheels");
            int index = 0;
            foreach (var wheel in wheels.Elements())
            {
                string path = "gear.wheels[" + index++ + "]";
                RequireObject(wheel, path);
                Vector(wheel.Element("position_m"), path + ".position_m");
                if (wheel.Element("kN_per_m") != null)
                    Number(wheel.Element("kN_per_m"), path + ".kN_per_m", positive: true);
                if (wheel.Element("cNs_per_m") != null)
                    Number(wheel.Element("cNs_per_m"), path + ".cNs_per_m", nonnegative: true);
                foreach (string key in new[] { "steerable", "brake" })
                    if (wheel.Element(key) != null)
                        RequireType(wheel.Element(key), "boolean", path + "." + key);
            }
            var friction = gear.Element("friction");
            if (friction == null) return;
            RequireObject(friction, "gear.friction");
            foreach (string key in new[] { "mu_lateral", "mu_rolling", "mu_brake", "maxSteerDeg" })
                if (friction.Element(key) != null)
                    Number(friction.Element(key), "gear.friction." + key, nonnegative: true);
        }

        private static void ValidateDecodedGear(GearJson gear, XElement source)
        {
            if (source == null) return;
            if (gear == null || gear.wheels == null)
                throw ConfigError("gear.wheels", "could not be deserialized");
            int index = 0;
            foreach (var wheelSource in source.Element("wheels").Elements())
            {
                string path = "gear.wheels[" + index + "]";
                if (index >= gear.wheels.Length || gear.wheels[index] == null)
                    throw ConfigError(path, "could not be deserialized");
                var wheel = gear.wheels[index++];
                if (wheel.position_m == null || wheel.position_m.Length != 3)
                    throw ConfigError(path + ".position_m", "must contain three finite numbers");
                // Preserve optional defaults explicitly, independent of JsonUtility
                // constructor/field-initializer behavior.
                if (wheelSource.Element("kN_per_m") == null) wheel.kN_per_m = 900;
                if (wheelSource.Element("cNs_per_m") == null) wheel.cNs_per_m = 80;
            }
            if (index != gear.wheels.Length)
                throw ConfigError("gear.wheels", "could not be deserialized");
            var frictionSource = source.Element("friction");
            if (frictionSource != null)
            {
                if (gear.friction == null)
                    throw ConfigError("gear.friction", "could not be deserialized");
                if (frictionSource.Element("mu_lateral") == null) gear.friction.mu_lateral = 0.7;
                if (frictionSource.Element("mu_rolling") == null) gear.friction.mu_rolling = 0.02;
                if (frictionSource.Element("mu_brake") == null) gear.friction.mu_brake = 0.45;
                if (frictionSource.Element("maxSteerDeg") == null) gear.friction.maxSteerDeg = 25;
            }
        }

    }
}
