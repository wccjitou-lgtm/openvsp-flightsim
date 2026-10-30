#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
generate_airfoil_library.py — 生成常用翼型库 (Selig .dat) 到 Data/Workshop/airfoils/。

只收录坐标可精确确定的翼型:
  - NACA 四位数 (对称/有弯度): 解析公式, 封闭后缘 (t 系数 -0.1036)
  - NACA 五位数 230 系列: 解析中弧线 (m=0.2025, k1=15.957)
不写凭记忆捏造的坐标 — 错误坐标会直接污染气动计算。
OpenVSP 随附的其他翼型由用户从本机安装读取，不通过本脚本复制。
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "Data" / "Workshop" / "airfoils"


def naca4_mean(x: float, m: float, p: float) -> tuple[float, float]:
    """四位数中弧线: 返回 (yc, dycdx)。"""
    if x < p:
        return (m / p**2) * (2 * p * x - x * x), (2 * m / p**2) * (p - x)
    return (m / (1 - p)**2) * ((1 - 2 * p) + 2 * p * x - x * x), \
           (2 * m / (1 - p)**2) * (p - x)


def naca5_mean(x: float) -> tuple[float, float]:
    """NACA 230 系列中弧线 (设计升力系数 0.3, 最大弯度位置 0.15)。"""
    m, k1 = 0.2025, 15.957
    if x < m:
        return (k1 / 6) * (x**3 - 3 * m * x * x + m * m * (3 - m) * x), \
               (k1 / 6) * (3 * x * x - 6 * m * x + m * m * (3 - m))
    return (k1 * m**3 / 6) * (1 - x), -k1 * m**3 / 6


def thickness(x: float, t: float) -> float:
    """NACA 四位数厚度分布 (封闭后缘)。"""
    return (t / 0.2) * (0.2969 * math.sqrt(x) - 0.1260 * x - 0.3516 * x * x
                        + 0.2843 * x**3 - 0.1036 * x**4)


def cosine_x(n: int) -> list[float]:
    return [0.5 * (1 - math.cos(math.pi * i / n)) for i in range(n + 1)]


def gen_points(mean_fn, t: float, n: int = 80) -> list[tuple[float, float]]:
    """Selig 点序: TE 上表面 → LE → TE 下表面。"""
    xs = cosine_x(n)
    upper, lower = [], []
    for x in xs:
        yc, dy = mean_fn(x)
        yt = thickness(x, t)
        th = math.atan(dy)
        upper.append((x - yt * math.sin(th), yc + yt * math.cos(th)))
        lower.append((x + yt * math.sin(th), yc - yt * math.cos(th)))
    return list(reversed(upper)) + lower[1:]


def write_dat(name: str, pts: list[tuple[float, float]]) -> Path:
    p = OUT / f"{name}.dat"
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(name + "\n")
        for x, y in pts:
            f.write(f"  {x:9.6f}  {y:9.6f}\n")
    return p


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    written = []

    # ---- NACA 对称 ----
    for t in (6, 9, 10, 12, 15, 18, 21):
        name = f"NACA 00{t:02d}"
        written.append(write_dat(name, gen_points(lambda x: (0.0, 0.0), t / 100)))

    # ---- NACA 有弯度 (四位数) ----
    for m, p, t in ((2, 4, 8), (2, 4, 10), (2, 4, 12), (2, 4, 15),
                    (4, 4, 12), (4, 4, 15), (4, 4, 21), (6, 4, 9),
                    (6, 4, 12), (4, 6, 12)):
        name = f"NACA {m}{p}{t:02d}"
        mm, pp, tt = m / 100, p / 10, t / 100
        written.append(write_dat(name, gen_points(lambda x: naca4_mean(x, mm, pp), tt)))

    # ---- NACA 五位数 230 系列 ----
    for t in (12, 15):
        name = f"NACA 230{t}"
        written.append(write_dat(name, gen_points(naca5_mean, t / 100)))

    print(f"已写入 {len(written)} 个翼型到 {OUT}")
    for p in written:
        n = sum(1 for ln in p.read_text(encoding="utf-8").splitlines()[1:]
                if len(ln.split()) >= 2)
        assert n >= 10, f"{p.name} 点数不足"
        print(f"  {p.name}: {n} 点")
    return 0


if __name__ == "__main__":
    sys.exit(main())
