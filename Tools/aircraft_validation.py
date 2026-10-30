"""Validate the physical AircraftConfig schema without coercion or mutation."""

import math


_AERO_FIELDS = (
    "CL0", "CLalpha", "CLq", "CD0", "k_induced", "Cm0", "Cmalpha", "Cmq",
    "CYbeta", "Clbeta", "Cnbeta", "CYp", "Clp", "Cnp", "CYr", "Clr", "Cnr",
)
_CONTROL_FIELDS = {
    "elevator": ("CLde", "Cmde"),
    "aileron": ("CYda", "Clda", "Cnda"),
    "rudder": ("CYdr", "Cldr", "Cndr"),
}
_INERTIA_FIELDS = ("Ixx", "Iyy", "Izz", "Ixy", "Ixz", "Iyz")


def _positive_definite(values):
    # Products of inertia enter the tensor with a minus sign. OpenVSP's
    # Ixy/Iyz axis conversion is an orthogonal similarity, so SPD is unchanged.
    scale = max(abs(value) for value in values)
    if scale == 0:
        return False
    xx, yy, zz, xy, xz, yz = (value / scale for value in values)
    if xx <= 0 or yy <= 0 or zz <= 0:
        return False
    l00 = math.sqrt(xx)
    l10, l20 = -xy / l00, -xz / l00
    pivot1 = yy - l10 * l10
    if not math.isfinite(pivot1) or pivot1 <= 0:
        return False
    l21 = (-yz - l10 * l20) / math.sqrt(pivot1)
    pivot2 = zz - l20 * l20 - l21 * l21
    return math.isfinite(pivot2) and pivot2 > 0


def validate_aircraft_config(cfg: dict) -> list[str]:
    """Return field-specific errors for malformed or unsafe physical inputs.

    Metadata and unknown fields are ignored. cg_m and gear are optional;
    supplied gear supports the existing wheel/friction defaults. The fields
    that drive mass, aerodynamics, control limits and thrust are required.
    JSON syntax and file I/O errors belong to the caller.
    """
    problems = []

    def obj(value, path):
        if not isinstance(value, dict):
            problems.append(f"{path}: must be an object")
            return None
        return value

    def number(value, path, positive=False, nonnegative=False, nonzero=False):
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            problems.append(f"{path}: must be a finite number")
            return None
        try:
            value = float(value)
        except (OverflowError, ValueError):
            problems.append(f"{path}: must be a finite number")
            return None
        if not math.isfinite(value):
            problems.append(f"{path}: must be a finite number")
            return None
        if positive and value <= 0:
            problems.append(f"{path}: must be positive")
        elif nonnegative and value < 0:
            problems.append(f"{path}: must be nonnegative")
        elif nonzero and value == 0:
            problems.append(f"{path}: primary control effectiveness must be nonzero")
        return value

    def vector(value, path):
        if not isinstance(value, list) or len(value) != 3:
            problems.append(f"{path}: must be an array of three finite numbers")
            return
        for index, component in enumerate(value):
            number(component, f"{path}[{index}]")

    cfg = obj(cfg, "config")
    if cfg is None:
        return problems

    reference = obj(cfg.get("reference"), "reference")
    if reference is not None:
        for key in ("Sref_m2", "bref_m", "cref_m"):
            number(reference.get(key), f"reference.{key}", positive=True)
        if "cg_m" in reference:
            vector(reference["cg_m"], "reference.cg_m")

    mass = obj(cfg.get("mass"), "mass")
    if mass is not None:
        number(mass.get("mass_kg"), "mass.mass_kg", positive=True)
        inertia = obj(mass.get("inertia_kgm2"), "mass.inertia_kgm2")
        if inertia is not None:
            values = [number(inertia.get(key), f"mass.inertia_kgm2.{key}")
                      for key in _INERTIA_FIELDS]
            if all(value is not None for value in values) and not _positive_definite(values):
                problems.append("mass.inertia_kgm2: tensor must be positive definite")

    aero = obj(cfg.get("aero"), "aero")
    if aero is not None:
        for key in _AERO_FIELDS:
            number(aero.get(key), f"aero.{key}", nonnegative=key in ("CD0", "k_induced"))
        controls = obj(aero.get("controls"), "aero.controls")
        if controls is not None:
            for surface, fields in _CONTROL_FIELDS.items():
                path = f"aero.controls.{surface}"
                control = obj(controls.get(surface), path)
                if control is None:
                    continue
                for key in fields:
                    number(control.get(key), f"{path}.{key}",
                           nonzero=key in ("Cmde", "Clda", "Cndr"))
                number(control.get("maxDeflectionDeg"), f"{path}.maxDeflectionDeg",
                       positive=True)

    engine = obj(cfg.get("engine"), "engine")
    if engine is not None:
        number(engine.get("maxThrust_N"), "engine.maxThrust_N", nonnegative=True)

    if "gear" not in cfg:
        return problems
    gear = obj(cfg["gear"], "gear")
    if gear is None:
        return problems
    wheels = gear.get("wheels")
    if not isinstance(wheels, list):
        problems.append("gear.wheels: must be an array")
    else:
        for index, value in enumerate(wheels):
            path = f"gear.wheels[{index}]"
            wheel = obj(value, path)
            if wheel is None:
                continue
            vector(wheel.get("position_m"), f"{path}.position_m")
            if "kN_per_m" in wheel:
                number(wheel["kN_per_m"], f"{path}.kN_per_m", positive=True)
            if "cNs_per_m" in wheel:
                number(wheel["cNs_per_m"], f"{path}.cNs_per_m", nonnegative=True)
            for key in ("steerable", "brake"):
                if key in wheel and not isinstance(wheel[key], bool):
                    problems.append(f"{path}.{key}: must be a boolean")
    if "friction" in gear:
        friction = obj(gear["friction"], "gear.friction")
        if friction is not None:
            for key in ("mu_lateral", "mu_rolling", "mu_brake", "maxSteerDeg"):
                if key in friction:
                    number(friction[key], f"gear.friction.{key}", nonnegative=True)

    return problems
