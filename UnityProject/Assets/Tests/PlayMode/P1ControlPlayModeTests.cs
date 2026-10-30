using System;
using System.Reflection;
using FlightDynamics;
using FlightDynamics.Math;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlightSim.Tests
{
    public class P1ControlPlayModeTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _aircraft;
        private AircraftController _c;

        [SetUp]
        public void SetUp()
        {
            _aircraft = new GameObject("P1ControlTestUAV");
            _c = _aircraft.AddComponent<AircraftController>();
            _c.enabled = false;
            _c.State.Position = new Vec3(0, 0, -100);
            _c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            _c.State.VelocityBody = new Vec3(20, 0, 0);
            _c.State.AngularVelocityBody = Vec3.Zero;
        }

        [TearDown]
        public void TearDown()
        {
            if (_aircraft != null) Object.DestroyImmediate(_aircraft);
        }

        [Test, Combinatorial]
        public void ManualAxes_ProduceRequestedPrimaryMoments_ForEveryDerivativeSign(
            [Values(-1, 1)] int pitchSign, [Values(-1, 1)] int rollSign,
            [Values(-1, 1)] int yawSign, [Values(-1, 1)] int direction,
            [Values(0, 1, 2)] int axis)
        {
            ConfigureSigns(pitchSign, rollSign, yawSign);
            var cmd = _c.Surfaces.Map(axis == 0 ? direction : 0,
                axis == 1 ? direction : 0, axis == 2 ? direction : 0, 0.4, 0.2, 0.04);
            double moment = PrimaryMomentIncrement(cmd, axis);
            Assert.Greater(direction * moment, 0, "Pilot direction must match the primary moment.");
            double[] limits = { _c.Surfaces.MaxElevatorRad, _c.Surfaces.MaxAileronRad,
                _c.Surfaces.MaxRudderRad };
            double[] deflections = { cmd.Elevator, cmd.Aileron, cmd.Rudder };
            for (int i = 0; i < 3; i++)
                Assert.That(Math.Abs(deflections[i]), Is.EqualTo(i == axis ? limits[i] : 0).Within(1e-12));
            Assert.That(cmd.Throttle, Is.EqualTo(0.4));
            Assert.That(cmd.Brake, Is.EqualTo(0.2));
        }

        [Test, Combinatorial]
        public void ClosedLoop_CorrectsAttitudeAndRates_WithoutDoubleInversion(
            [Values(-1, 1)] int pitchSign, [Values(-1, 1)] int rollSign,
            [Values(-1, 1)] int yawSign, [Values(-1, 1)] int disturbance,
            [Values(false, true)] bool rateOnly, [Values(false, true)] bool modePilot)
        {
            ConfigureSigns(pitchSign, rollSign, yawSign);
            double pitchTarget = 2 * Mathd.Deg2Rad;
            double bank = rateOnly ? 0 : disturbance * 0.12;
            double pitch = pitchTarget + (rateOnly ? 0 : disturbance * 0.10);
            double p = rateOnly ? disturbance * 0.16 : 0;
            double q = rateOnly ? disturbance * 0.18 : 0;
            double r = disturbance * 0.08;
            _c.State.Attitude = Quat.FromEuler321(bank, pitch, 0);
            _c.State.AngularVelocityBody = new Vec3(p, q, r);
            ControlInputs cmd;
            if (modePilot)
            {
                var pilot = _aircraft.AddComponent<ModePilot>();
                pilot.Engage(FlightMode.Stab);
                cmd = pilot.Poll();
            }
            else
                cmd = _aircraft.AddComponent<AutoPilotInput>().Poll();

            for (int axis = 0; axis < 3; axis++)
                Assert.Greater(-disturbance * PrimaryMomentIncrement(cmd, axis), 0,
                    "Closed-loop primary moment must oppose the disturbance on axis " + axis);

            double servo = Time.fixedDeltaTime / 0.04;
            Assert.That(cmd.Elevator, Is.EqualTo(pitchSign *
                (0.8 * (pitchTarget - pitch) - 0.18 * q) * servo).Within(1e-7));
            Assert.That(cmd.Aileron, Is.EqualTo(rollSign *
                (-0.6 * bank - 0.15 * p) * servo).Within(1e-7));
            Assert.That(cmd.Rudder, Is.EqualTo(yawSign * -0.8 * r * servo).Within(1e-7));
        }

        [Test, Combinatorial]
        public void ModeNavigation_CommandsRequestedTurn_ForEveryDerivativeSign(
            [Values(-1, 1)] int pitchSign, [Values(-1, 1)] int rollSign,
            [Values(-1, 1)] int yawSign, [Values(-1, 1)] int turn,
            [Values(FlightMode.Loiter, FlightMode.Rtl)] FlightMode mode)
        {
            ConfigureSigns(pitchSign, rollSign, yawSign);
            // Home is due east/west: RTL must command the same turn as signed loiter.
            _c.State.Position = new Vec3(0, -turn * 500, -100);
            var pilot = _aircraft.AddComponent<ModePilot>();
            pilot.LoiterBankDeg = turn * 12;
            pilot.Engage(mode);
            ControlInputs cmd = pilot.Poll();
            Assert.Greater(turn * PrimaryMomentIncrement(cmd, 1), 0);
            double desiredBank = (mode == FlightMode.Loiter ? 12 : pilot.MaxBankDeg) * Mathd.Deg2Rad;
            double magnitude = Math.Min(0.6 * desiredBank, 0.5 * _c.Surfaces.MaxAileronRad);
            Assert.That(cmd.Aileron, Is.EqualTo(rollSign * turn * magnitude *
                Time.fixedDeltaTime / 0.04).Within(1e-7), "Keep existing gains and mode limits.");
            Assert.AreSame(pilot, _c.OverrideInput);
        }

        [Test, Combinatorial]
        public void Guard_BlendsCorrectiveMomentInDeflectionSpace_ForEveryDerivativeSign(
            [Values(-1, 1)] int pitchSign, [Values(-1, 1)] int rollSign,
            [Values(-1, 1)] int yawSign, [Values(-1, 1)] int turn,
            [Values(1050, 1200)] int distance)
        {
            ConfigureSigns(pitchSign, rollSign, yawSign);
            _c.State.Position = new Vec3(0, -turn * distance, -100);
            var guard = _aircraft.AddComponent<FlightGuard>();
            // Opposing pilot input is weak enough for the guard to dominate at half authority.
            ControlInputs cmd = _c.Surfaces.Map(0.2, -turn * 0.1, -0.1, 0.6, 0.3, 0.04);
            double elevator = cmd.Elevator, rudder = cmd.Rudder, aileron = cmd.Aileron;
            guard.Apply(ref cmd);
            double authority = distance == 1050 ? 0.5 : 1;
            double guardMagnitude = Math.Min(guard.BankGain * guard.MaxBankRad,
                guard.MaxAileronStick * _c.Surfaces.MaxAileronRad);
            double expected = (1 - authority) * aileron + authority * rollSign * turn * guardMagnitude;
            Assert.That(guard.Authority, Is.EqualTo(authority).Within(1e-6));
            Assert.IsTrue(guard.IsTurningBack);
            Assert.Greater(turn * PrimaryMomentIncrement(cmd, 1), 0);
            Assert.That(cmd.Aileron, Is.EqualTo(expected).Within(1e-7));
            Assert.That(cmd.Elevator, Is.EqualTo(elevator));
            Assert.That(cmd.Rudder, Is.EqualTo(rudder));
            Assert.That(cmd.Throttle, Is.EqualTo(0.6));
            Assert.That(cmd.Brake, Is.EqualTo(0.3));
        }

        [Test, Combinatorial]
        public void GroundYawFeedback_SteersAgainstHeadingOrRate_ForEitherRudderPolarity(
            [Values(-1, 1)] int yawSign, [Values(-1, 1)] int disturbance,
            [Values(false, true)] bool rateOnly)
        {
            ConfigureSigns(-1, -1, yawSign);
            _c.State.Position = new Vec3(0, 0, -0.30);
            _c.State.VelocityBody = new Vec3(10, 0, 0);
            _c.State.Attitude = Quat.FromEuler321(0, 0, rateOnly ? 0 : disturbance * 0.1);
            _c.State.AngularVelocityBody = new Vec3(0, 0, rateOnly ? disturbance * 0.08 : 0);
            var runway = new GameObject("RunwayEnvironment");
            runway.transform.SetParent(_aircraft.transform);
            var pilot = _aircraft.AddComponent<ModePilot>();
            pilot.Engage(FlightMode.Land);
            typeof(ModePilot).GetField("_landPhase", PrivateInstance).SetValue(pilot, 3);
            ControlInputs cmd = pilot.Poll();
            _c.Plane.ComputeForce(_c.State, cmd, 0, out AeroResult aero, out double thrust);
            double controlled = _c.Plane.LastGroundContact.TotalMomentBody.Z;
            cmd.Rudder = 0;
            _c.Plane.ComputeForce(_c.State, cmd, 0, out aero, out thrust);
            double unsteered = _c.Plane.LastGroundContact.TotalMomentBody.Z;
            Assert.Greater(-disturbance * (controlled - unsteered), 0,
                "Steering feedback must oppose heading/rate error independently of tire damping.");
        }

        [Test]
        public void DefaultMapper_PreservesClampFilterReversalAndReset()
        {
            AssertDefaultSigns();
            double e = 0, a = 0, r = 0;
            double dt = 1.0 / 120, k = dt / 0.04;
            for (int step = 0; step < 12; step++)
            {
                double input = step < 6 ? 1.5 : -0.25;
                double clamped = Math.Max(-1, Math.Min(1, input));
                e += (-clamped * _c.Surfaces.MaxElevatorRad - e) * k;
                a += (-clamped * _c.Surfaces.MaxAileronRad - a) * k;
                r += (clamped * _c.Surfaces.MaxRudderRad - r) * k;
                ControlInputs cmd = _c.Surfaces.Map(input, input, input, 2, -1, dt);
                AssertSurfaces(cmd, e, a, r, 1e-12);
                Assert.That(cmd.Throttle, Is.EqualTo(1));
                Assert.That(cmd.Brake, Is.EqualTo(0));
            }
            _c.Surfaces.ResetState();
            AssertSurfaces(_c.Surfaces.Map(0, 0, 0, 0, 0, dt), 0, 0, 0, 1e-12);
        }

        [TestCase(0.0, 10.0, 0.0, 1.0)]
        [TestCase(10.0, 20.0, 9.0, 1.0)]
        [TestCase(100.0, 20.0, 2.0, 0.25)]
        public void DefaultAutoPilot_PreservesFlightProfileAndGainMagnitudes(
            double altitude, double speed, double targetPitchDeg, double throttle)
        {
            AssertDefaultSigns();
            _c.State.Position = new Vec3(0, 0, -altitude);
            _c.State.VelocityBody = new Vec3(speed, 0, 0);
            _c.State.Attitude = Quat.FromEuler321(0.1, 0.04, 0);
            _c.State.AngularVelocityBody = new Vec3(0.08, -0.03, 0.06);
            var pilot = _aircraft.AddComponent<AutoPilotInput>();
            double eTarget = -(0.8 * (targetPitchDeg * Mathd.Deg2Rad - 0.04) + 0.18 * 0.03);
            double aTarget = 0.6 * 0.1 + 0.15 * 0.08;
            double rTarget = -0.8 * 0.06;
            double e = 0, a = 0, r = 0, k = Time.fixedDeltaTime / 0.04;
            for (int step = 0; step < 8; step++)
            {
                e += (eTarget - e) * k;
                a += (aTarget - a) * k;
                r += (rTarget - r) * k;
                ControlInputs cmd = pilot.Poll();
                AssertSurfaces(cmd, e, a, r, 1e-10);
                Assert.That(cmd.Throttle, Is.EqualTo(throttle));
                Assert.That(cmd.Brake, Is.EqualTo(0));
            }
        }

        [TestCase(false, 0.85, 0.7)]
        [TestCase(true, 0.7, 0.5)]   // U15: STAB 副翼钳制 ±0.7
        public void DefaultClosedLoop_PreservesSaturatedAuthority(bool modePilot, double ailLimit, double rudLimit)
        {
            AssertDefaultSigns();
            _c.State.AngularVelocityBody = new Vec3(10, 10, 10);
            ControlInputs cmd;
            if (modePilot)
            {
                var pilot = _aircraft.AddComponent<ModePilot>();
                pilot.Engage(FlightMode.Stab);
                cmd = pilot.Poll();
            }
            else
                cmd = _aircraft.AddComponent<AutoPilotInput>().Poll();
            double k = Time.fixedDeltaTime / 0.04;
            // U15: STAB 钳制放宽到 ±0.9/±0.7 (杆量前馈需更高权限);
            // AutoPilotInput (非 STAB) 保持 ±0.8
            double eLimit = modePilot ? 0.9 : 0.8;
            AssertSurfaces(cmd, eLimit * _c.Surfaces.MaxElevatorRad * k,
                ailLimit * _c.Surfaces.MaxAileronRad * k,
                -rudLimit * _c.Surfaces.MaxRudderRad * k, 1e-10);
        }

        [TestCase(10.0, true)]
        [TestCase(-10.0, false)]
        [TestCase(2.0, false)]
        [TestCase(-2.0, false)]
        [TestCase(8.0, false)]
        [TestCase(8.01, true)]
        public void Crash_UsesPositiveDownAirborneSink_NotClimbOrGentleContact(
            double velocityDown, bool expectedCrash)
        {
            _c.OverrideInput = new NeutralInput();
            SetPrivate("_input", _c.OverrideInput);
            _c.State.VelocityBody = new Vec3(0, 0, velocityDown);
            FixedStep();
            Assert.IsFalse(_c.OnGround, "Sample must be captured while airborne.");
            Assert.IsFalse(_c.IsCrashed, "An airborne sink must not crash before contact.");

            // Preserve the last airborne sample through contact, as the real gear cache does.
            // The contact itself is gentle and level, so attitude thresholds cannot mask the sign bug.
            _c.State.Position = new Vec3(0, 0, -0.327);
            _c.State.Attitude = Quat.FromEuler321(0, 0, 0);
            _c.State.AngularVelocityBody = Vec3.Zero;
            _c.State.VelocityBody = Vec3.Zero;
            _c.Plane.LastGroundContact = new GroundContactResult { AnyContact = true };
            FixedStep();
            Assert.IsTrue(_c.OnGround, "The real landing gear must report contact.");
            Assert.That(Math.Abs(_c.BankDeg), Is.LessThan(1));
            Assert.That(Math.Abs(_c.PitchDeg), Is.LessThan(1));
            Assert.That(Math.Abs(_c.VerticalSpeed), Is.LessThan(1));
            Assert.That(_c.IsCrashed, Is.EqualTo(expectedCrash),
                "Only a positive-down airborne sample above CrashSinkRate is a hard landing.");
        }

        [Test]
        public void Crash_LevelDescendingImpact_IsNotMaskedByPitchOrBank()
        {
            _c.OverrideInput = new NeutralInput();
            SetPrivate("_input", _c.OverrideInput);
            _c.State.Position = new Vec3(0, 0, -0.36);
            _c.State.VelocityBody = new Vec3(0, 0, 10);
            FixedStep();
            Assert.IsTrue(_c.OnGround);
            Assert.That(Math.Abs(_c.BankDeg), Is.LessThan(1));
            Assert.That(Math.Abs(_c.PitchDeg), Is.LessThan(1));
            Assert.IsTrue(_c.IsCrashed, "Level +10 m/s NED descent must trigger the sink criterion.");
        }

        private void ConfigureSigns(int pitch, int roll, int yaw)
        {
            AeroData data = _c.Plane.Aero.Data;
            data.Cmde = pitch * Math.Abs(data.Cmde);
            data.Clda = roll * Math.Abs(data.Clda);
            data.Cndr = yaw * Math.Abs(data.Cndr);
            var limits = new MaxDeflection
            {
                Elevator = _c.Surfaces.MaxElevatorRad * Mathd.Rad2Deg,
                Aileron = _c.Surfaces.MaxAileronRad * Mathd.Rad2Deg,
                Rudder = _c.Surfaces.MaxRudderRad * Mathd.Rad2Deg
            };
            SetPrivate("_surfaces", new ControlSurfaceMapper(data, limits));
        }

        private double PrimaryMomentIncrement(ControlInputs cmd, int axis)
        {
            // Evaluate one surface at a time; stability derivatives and cross-axis controls
            // must not hide an incorrect primary control moment.
            var isolated = ControlInputs.Zero;
            if (axis == 0) isolated.Elevator = cmd.Elevator;
            if (axis == 1) isolated.Aileron = cmd.Aileron;
            if (axis == 2) isolated.Rudder = cmd.Rudder;
            AeroModel aero = _c.Plane.Aero;
            Vec3 velocity = new Vec3(20, 0, 0);
            Vec3 zero = aero.Compute(velocity, Vec3.Zero, 1.225, ControlInputs.Zero).MomentBody;
            Vec3 moment = aero.Compute(velocity, Vec3.Zero, 1.225, isolated).MomentBody - zero;
            return axis == 0 ? moment.Y : axis == 1 ? moment.X : moment.Z;
        }

        private void AssertDefaultSigns()
        {
            Assert.That(_c.Plane.Aero.Data.Cmde, Is.LessThan(0));
            Assert.That(_c.Plane.Aero.Data.Clda, Is.LessThan(0));
            Assert.That(_c.Plane.Aero.Data.Cndr, Is.GreaterThan(0));
        }

        private static void AssertSurfaces(ControlInputs cmd, double elevator, double aileron,
            double rudder, double tolerance)
        {
            Assert.That(cmd.Elevator, Is.EqualTo(elevator).Within(tolerance), "Elevator");
            Assert.That(cmd.Aileron, Is.EqualTo(aileron).Within(tolerance), "Aileron");
            Assert.That(cmd.Rudder, Is.EqualTo(rudder).Within(tolerance), "Rudder");
        }

        private void SetPrivate(string name, object value)
        {
            FieldInfo field = typeof(AircraftController).GetField(name, PrivateInstance);
            Assert.NotNull(field, name);
            field.SetValue(_c, value);
        }

        private void FixedStep()
        {
            MethodInfo step = typeof(AircraftController).GetMethod("FixedUpdate", PrivateInstance);
            Assert.NotNull(step);
            step.Invoke(_c, null);
        }

        private sealed class NeutralInput : IFlightInput
        {
            public ControlInputs Poll() => ControlInputs.Zero;
            public void ResetState() { }
        }
    }
}
