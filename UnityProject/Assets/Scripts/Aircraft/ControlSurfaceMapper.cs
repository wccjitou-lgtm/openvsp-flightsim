using FlightDynamics;
using FlightDynamics.Math;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 操纵面映射 (开发提示词 6 节): 把 -1..1 的操纵输入映射为舵面偏角,
    /// 施加最大偏角限制 (来自配置), 并带一阶滞后模拟舵机响应。
    /// 注意: 滞后对数值稳定性是必要的 (滚转模态快, 无滞后滤波的刚性
    /// 操纵输入会与定步长积分发生数值自激)。
    /// </summary>
    public class ControlSurfaceMapper
    {
        private readonly double _maxElevator, _maxAileron, _maxRudder;
        private readonly double _elevatorSign, _aileronSign, _rudderSign;
        private const double ServoTau = 0.04; // 舵机时间常数 s

        private double _elevCmd, _ailCmd, _rudCmd;

        public ControlSurfaceMapper(AeroData aero, MaxDeflection maxDef)
        {
            _maxElevator = maxDef.Elevator * Mathd.Deg2Rad;
            _maxAileron = maxDef.Aileron * Mathd.Deg2Rad;
            _maxRudder = maxDef.Rudder * Mathd.Deg2Rad;
            _elevatorSign = ControlLaws.SafeSign(aero.Cmde, "Cmde(升降舵)");
            _aileronSign = ControlLaws.SafeSign(aero.Clda, "Clda(副翼)");
            _rudderSign = ControlLaws.SafeSign(aero.Cndr, "Cndr(方向舵)");
        }

        /// <summary>配置的最大偏角 (rad), 供控制律把指令归一化为杆量。</summary>
        public double MaxElevatorRad { get { return _maxElevator; } }
        public double MaxAileronRad { get { return _maxAileron; } }
        public double MaxRudderRad { get { return _maxRudder; } }

        /// <summary>每物理帧调用。pitchInput: +1 = 拉杆 (抬头); rollInput: +1 = 右压杆 (右滚);
        /// yawInput: +1 = 右舵 (机头右偏)。即杆量语义统一为"人体操纵方向", 偏角符号由本类转换。</summary>
        public ControlInputs Map(double pitchInput, double rollInput, double yawInput,
                                 double throttle, double brake, double dt)
        {
            // 正杆量对应正主轴力矩; 舵偏方向由实际操纵导数决定, 不依赖机型约定。
            _elevCmd = _elevatorSign * Mathd.Clamp(pitchInput, -1, 1) * _maxElevator;
            _ailCmd = _aileronSign * Mathd.Clamp(rollInput, -1, 1) * _maxAileron;
            _rudCmd = _rudderSign * Mathd.Clamp(yawInput, -1, 1) * _maxRudder;

            // 一阶舵机滞后
            double k = dt / ServoTau;
            var c = new ControlInputs();
            c.Elevator = _elevFilter += (_elevCmd - _elevFilter) * k;
            c.Aileron = _ailFilter += (_ailCmd - _ailFilter) * k;
            c.Rudder = _rudFilter += (_rudCmd - _rudFilter) * k;
            c.Throttle = Mathd.Clamp(throttle, 0, 1);
            c.Brake = Mathd.Clamp(brake, 0, 1);
            return c;
        }

        /// <summary>清空舵机滞后滤波状态 (配合 AircraftController.ResetToInitial(),
        /// 否则复位后舵面会从旧偏角缓慢滑回中立, 而不是立刻归零)。</summary>
        public void ResetState()
        {
            _elevCmd = _ailCmd = _rudCmd = 0;
            _elevFilter = _ailFilter = _rudFilter = 0;
        }

        private double _elevFilter, _ailFilter, _rudFilter;
    }
}
