using System;
using FlightDynamics;
using FlightDynamics.IO;
using FlightDynamics.Math;

// 地面弹射诊断: 飞机静止放地面 6s (油门 0), 比较"按默认机轮深 0.327 出生"与"按实际轮深出生"
// 的俯仰扰动。用法: GroundBounce.exe <AircraftConfig.json> [出生轮深m, 默认读配置]
class GroundBounce
{
    static int Main(string[] args)
    {
        var cfg = AircraftConfigJson.Load(args[0]);
        double wheelZ = 0;
        foreach (var w in cfg.Gear.Wheels) wheelZ = Math.Max(wheelZ, w.Position.Z);
        double spawnDepth = args.Length > 1 ? double.Parse(args[1]) : wheelZ;
        if (args.Length > 2) AtmosphereModel.AltitudeOffsetM = double.Parse(args[2]);   // 场地海拔
        Console.WriteLine($"gear max wheel z = {wheelZ:F3} m; spawn uses {spawnDepth:F3} m " +
                          $"(埋深 {spawnDepth - wheelZ:F3} m)");

        var plane = new Aircraft(cfg.Mass, new AeroModel(cfg.Aero), cfg.Engine, new CalmWind())
        { Gear = cfg.Gear };
        var integ = new RigidBodyIntegrator(plane);
        var s = new AircraftState();
        s.Position = new Vec3(0, 0, -spawnDepth);

        double t = 0, dt = 1.0 / 120.0;
        double maxPitch = 0, maxRoll = 0, maxAlt = -1;
        for (int i = 0; i <= (int)(6.0 / dt); i++)
        {
            double[] e = s.Euler321();
            maxPitch = Math.Max(maxPitch, Math.Abs(e[1] * Mathd.Rad2Deg));
            maxRoll = Math.Max(maxRoll, Math.Abs(e[0] * Mathd.Rad2Deg));
            maxAlt = Math.Max(maxAlt, s.Altitude);
            integ.Step(s, ControlInputs.Zero, t, dt);
            t += dt;
            if (i % 120 == 0)
                Console.WriteLine($"t={t,4:F1} pitch={e[1] * Mathd.Rad2Deg,7:F2} roll={e[0] * Mathd.Rad2Deg,7:F2} alt={s.Altitude,6:F3}");
        }
        Console.WriteLine($"max|pitch|={maxPitch:F1}°  max|roll|={maxRoll:F1}°  maxAlt={maxAlt:F2} m");
        return maxPitch < 5 && maxRoll < 5 ? 0 : 1;
    }
}
