using System;
using FlightDynamics.Math;

namespace FlightDynamics
{
    /// <summary>飞机装配: 质量 + 气动 + 发动机 + 风模型 + 可选起落架, 计算总力/总力矩。</summary>
    public class Aircraft
    {
        public readonly MassData Mass;
        public readonly AeroModel Aero;
        public readonly EngineData Engine;
        public IWindModel Wind;

        /// <summary>起落架/地面接触 (Phase 2+; null = 无地面模型, 如空中共用状态)。</summary>
        public GroundContactModel Gear;

        /// <summary>上一步的地面接触结果 (供起落架压缩显示/音效/调试)。</summary>
        public GroundContactResult LastGroundContact;

        public Aircraft(MassData mass, AeroModel aero, EngineData engine, IWindModel wind)
        {
            Mass = mass;
            Aero = aero;
            Engine = engine;
            Wind = wind ?? new CalmWind();
        }

        /// <summary>计算指定状态下的总力 (机体轴, N)。同时通过 out 返回气动细节。</summary>
        public Vec3 ComputeForce(AircraftState state, ControlInputs controls, double time,
                                 out AeroResult aeroOut, out double thrustOut)
        {
            Vec3 windNed = Wind.WindAt(state.Position, time);
            Vec3 vRelBody = state.RelativeWindBody(windNed);
            double altitude = state.Altitude;
            double rho = AtmosphereModel.Density(altitude);
            double V = vRelBody.Length;

            AeroResult aero = Aero.Compute(vRelBody, state.AngularVelocityBody, rho, controls);

            // 推力: 沿机体 x 前 (简化, 不产生力矩)
            double thrust = Engine.Thrust(controls.Throttle, V);
            Vec3 thrustForce = new Vec3(thrust, 0, 0);

            // 重力: NED 系 (0,0,+mg) (z 向下), 旋转到机体轴
            Vec3 gravityNed = new Vec3(0, 0, Mass.MassKg * AtmosphereModel.G0);
            Vec3 gravityBody = state.Attitude.Conjugate.Rotate(gravityNed);

            // 地面接触 (起落架)
            Vec3 gearForce = Vec3.Zero;
            if (Gear != null)
            {
                // Aerodynamic rudder polarity is model-specific; steering is positive-right.
                double steer = Aero.Data.Cndr < 0 ? -controls.Rudder : controls.Rudder;
                GroundContactResult g = Gear.Compute(state, controls.Brake, steer);
                LastGroundContact = g;
                gearForce = g.TotalForceBody;
            }

            aeroOut = aero;
            thrustOut = thrust;
            return aero.ForceBody + thrustForce + gravityBody + gearForce;
        }

        /// <summary>计算指定状态下的总力矩 (机体轴, N·m, 关于重心)。推力过重心无力矩。</summary>
        public Vec3 ComputeMoment(AircraftState state, AeroResult aero)
        {
            Vec3 m = aero.MomentBody;
            if (Gear != null)
                m = m + LastGroundContact.TotalMomentBody;
            return m;
        }
    }

    /// <summary>
    /// 6DOF 刚体积分器 (开发提示词 4.4 节): 牛顿平动 + 欧拉转动方程 + 四元数运动学,
    /// 固定步长 RK4。步长与渲染帧率解耦 (Unity 侧在 FixedUpdate 中驱动)。
    /// </summary>
    public class RigidBodyIntegrator
    {
        public readonly Aircraft Plane;
        private readonly Matrix3 _inertia;
        private readonly Matrix3 _inertiaInv;

        public RigidBodyIntegrator(Aircraft plane)
        {
            Plane = plane;
            _inertia = plane.Mass.InertiaTensor();
            _inertiaInv = _inertia.Inverse();
        }

