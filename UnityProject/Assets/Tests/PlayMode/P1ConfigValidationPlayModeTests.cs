using System;
using NUnit.Framework;

namespace FlightSim.Tests
{
    public class P1ConfigValidationPlayModeTests
    {
        private const string Valid = @"{
            ""name"": ""ValidationFixture"",
            ""reference"": {""Sref_m2"": 3.1, ""bref_m"": 4, ""cref_m"": 0.775, ""cg_m"": [1, 0, -0.1]},
            ""mass"": {""mass_kg"": 12, ""inertia_kgm2"": {
                ""Ixx"": 2, ""Iyy"": 3, ""Izz"": 4, ""Ixy"": 0, ""Ixz"": 0, ""Iyz"": 0}},
            ""aero"": {
                ""CL0"": 0, ""CLalpha"": 4.4, ""CLq"": 7, ""CD0"": 0.03, ""k_induced"": 0.06,
                ""Cm0"": 0, ""Cmalpha"": -0.3, ""Cmq"": -4,
                ""CYbeta"": 0, ""Clbeta"": 0, ""Cnbeta"": 0,
                ""CYp"": 0, ""Clp"": 0, ""Cnp"": 0, ""CYr"": 0, ""Clr"": 0, ""Cnr"": 0,
                ""controls"": {
                    ""elevator"": {""CLde"": 0, ""Cmde"": -0.5, ""maxDeflectionDeg"": 25},
                    ""aileron"": {""CYda"": 0, ""Clda"": -0.1, ""Cnda"": 0, ""maxDeflectionDeg"": 20},
                    ""rudder"": {""CYdr"": 0, ""Cldr"": 0, ""Cndr"": 0.05, ""maxDeflectionDeg"": 25}}},
            ""engine"": {""maxThrust_N"": 40},
            ""gear"": {
                ""wheels"": [{""name"": ""nose"", ""position_m"": [-1, 0, 0.35],
                    ""kN_per_m"": 900, ""cNs_per_m"": 80, ""steerable"": true, ""brake"": false}],
                ""friction"": {""mu_lateral"": 0.7, ""mu_rolling"": 0.02,
                    ""mu_brake"": 0.45, ""maxSteerDeg"": 25}}
        }";

        [Test]
        public void ValidConfig_PreservesZeroCoefficientsAndSignedPositions()
        {
            var aircraft = AircraftConfigLoader.Parse(Valid);
            Assert.AreEqual(0, aircraft.Aero.CL0);
            Assert.AreEqual(0, aircraft.Aero.Cm0);
            Assert.AreEqual(0, aircraft.Aero.CYda);
            Assert.AreEqual(0, aircraft.Aero.Cldr);
            Assert.AreEqual(12, aircraft.Mass.MassKg);
            Assert.AreEqual(-1, aircraft.Gear.Wheels[0].Position.X);
            Assert.AreEqual(20, aircraft.MaxDeflectionDeg.Aileron);
        }

