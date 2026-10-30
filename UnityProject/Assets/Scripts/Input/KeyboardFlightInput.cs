using FlightDynamics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlightSim
{
    /// <summary>
    /// 键盘飞行输入 (开发提示词 6 节, 基于 Input System 包):
    ///   W/S 或 ↑/↓  : 俯仰 (遥控器 Mode 2 习惯: S/↓ = 拉杆抬头, W/↑ = 低头)
    ///   A/D 或 ←/→  : 滚转 (D = 右压杆右滚; 杆量经 ControlSurfaceMapper 按配置
    ///                 Clda<0 符号取负转换 —— 此前 D 实际左滚, 与遥控器惯例相反)
    ///   Q/E         : 方向舵 (Q = 左舵 / E = 右舵, 机头随之左/右偏)
    ///   Shift/Ctrl  : 油门增减
    ///   B           : 刹车 (按住)
    ///   R           : 配平俯仰 (按住时渐进配平)
    /// </summary>
    [RequireComponent(typeof(AircraftController))]
    public class KeyboardFlightInput : MonoBehaviour, IFlightInput
    {
        [Header("油门速率")] public float ThrottleRate = 0.5f;   // 每秒全行程比例
        [Header("配平速率")] public float TrimRate = 3.0f;       // 度/秒

        private float _throttle;
        private float _trim;  // 度, 迭加到俯仰输入

        public ControlInputs Poll()
        {
            var kb = Keyboard.current;
            if (WorkshopUI.IsOpen) return ControlInputs.Zero;   // R13: 工坊打字不进飞行输入
            if (kb == null) return ControlInputs.Zero;

            // ---- 俯仰/滚转 (杆量 -1..1) ----
            // 俯仰按遥控器 (Mode 2) 习惯: 向下拨杆 (S/↓) = 拉杆抬头, 向上推杆 (W/↑) = 低头
            float pitch = 0f, roll = 0f, yaw = 0f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) pitch += 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) pitch -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) roll += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) roll -= 1f;
            if (kb.eKey.isPressed) yaw += 1f;   // 右舵
            if (kb.qKey.isPressed) yaw -= 1f;   // 左舵

            // ---- 油门 ----
            if (kb.leftShiftKey.isPressed) _throttle += ThrottleRate * Time.fixedDeltaTime;
            if (kb.leftCtrlKey.isPressed) _throttle -= ThrottleRate * Time.fixedDeltaTime;
            _throttle = Mathf.Clamp01(_throttle);

            // ---- 配平 ----
            if (kb.rKey.isPressed) _trim += TrimRate * Time.fixedDeltaTime;
            if (kb.fKey.isPressed) _trim -= TrimRate * Time.fixedDeltaTime;
            _trim = Mathf.Clamp(_trim, -25f, 25f);

            // ---- 刹车 ----
            float brake = kb.bKey.isPressed ? 1f : 0f;

            // 共享 AircraftController.Surfaces (按配置限位, P2-6)
            var controller = GetComponent<AircraftController>();
            if (controller == null)
                return ControlInputs.Zero;   // 残骸/异常组合 (曾致每帧 NRE 刷爆 8.7GB 日志拖死主线程)
            return controller.Surfaces.Map(
                Mathf.Clamp(pitch + _trim / 25f, -1.25f, 1.25f),
                roll, yaw, _throttle, brake, Time.fixedDeltaTime);
        }

        /// <summary>复位油门/配平累积量 (配合机身复位, 否则复位后残留旧油门冲出跑道)。</summary>
        public void ResetState()
        {
            _throttle = 0f;
            _trim = 0f;
        }
    }
}