        /// <summary>状态导数 (13 维: pos3 + quat4 + vel3 + omega3)。</summary>
        private StateDerivative Derivative(AircraftState s, ControlInputs c, double t)
        {
            var d = new StateDerivative();
            AeroResult aero;
            double thrust;
            Vec3 force = Plane.ComputeForce(s, c, t, out aero, out thrust);
            Vec3 moment = Plane.ComputeMoment(s, aero);

            d.PosDot = s.Attitude.Rotate(s.VelocityBody);   // NED 位置率

            // 四元数运动学: q̇ = 0.5 · q ⊗ (0, p, q, r)
            var wq = new Quat(0, s.AngularVelocityBody.X, s.AngularVelocityBody.Y, s.AngularVelocityBody.Z);
            Quat qd = s.Attitude * wq;
            d.QuatDot = 0.5 * qd;

            // 体轴牛顿: v̇ = F/m − ω×v
            d.VelDot = force * (1.0 / Plane.Mass.MassKg)
                     - s.AngularVelocityBody.Cross(s.VelocityBody);

            // 欧拉方程: ω̇ = I⁻¹(M − ω×(Iω))
            Vec3 iw = _inertia.Multiply(s.AngularVelocityBody);
            d.OmegaDot = _inertiaInv.Multiply(moment - s.AngularVelocityBody.Cross(iw));
            return d;
        }

        /// <summary>推进一步 (RK4)。控制量在整个步内保持常值 (零阶保持)。
        /// 内部固定细分为 4 个子步 (dt_eff = dt/4):
        ///   - 滚转模态衰减率 |λ|≈614/s (小 Ixx + 大展长), dt=1/120 时
        ///     |λ|·dt≈5.1 超出 RK4 稳定域 (~3.5), 会发生数值自激振荡
        ///     (表现: 滚转率高频振荡 + 姿态角单调漂移发散);
        ///   - 起落架弹簧刚度同样需要子步。
        /// 4 子步后 |λ|·dt≈1.3, 稳定裕度充足。</summary>
        /// <summary>内部固定细分子步数 (外部 Gear 阻尼自适应钳制也用它算有效步长)。</summary>
        public const int Substeps = 4;

        public void Step(AircraftState s, ControlInputs controls, double time, double dt)
        {
            double h = dt / Substeps;
            for (int i = 0; i < Substeps; i++)
                StepRk4(s, controls, time + i * h, h);
        }

        private void StepRk4(AircraftState s, ControlInputs controls, double time, double dt)
        {
            AircraftState s2 = s.Clone(), s3 = s.Clone(), s4 = s.Clone();

            StateDerivative k1 = Derivative(s, controls, time);

            AddScaled(s2, s, k1, 0.5 * dt);
            StateDerivative k2 = Derivative(s2, controls, time + 0.5 * dt);

            AddScaled(s3, s, k2, 0.5 * dt);
            StateDerivative k3 = Derivative(s3, controls, time + 0.5 * dt);

            AddScaled(s4, s, k3, dt);
            StateDerivative k4 = Derivative(s4, controls, time + dt);

            // 加权合成
            s.Position = s.Position + (dt / 6.0) * (k1.PosDot + 2.0 * k2.PosDot + 2.0 * k3.PosDot + k4.PosDot);
            Quat qd = (1.0 / 6.0) * (k1.QuatDot + 2.0 * k2.QuatDot + 2.0 * k3.QuatDot + k4.QuatDot);
            s.Attitude = new Quat(
                s.Attitude.W + dt * qd.W, s.Attitude.X + dt * qd.X,
                s.Attitude.Y + dt * qd.Y, s.Attitude.Z + dt * qd.Z).Normalized();
            s.VelocityBody = s.VelocityBody + (dt / 6.0) * (k1.VelDot + 2.0 * k2.VelDot + 2.0 * k3.VelDot + k4.VelDot);
            s.AngularVelocityBody = s.AngularVelocityBody + (dt / 6.0) * (k1.OmegaDot + 2.0 * k2.OmegaDot + 2.0 * k3.OmegaDot + k4.OmegaDot);
        }

        private static void AddScaled(AircraftState dst, AircraftState src, StateDerivative d, double h)
        {
            dst.Position = src.Position + h * d.PosDot;
            dst.Attitude = new Quat(
                src.Attitude.W + h * d.QuatDot.W, src.Attitude.X + h * d.QuatDot.X,
                src.Attitude.Y + h * d.QuatDot.Y, src.Attitude.Z + h * d.QuatDot.Z).Normalized();
            dst.VelocityBody = src.VelocityBody + h * d.VelDot;
            dst.AngularVelocityBody = src.AngularVelocityBody + h * d.OmegaDot;
        }

        private struct StateDerivative
        {
            public Vec3 PosDot;
            public Quat QuatDot;
            public Vec3 VelDot;
            public Vec3 OmegaDot;
        }
    }
}
