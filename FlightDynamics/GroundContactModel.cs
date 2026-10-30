using System;
using System.Collections.Generic;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>单个起落架机轮 (标准机体轴, 原点=重心)。</summary>
    public class GearWheel
    {
        public string Name = "";
        /// <summary>机轮接地点在机体轴的位置 (m)。z 向下为正; 静止时接地点应在 z≈+0.35。</summary>
        public Vec3 Position;
        public double SpringN = 900.0;   // 法向刚度 N/m
        public double DamperNs = 80.0;   // 法向阻尼 N·s/m
        /// <summary>法向力上限 (N), 等效轮胎非线性缓冲, 约 4 倍静态轮载。</summary>
        public double MaxLoadN = 250.0;
        public bool Steerable;           // 前轮转向 (跟随方向舵输入)
        public bool HasBrake;            // 主轮刹车

        public GearWheel(string name, Vec3 pos, double k, double c, bool steer, bool brake)
        {
            Name = name; Position = pos; SpringN = k; DamperNs = c;
            Steerable = steer; HasBrake = brake;
        }
    }

    /// <summary>地面接触参数。</summary>
    public class GroundContactParams
    {
        public double MuLateral = 0.7;   // 轮胎侧向摩擦 (侧向抓地)
        public double MuRolling = 0.02;  // 滚动阻力系数
        public double MuBrake = 0.45;    // 刹车摩擦系数
        public double MaxSteerRad = 25.0 * Mathd.Deg2Rad;

        // 接地姿态结构阻尼 (等效支柱侧向刚度耗散/减摆器)。
        // 前三点布局在垂直冲击瞬间存在几何性净俯仰激励 (轮力 × 前臂),
        // 限幅 + 该阻尼共同防止冲击激发翻覆; 仅在有轮接地时施加。
        public double StructDampPitch = 150.0;  // N·m/(rad/s)
        public double StructDampRoll = 100.0;
        public double StructDampYaw = 50.0;
    }

    /// <summary>地面接触计算结果 (调试/遥测用)。</summary>
    public struct GroundContactResult
    {
        public Vec3 TotalForceBody;      // N (机体轴)
        public Vec3 TotalMomentBody;     // N·m (机体轴, 关于重心)
        public int WheelsOnGround;
        public double MaxPenetration;    // m
        public bool AnyContact;
    }

    /// <summary>
    /// 起落架/地面接触模型 (开发提示词 4.6 节): 每个机轮一个弹簧-阻尼法向力 +
    /// 各向异性摩擦 (纵向滚动阻力小 / 侧向抓地强), 前轮随方向舵输入转向,
    /// 主轮刹车。不追求真实轮胎动力学。
    /// 地面: NED z=0 平面, z 向下。机轮接地点 z_NED > 0 即穿透。
    /// </summary>
    public class GroundContactModel
    {
        public readonly List<GearWheel> Wheels = new List<GearWheel>();
        public readonly GroundContactParams Params = new GroundContactParams();

        /// <summary>地形模型 (Phase 4): 默认平坦 z=0; 山地场景设为 HeightmapTerrain。</summary>
        public ITerrainModel Terrain = FlatTerrain.Default;

        public GroundContactModel AddWheel(GearWheel w)
        {
            Wheels.Add(w);
            return this;
        }

        /// <summary>
        /// 计算全部机轮的合力/合力矩 (机体轴)。
        /// </summary>
        /// <param name="state">飞行状态</param>
        /// <param name="brakeInput">刹车 0..1 (作用在 HasBrake 机轮)</param>
        /// <param name="steerInput">前轮转向 -1..1 (×MaxSteerRad; 正 = 机头右转, 跟随方向舵)</param>
        public GroundContactResult Compute(AircraftState state, double brakeInput, double steerInput)
        {
            var res = new GroundContactResult();
            res.TotalForceBody = Vec3.Zero;
            res.TotalMomentBody = Vec3.Zero;

            Vec3 posNed = state.Position;
            Quat q = state.Attitude;   // 体→NED

            foreach (GearWheel w in Wheels)
            {
                // 机轮接地点的世界位置与速度 (NED)
                Vec3 pNed = posNed + q.Rotate(w.Position);
                // 地形上表面 NED z = -HeightAt; 穿透 = z_w + 地形高 (Phase 4 接入地形)
                double penetration = pNed.Z + Terrain.HeightAt(pNed.X, pNed.Y);
                if (penetration <= 0.0)
                    continue;

                // 接地点速度 = 质心速度 + ω×r (先在体轴算叉积, 再转到 NED)
                Vec3 vPointNed = state.VelocityNed
                               + q.Rotate(state.AngularVelocityBody.Cross(w.Position));

                // ---- 法向力 (弹簧-阻尼, 只压不拉, 峰值限幅) ----
                double vzDown = vPointNed.Z;                     // 正 = 向下压向地面
                // U8: 回弹时阻尼项至多抵消弹簧 (法向力连续过零, 不钳负) —
                // 原实现 damper 把合力拉负后被硬钳 0, 轮子瞬间"松手"再下帧
                // 重撞, 是能量泵送源 (轻机+软簧会越弹越翻, 实测翻覆)。
                double dampTerm = w.DamperNs * vzDown;
                if (dampTerm < -w.SpringN * penetration)
                    dampTerm = -w.SpringN * penetration;
                double normal = w.SpringN * penetration + dampTerm; // 压入 (vzDown>0) 增大法向力
                if (normal < 0.0) normal = 0.0;                   // 穿透=0 时无残余力
                if (normal > w.MaxLoadN) normal = w.MaxLoadN;     // 等效轮胎非线性缓冲
                // 法向力沿 NED 上方向 (-z)
                Vec3 fNed = new Vec3(0, 0, -normal);

                // ---- 摩擦 (各向异性): 在地面平面内 ----
                // 机轮滚动方向 (NED): 机体 x 轴 (可被前轮转向偏转)
                double steer = w.Steerable ? steerInput * Params.MaxSteerRad : 0.0;
                Vec3 rollDirBody = new Vec3(System.Math.Cos(steer), System.Math.Sin(steer), 0);
                Vec3 rollDirNed = q.Rotate(rollDirBody);

                Vec3 vHoriz = new Vec3(vPointNed.X, vPointNed.Y, 0);
                double speed = vHoriz.Length;
                if (speed > 1e-6 && normal > 0.0)
                {
                    // 侧向: 垂直于滚动方向的水平分量 → 强摩擦 (抓地/转向)
                    double vRoll = vHoriz.Dot(rollDirNed);
                    Vec3 vLat = vHoriz - vRoll * new Vec3(rollDirNed.X, rollDirNed.Y, 0);
                    double latSpeed = vLat.Length;

                    // 纵向: 滚动阻力 (+ 刹车)
                    double muLong = Params.MuRolling + (w.HasBrake ? brakeInput * Params.MuBrake : 0.0);
                    Vec3 fRollNed = -System.Math.Sign(vRoll) * muLong * normal
                                    * new Vec3(rollDirNed.X, rollDirNed.Y, 0);

                    // 侧向摩擦 (低速渐入, 避免数值抖动)
                    double fLatMag = System.Math.Min(Params.MuLateral * normal,
                                              latSpeed * w.SpringN * 0.05);
                    Vec3 fLatNed = latSpeed > 1e-9 ? -(fLatMag / latSpeed) * vLat : Vec3.Zero;

                    fNed = fNed + fRollNed + fLatNed;
                }

                // 转换到机体轴并累加 (力 + 对重心之力矩)
                Vec3 fBody = q.Conjugate.Rotate(fNed);
                res.TotalForceBody = res.TotalForceBody + fBody;
                res.TotalMomentBody = res.TotalMomentBody + w.Position.Cross(fBody);

                res.WheelsOnGround++;
                res.MaxPenetration = System.Math.Max(res.MaxPenetration, penetration);
            }

            res.AnyContact = res.WheelsOnGround > 0;

            // 接地姿态结构阻尼 (见 GroundContactParams 注释)
            if (res.AnyContact)
            {
                Vec3 wBody = state.AngularVelocityBody;
                res.TotalMomentBody = res.TotalMomentBody - new Vec3(
                    Params.StructDampRoll * wBody.X,
                    Params.StructDampPitch * wBody.Y,
                    Params.StructDampYaw * wBody.Z);
            }
            return res;
        }
    }
}
