using FlightDynamics;
using UnityEngine;

namespace FlightSim
{
    /// <summary>飞行输入接口 (键盘 / 手柄 / 摇杆统一抽象, 开发提示词 6 节)。</summary>
    public interface IFlightInput
    {
        /// <summary>每物理帧轮询, 返回 -1..1 的操纵期望与油门/刹车。</summary>
        ControlInputs Poll();

        /// <summary>清空内部累积状态 (油门/配平/舵机滤波等), 配合
        /// AircraftController.ResetToInitial() 复位飞行时调用, 避免复位后
        /// 残留旧的油门/配平量。</summary>
        void ResetState();
    }
}
