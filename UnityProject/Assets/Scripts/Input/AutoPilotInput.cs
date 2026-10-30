using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 自动驾驶输入 (IFlightInput 的自动实现, 供 PlayMode 自动化测试与演示):
    /// 满油门滑跑 → 15 m/s 抬轮 → 爬升到目标高度改平, 滚转/偏航增稳保持姿态。
    /// 与核心库 headless 演示 (ConsoleFlightSim --headless) 用同一套逻辑,
    /// 用于验证 Unity 运行时内物理行为与独立核心库一致。
    /// 控制律输出归一化杆量, 偏角限幅与舵机滞后由共享 ControlSurfaceMapper
    /// (AircraftController.Surfaces, 按配置限位) 施加 (P2-6)。
    /// </summary>
    [RequireComponent(typeof(AircraftController))]
    public class AutoPilotInput : MonoBehaviour, IFlightInput
    {
        public float TargetAltitude = 40f;
        public float RotateSpeed = 15f;    // 抬轮空速 m/s

        // 杆量上限 (控制律设计常数 = 可用舵权的比例, 与飞机配置无关;
        // 实际偏角上限 = 比例 × 配置限位, 换机自动缩放)
        private const double ElevStickMax = 0.8;
        private const double AilStickMax = 0.85;
        private const double RudStickMax = 0.7;

        public ControlInputs Poll()
        {
            var controller = GetComponent<AircraftController>();
            var s = controller.State;
            double spd = s.Airspeed(Vec3.Zero);
            double alt = s.Altitude;
            double[] e = s.Euler321();
            double pitch = e[1], bank = e[0];
            double q = s.AngularVelocityBody.Y, p = s.AngularVelocityBody.X, r = s.AngularVelocityBody.Z;

            var surfaces = controller.Surfaces;

            // 目标俯仰剖面
            double pitchTarget;
            if (alt > TargetAltitude) _climbed = true;
            if (_climbed) pitchTarget = 2.0 * Mathd.Deg2Rad;
            else if (spd > RotateSpeed) pitchTarget = 9.0 * Mathd.Deg2Rad;
            else pitchTarget = 0;

            double throttle = _climbed ? 0.25 : 1.0;

            // 控制律输出正力矩方向的等效舵量; 归一化后由 mapper 统一转换舵偏符号。
            double eCmd = ControlLaws.PitchStabilize(pitchTarget, pitch, q);
            double aCmd = ControlLaws.RollStabilize(bank, 0.0, p);
            double rCmd = ControlLaws.YawDamp(r);

            return surfaces.Map(
                Mathd.Clamp(eCmd / surfaces.MaxElevatorRad, -ElevStickMax, ElevStickMax),
                Mathd.Clamp(aCmd / surfaces.MaxAileronRad, -AilStickMax, AilStickMax),
                Mathd.Clamp(rCmd / surfaces.MaxRudderRad, -RudStickMax, RudStickMax),
                throttle, 0, Time.fixedDeltaTime);
        }

        /// <summary>复位起飞爬升阶段标志 (配合机身复位, 否则复位后自动驾驶会
        /// 以为已经爬到高度, 直接进入巡航俯仰而跳过重新起飞的抬轮阶段)。</summary>
        public void ResetState()
        {
            _climbed = false;
        }

        private bool _climbed;
    }
}