        [TestCase(@"""mass_kg"": 12", @"""mass_kg"": NaN", "mass.mass_kg")]
        [TestCase(@"""mass_kg"": 12", @"""mass_kg"": -1", "mass.mass_kg")]
        [TestCase(@"""mass_kg"": 12", @"""mass_kg"": 0", "mass.mass_kg")]
        [TestCase(@"""mass_kg"": 12", @"""mass_kg"": ""12""", "mass.mass_kg")]
        [TestCase(@"""mass_kg"": 12", @"""mass_kg"": true", "mass.mass_kg")]
        [TestCase(@"""mass_kg"": 12", @"""mass_kg"": null", "mass.mass_kg")]
        [TestCase(@"""mass_kg"": 12,", "", "mass.mass_kg")]
        [TestCase(@"""mass"":", @"""unusedMass"":", "mass")]
        [TestCase(@"""inertia_kgm2"":", @"""unusedInertia"":", "mass.inertia_kgm2")]
        [TestCase(@"""Ixx"": 2", @"""Ixx"": 0", "mass.inertia_kgm2")]
        [TestCase(@"""Iyy"": 3", @"""Iyy"": -3", "mass.inertia_kgm2")]
        [TestCase(@"""Ixy"": 0", @"""Ixy"": 3", "mass.inertia_kgm2")]
        [TestCase(@"""Ixz"": 0", @"""Ixz"": 3", "mass.inertia_kgm2")]
        [TestCase(@"""Iyz"": 0", @"""Iyz"": 4", "mass.inertia_kgm2")]
        [TestCase(@"""Ixy"": 0", @"""Ixy"": 1e999", "mass.inertia_kgm2")]
        [TestCase(@"""Sref_m2"": 3.1", @"""Sref_m2"": NaN", "reference.Sref_m2")]
        [TestCase(@"""bref_m"": 4", @"""bref_m"": -4", "reference.bref_m")]
        [TestCase(@"""cref_m"": 0.775", @"""cref_m"": 0", "reference.cref_m")]
        [TestCase(@"""Sref_m2"": 3.1,", "", "reference.Sref_m2")]
        [TestCase(@"""cg_m"": [1, 0, -0.1]", @"""cg_m"": null", "reference.cg_m")]
        [TestCase(@"""cg_m"": [1, 0, -0.1]", @"""cg_m"": [0, 0]", "reference.cg_m")]
        [TestCase(@"""cg_m"": [1, 0, -0.1]", @"""cg_m"": [0, ""0"", 0]", "reference.cg_m")]
        [TestCase(@"""aero"":", @"""unusedAero"":", "aero")]
        [TestCase(@"""CL0"": 0,", "", "aero.CL0")]
        [TestCase(@"""CL0"": 0", @"""CL0"": null", "aero.CL0")]
        [TestCase(@"""CLalpha"": 4.4", @"""CLalpha"": 1e999", "aero.CLalpha")]
        [TestCase(@"""CD0"": 0.03", @"""CD0"": -0.03", "aero.CD0")]
        [TestCase(@"""k_induced"": 0.06", @"""k_induced"": -0.06", "aero.k_induced")]
        [TestCase(@"""Cmde"": -0.5", @"""Cmde"": Infinity", "elevator.Cmde")]
        [TestCase(@"""Cmde"": -0.5", @"""Cmde"": 0", "elevator.Cmde")]
        [TestCase(@"""Clda"": -0.1", @"""Clda"": 0", "aileron.Clda")]
        [TestCase(@"""Cndr"": 0.05", @"""Cndr"": 0", "rudder.Cndr")]
        [TestCase(@"""CYda"": 0", @"""CYda"": ""invalid""", "aileron.CYda")]
        [TestCase(@"""controls"":", @"""unusedControls"":", "aero.controls")]
        [TestCase(@"""maxDeflectionDeg"": 25", @"""maxDeflectionDeg"": -1", "maxDeflectionDeg")]
        [TestCase(@"""maxDeflectionDeg"": 25", @"""maxDeflectionDeg"": NaN", "maxDeflectionDeg")]
        [TestCase(@", ""maxDeflectionDeg"": 25", "", "maxDeflectionDeg")]
        [TestCase(@"""engine"":", @"""unusedEngine"":", "engine")]
        [TestCase(@"""maxThrust_N"": 40", @"""maxThrust_N"": -1", "engine.maxThrust_N")]
        [TestCase(@"""maxThrust_N"": 40", @"""maxThrust_N"": NaN", "engine.maxThrust_N")]
        [TestCase(@"""maxThrust_N"": 40", @"""unusedThrust"": 40", "engine.maxThrust_N")]
        [TestCase(@"""wheels"":", @"""unusedWheels"":", "gear.wheels")]
        [TestCase(@"""position_m"": [-1, 0, 0.35]", @"""position_m"": null", "position_m")]
        [TestCase(@"""position_m"": [-1, 0, 0.35]", @"""position_m"": []", "position_m")]
        [TestCase(@"""position_m"": [-1, 0, 0.35]", @"""position_m"": [0, 0, NaN]", "position_m")]
        [TestCase(@"""position_m"": [-1, 0, 0.35]", @"""position_m"": [0, 0, true]", "position_m")]
        [TestCase(@"""kN_per_m"": 900", @"""kN_per_m"": -1", "kN_per_m")]
        [TestCase(@"""cNs_per_m"": 80", @"""cNs_per_m"": -1", "cNs_per_m")]
        [TestCase(@"""mu_lateral"": 0.7", @"""mu_lateral"": -1", "mu_lateral")]
        [TestCase(@"""mu_rolling"": 0.02", @"""mu_rolling"": NaN", "mu_rolling")]
        [TestCase(@"""maxSteerDeg"": 25", @"""maxSteerDeg"": -25", "maxSteerDeg")]
        [TestCase(@"""steerable"": true", @"""steerable"": ""true""", "steerable")]
        public void InvalidPhysicalField_IsRejectedBeforePublishingDesign(
            string original, string replacement, string field)
        {
            AircraftConfigLoader.Parse(Valid);
            var lastDesign = AircraftConfigLoader.LastDesign;
            string invalid = Valid.Replace(original, replacement);
            Assert.AreNotEqual(Valid, invalid, "Test mutation must change the fixture");
            var error = Assert.Throws<InvalidOperationException>(
                () => AircraftConfigLoader.Parse(invalid));
            StringAssert.Contains(field, error.Message);
            Assert.AreSame(lastDesign, AircraftConfigLoader.LastDesign);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{")]
        public void InvalidRoot_ThrowsConfigError(string json)
        {
            Assert.Throws<InvalidOperationException>(() => AircraftConfigLoader.Parse(json));
        }

        [TestCase("mass_kg", "-1")]
        [TestCase("mass_kg", "NaN")]
        [TestCase("mass_kg", @"""12""")]
        [TestCase("mass_kg", "null")]
        [TestCase(@"mass_\u006bg", "-1")]
        public void DuplicateMassProperty_RejectsValidFirstAndInvalidLast(string key, string value)
        {
            AircraftConfigLoader.Parse(Valid);
            var lastDesign = AircraftConfigLoader.LastDesign;
            string invalid = Valid.Replace(@"""mass_kg"": 12",
                @"""mass_kg"": 12, """ + key + @""": " + value);
            var error = Assert.Throws<InvalidOperationException>(
                () => AircraftConfigLoader.Parse(invalid));
            StringAssert.Contains("mass.mass_kg", error.Message);
            StringAssert.Contains("duplicate", error.Message);
            Assert.AreSame(lastDesign, AircraftConfigLoader.LastDesign);
        }

        [Test]
        public void NullWheel_IsRejectedWithConfigError()
        {
            int start = Valid.IndexOf(@"{""name"": ""nose""", StringComparison.Ordinal);
            int end = Valid.IndexOf("}", start, StringComparison.Ordinal);
            string invalid = Valid.Substring(0, start) + "null" + Valid.Substring(end + 1);
            var error = Assert.Throws<InvalidOperationException>(
                () => AircraftConfigLoader.Parse(invalid));
            StringAssert.Contains("gear.wheels[0]", error.Message);
        }

        [Test]
        public void MissingGear_IsAllowed()
        {
            var aircraft = AircraftConfigLoader.Parse(Valid.Replace(@"""gear"":", @"""unusedGear"":"));
            Assert.IsNull(aircraft.Gear);
        }

        [Test]
        public void ZeroThrustDragAndDamping_AreAllowed()
        {
            var aircraft = AircraftConfigLoader.Parse(Valid
                .Replace(@"""maxThrust_N"": 40", @"""maxThrust_N"": 0")
                .Replace(@"""CD0"": 0.03", @"""CD0"": 0")
                .Replace(@"""k_induced"": 0.06", @"""k_induced"": 0")
                .Replace(@"""cNs_per_m"": 80", @"""cNs_per_m"": 0")
                .Replace(@"""mu_rolling"": 0.02", @"""mu_rolling"": 0"));
            Assert.AreEqual(0, aircraft.Engine.MaxThrustN);
            Assert.AreEqual(0, aircraft.Gear.Wheels[0].DamperNs);
        }

        [Test]
        public void PositiveDefiniteProducts_KeepOpenVspAxisConversion()
        {
            var aircraft = AircraftConfigLoader.Parse(Valid
                .Replace(@"""Ixy"": 0", @"""Ixy"": 0.1")
                .Replace(@"""Ixz"": 0", @"""Ixz"": -0.2")
                .Replace(@"""Iyz"": 0", @"""Iyz"": 0.3"));
            Assert.AreEqual(-0.1, aircraft.Mass.Ixy);
            Assert.AreEqual(-0.2, aircraft.Mass.Ixz);
            Assert.AreEqual(-0.3, aircraft.Mass.Iyz);
        }

        [Test]
        public void PositiveDiagonalAndMinors_ButNegativeDeterminant_IsRejected()
        {
            string invalid = Valid
                .Replace(@"""Ixx"": 2", @"""Ixx"": 1")
                .Replace(@"""Iyy"": 3", @"""Iyy"": 1")
                .Replace(@"""Izz"": 4", @"""Izz"": 1")
                .Replace(@"""Ixy"": 0", @"""Ixy"": 0.6")
                .Replace(@"""Ixz"": 0", @"""Ixz"": 0.6")
                .Replace(@"""Iyz"": 0", @"""Iyz"": 0.6");
            var error = Assert.Throws<InvalidOperationException>(
                () => AircraftConfigLoader.Parse(invalid));
            StringAssert.Contains("positive definite", error.Message);
        }
    }
}
