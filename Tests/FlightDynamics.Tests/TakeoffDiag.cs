using System;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

// 起飞诊断: 复刻 AutoPilotInput 逻辑 (满油门滑跑→15m/s 抬轮 9°→40m 改平 0.25 油门),
// 逐秒打印 速度/高度/俯仰/迎角/升降舵/油门/触地。用法: TakeoffDiag.exe <AircraftConfig.json> [秒]
class TakeoffDiag
{
    class Mapper
    {
        public double MaxE = 25 * Mathd.Deg2Rad, MaxA = 20 * Mathd.Deg2Rad, MaxR = 25 * Mathd.Deg2Rad;
        double SE, SA, SR, fe, fa, fr;
        const double Tau = 0.04;
        public Mapper(AeroData a)
        {
            SE = Math.Sign(a.Cmde); SA = Math.Sign(a.Clda); SR = Math.Sign(a.Cndr);
            if (SE == 0) SE = 1; if (SA == 0) SA = 1; if (SR == 0) SR = 1;
        }
        public ControlInputs Map(double pitch, double roll, double yaw, double thr, double dt)
        {
            double ec = SE * Mathd.Clamp(pitch, -1, 1) * MaxE;
            double ac = SA * Mathd.Clamp(roll, -1, 1) * MaxA;
            double rc = SR * Mathd.Clamp(yaw, -1, 1) * MaxR;
            double k = dt / Tau;
            var c = new ControlInputs();
            c.Elevator = fe += (ec - fe) * k;
            c.Aileron = fa += (ac - fa) * k;
            c.Rudder = fr += (rc - fr) * k;
            c.Throttle = Mathd.Clamp(thr, 0, 1);
            return c;
        }
    }

    static int Main(string[] args)
    {
        var cfg = AircraftConfigJson.Load(args[0]);
        double tEnd = args.Length > 1 ? double.Parse(args[1]) : 60;
        var plane = new Aircraft(cfg.Mass, new AeroModel(cfg.Aero), cfg.Engine, new CalmWind())
        { Gear = cfg.Gear };
        // 与 AircraftController.Awake 一致: 结构阻尼按惯量缩放并钳 RK4 稳定上限
        if (plane.Gear != null)
        {
            double h = (1.0 / 120.0) / RigidBodyIntegrator.Substeps;
            var gp = plane.Gear.Params;
            gp.StructDampRoll = Math.Min(gp.StructDampRoll * plane.Mass.Ixx / 0.509, 2.0 * plane.Mass.Ixx / h);
            gp.StructDampPitch = Math.Min(gp.StructDampPitch * plane.Mass.Iyy / 5.07, 2.0 * plane.Mass.Iyy / h);
            gp.StructDampYaw = Math.Min(gp.StructDampYaw * plane.Mass.Izz / 5.27, 2.0 * plane.Mass.Izz / h);
        }
        var integ = new RigidBodyIntegrator(plane);
        var mapper = new Mapper(cfg.Aero);
        var s = new AircraftState();
        s.Position = new Vec3(0, 0, -0.33);
        bool climbed = false;
        double t = 0, dt = 1.0 / 120.0;
        Console.WriteLine("  t     V     alt   pitch  alpha  elevDeg  thr  ground  deCmd");
        for (int i = 0; i <= (int)(tEnd / dt); i++)
        {
            double spd = s.Airspeed(Vec3.Zero);
            double alt = s.Altitude;
            var e = s.Euler321();
            double pitch = e[1], bank = e[0];
            double q = s.AngularVelocityBody.Y, p = s.AngularVelocityBody.X, r = s.AngularVelocityBody.Z;

            double pitchTarget;
            if (alt > 40) climbed = true;
            if (climbed) pitchTarget = 2.0 * Mathd.Deg2Rad;
            else if (spd > 15) pitchTarget = 9.0 * Mathd.Deg2Rad;
            else pitchTarget = 0;
            double throttle = climbed ? 0.25 : 1.0;
            double eCmd = 0.8 * (pitchTarget - pitch) + 0.18 * (0.0 - q);
            double aCmd = 0.6 * (0.0 - bank) - 0.15 * p;
            double rCmd = -0.8 * r;
            var c = mapper.Map(
                Mathd.Clamp(eCmd / mapper.MaxE, -0.8, 0.8),
                Mathd.Clamp(aCmd / mapper.MaxA, -0.85, 0.85),
                Mathd.Clamp(rCmd / mapper.MaxR, -0.7, 0.7), throttle, dt);
            if (i % 120 == 0)
                Console.WriteLine(string.Format(
                    "{0,4}  {1,5:F1}  {2,5:F1}  {3,5:F1}  {4,5:F1}  {5,7:F1}  {6,4:F2}  {7}  {8,6:F1}",
                    t, spd, alt, pitch * Mathd.Rad2Deg, s.Alpha(Vec3.Zero) * Mathd.Rad2Deg,
                    c.Elevator * Mathd.Rad2Deg, c.Throttle,
                    plane.LastGroundContact.AnyContact ? "GND" : "AIR", eCmd * Mathd.Rad2Deg)
                    + string.Format("  gearF=({0:F1},{1:F1},{2:F1}) gearM=({3:F2},{4:F2},{5:F2})",
                        plane.LastGroundContact.TotalForceBody.X, plane.LastGroundContact.TotalForceBody.Y,
                        plane.LastGroundContact.TotalForceBody.Z, plane.LastGroundContact.TotalMomentBody.X,
                        plane.LastGroundContact.TotalMomentBody.Y, plane.LastGroundContact.TotalMomentBody.Z));
            integ.Step(s, c, t, dt);
            t += dt;
        }
        return 0;
    }
}
