#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
terrain_alpine.py — 4 km 高山雪谷高度图生成器 (多场景 C1)

布局 (世界米, 中心 = 原点, 与 Unity/NED 一致; 跑道沿 +north, 中线 east=0, 朝北):
  * 冰川 U 型谷沿南北贯穿全图, 谷底半宽 >= 390 m (宽度 >= 780 m, 任务书下限 600 m), 中线在机场一带为 east≈45, 远离机场处缓慢蛇行;
  * 谷壁从谷底边缘 (离跑道中线 >= 285 m) 才开始起, 先圆润后陡 (smoothstep), 向两侧升到台地以上 ~600 m,
    脊状噪声 (域扭曲) 叠加出尖脊与山顶, 最高约台地以上 850~900 m; 热侵蚀松弛过陡处;
  * 谷底沿走廊方向缓升: 台地以南 0.9°、以北 1.0° (均小于 1.5°/2.0° 的进近/起飞限制面), 零星冰碛丘 (只在离中线 > 260 m 处),
    一条小溪沿谷刻在地形上 (只是地形凹槽, 不做水);
  * 机场台地高度 PLATFORM_H (RAW 基准); 台地 MSL 标高 2000 m 只记录在 meta (platform_msl_m), 供 HUD 与大气偏置用;
  * 图边缘一圈渐变到常数高度 EDGE_H (与图外远景地面同高), 山体在边缘落成平原。

输出 (同海岛风格): alpine.raw / alpine.meta.json / alpine.slope.npy / alpine.relalt.npy (相对台地高度 m, 供雪线/树线分层) /
alpine.preview.png。全部确定性: 同种子逐字节一致。

用法 (项目 Python 3.11):
  python Tools/terrain_alpine.py Data/Terrain/alpine/alpine --size 2049 --world 4000 --seed 31
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

import terrain_airport as ta
from terrain_forest import _smoothstep, fbm, slope_deg, thermal_erosion

PLATFORM_H = 100.0               # 机场台地在 RAW 高度基准里的高度 (m); Unity 里下移到 y=0
PLATFORM_MSL_M = 2000.0          # 台地真实海拔 (m): 只记录, 供 HUD 与 AtmosphereModel.AltitudeOffsetM
PLATFORM = ta.Platform(east=(-80.0, 170.0), north=(-110.0, 910.0), height=PLATFORM_H, feather=140.0)
CORRIDOR = ta.Corridor(half_width=100.0, feather=150.0, approach_deg=1.5, departure_deg=2.0)
EDGE_H = PLATFORM_H + 30.0       # 图边缘常数高度 (≈ 谷底在图端的高度)
EDGE_FADE = 700.0

VALLEY_CENTER_E = 45.0           # 机场一带谷中线 (台地中心)
FLOOR_HALF_W = 430.0             # 谷底半宽基值 (蛇行/宽窄变化后下限 390 m = 宽度 780 m; 任务书下限 600 m, 多留余量给谷中盘旋)
FLOOR_HALF_W_VAR = 40.0
FLOOR_SOUTH_DEG = 0.9            # 谷底沿台地以南缓升角 (< 进近限制面 1.5°)
FLOOR_NORTH_DEG = 1.0            # 台地以北 (< 起飞限制面 2.0°)
WALL_BASE_M = 560.0              # 谷壁基础抬升 (台地以上)
WALL_RIDGE_M = 250.0             # 脊状噪声幅度
WALL_RUN_M = 850.0              # 谷壁水平跨度 (smoothstep 从谷底边缘到基础抬升)
CREEK_DEPTH_M = 2.2
CREEK_HALF_W = 5.0


def _sm(x, lo, hi):
    return _smoothstep((x - lo) / (hi - lo))


def floor_profile(north):
    """谷底沿北向的高度 (台地内为常数 PLATFORM_H; 台地外缓升, 角度小于限制面)。"""
    n0, n1 = PLATFORM.north
    south = np.maximum(n0 - north, 0.0) * np.tan(np.radians(FLOOR_SOUTH_DEG))
    north_ = np.maximum(north - n1, 0.0) * np.tan(np.radians(FLOOR_NORTH_DEG))
    return PLATFORM_H + south + north_


def valley_geometry(north):
    """谷中线 east 与谷底半宽, 随 north 变化: 机场一带笔直, 远处蛇行并忽宽忽窄。"""
    free = _sm(np.abs(north - 400.0), 800.0, 1500.0)
    xc = VALLEY_CENTER_E + free * (110.0 * np.sin(north / 650.0 + 0.7) + 60.0 * np.sin(north / 260.0 + 2.1))
    w0 = FLOOR_HALF_W + free * FLOOR_HALF_W_VAR * np.sin(north / 510.0 + 1.3)
    return xc.astype(np.float32), w0.astype(np.float32)


def creek_east(north, xc):
    """小溪中线 east: 谷中线东侧约 230 m, 小幅蛇行。"""
    return (xc + 230.0 + 28.0 * np.sin(north / 210.0 + 0.4) + 12.0 * np.sin(north / 77.0)).astype(np.float32)


def generate(size: int, world: float, seed: int, erosion_iters: int = 25):
    rng = np.random.default_rng(seed)
    east, north, cell = ta.world_axes(size, world)

    # 域扭曲: 谷壁与山脊不是直线
    warp_e = ((fbm(size, 3, 4, rng) - 0.5) * 2.0 * 140.0).astype(np.float32)
    warp_n = ((fbm(size, 3, 4, rng) - 0.5) * 2.0 * 140.0).astype(np.float32)
    xc, w0 = valley_geometry(north)                         # (N,1)
    xc_w, w0_w = valley_geometry(north + warp_n)            # 扭曲后的几何, 只用于谷壁
    u = np.maximum(np.abs(east + warp_e - xc_w) - w0_w, 0.0)   # 到谷底边缘的水平距离 (谷底内 = 0)
    floor_w = (1.0 - _sm(u, 0.0, 160.0)).astype(np.float32)    # 谷底权重 (地表细节只加在谷底)

    # ---- 谷壁 + 山体 ----
    wall = WALL_BASE_M * _smoothstep(u / WALL_RUN_M)
    # 脊线噪声只取低频 4 个倍频: 取绝对值的折痕会放大高频, 高频倍频多了会在山体上显出方格马赛克
    ridge = 1.0 - np.abs(2.0 * fbm(size, 4, 5, rng, gain=0.5) - 1.0)
    ridge = ridge ** 2.2
    ridge2 = (1.0 - np.abs(2.0 * fbm(size, 12, 3, rng, gain=0.5) - 1.0)) ** 2.0   # 次级山脊: 谷壁上的沟梁
    detail = fbm(size, 6, 4, rng, gain=0.45) - 0.5
    amp = _sm(u, 60.0, 560.0)
    peaks = amp * (WALL_RIDGE_M * ridge + 75.0 * ridge2 * _sm(u, 150.0, 700.0) + 50.0 * detail)
    # 山顶锐化: 把高处的脊再往上拉一点, 谷底附近不受影响
    sharp = _sm(u, 500.0, 1200.0) * 40.0 * ridge ** 2.0
    # 山体向图边缘逐渐低伏: 否则山脊正好落在边缘渐变带上, 被切成一道笔直的悬崖
    taper = (1.0 - 0.6 * _sm(np.abs(east), 1000.0, 1900.0)) * (1.0 - 0.45 * _sm(np.abs(north), 1100.0, 1900.0))
    mountains = (wall + peaks + sharp) * taper

    # ---- 谷底 ----
    rough = (fbm(size, 24, 4, rng) - 0.5) * 3.0 * floor_w
    mounds = np.zeros((size, size), np.float32)
    mrng = np.random.default_rng(seed + 1)
    placed = 0
    while placed < 16:
        mn = mrng.uniform(-1800.0, 1800.0)
        sign = 1.0 if mrng.random() < 0.7 else -1.0
        me = float(xc[int(np.argmin(np.abs(north[:, 0] - mn)))][0]) + sign * mrng.uniform(280.0, 330.0) * (1 if sign > 0 else 1)
        if abs(me) < 270.0:        # 冰碛丘不进走廊/机场两侧
            continue
        r = mrng.uniform(25.0, 70.0)
        ht = mrng.uniform(3.0, 9.0)
        mounds += (ht * np.exp(-((east - me) ** 2 + (north - mn) ** 2) / (2.0 * (r * 0.55) ** 2))).astype(np.float32)
        placed += 1

    ce = creek_east(north, xc)
    cd = np.abs(east - ce)
    creek = CREEK_DEPTH_M * (1.0 - _sm(cd, CREEK_HALF_W * 0.4, CREEK_HALF_W * 2.6)) * floor_w

    h = (floor_profile(north) + mountains + rough + mounds * floor_w - creek).astype(np.float32)

    h = thermal_erosion(h, erosion_iters, talus=float(np.tan(np.radians(48.0)) * cell))
    h = ta.apply_edge_fade(h, east, north, world, EDGE_H, EDGE_FADE)
    h = ta.apply_corridor(h, east, north, PLATFORM, CORRIDOR)
    h = ta.apply_platform(h, east, north, PLATFORM)
    h = np.maximum(h, 0.5).astype(np.float32)
    return h, u


def write_outputs(base: Path, h, u, size, world, seed):
    base.parent.mkdir(parents=True, exist_ok=True)
    cell = world / (size - 1)
    scale = ta.write_raw(base.with_suffix(".raw"), h)
    meta = ta.make_meta(base.name, size, world, scale, seed, PLATFORM, CORRIDOR, EDGE_H, biome="alpine",
                        extra={"far_ground_rgb": [0.80, 0.83, 0.87],
                               "platform_msl_m": PLATFORM_MSL_M,
                               "valley": {"center_east_m": VALLEY_CENTER_E, "floor_half_width_m": FLOOR_HALF_W,
                                          "floor_half_width_min_m": FLOOR_HALF_W - FLOOR_HALF_W_VAR},
                               "creek": {"depth_m": CREEK_DEPTH_M, "half_width_m": CREEK_HALF_W}})
    meta["terrain"] = "alpine_4km"
    base.with_suffix(".meta.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
    np.save(str(base) + ".slope.npy", slope_deg(h, cell))
    np.save(str(base) + ".relalt.npy", (h - PLATFORM_H).astype(np.float32))

    try:
        from PIL import Image
        rel = h - PLATFORM_H
        t = np.clip(rel / 900.0, 0, 1)
        img = np.zeros(h.shape + (3,), np.float32)
        img[..., 0] = 0.40 + 0.55 * t
        img[..., 1] = 0.45 + 0.50 * t
        img[..., 2] = 0.42 + 0.55 * t
        gy, gx = np.gradient(h, cell)
        # 山体阴影: 光从西北照来 (gx>0 = 向东升 = 朝西南坡亮)
        shade = np.clip(0.62 + 0.7 * (-gx * 0.7 + gy * 0.7) / np.sqrt(1.0 + gx * gx + gy * gy), 0.12, 1.0)
        img *= shade[..., None]
        Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).resize((1024, 1024)).save(str(base) + ".preview.png")
    except Exception as ex:
        print("预览生成跳过:", ex)
    return meta


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out_base", type=Path)
    ap.add_argument("--size", type=int, default=2049)
    ap.add_argument("--world", type=float, default=4000.0)
    ap.add_argument("--seed", type=int, default=31)
    ap.add_argument("--erosion", type=int, default=25)
    a = ap.parse_args(argv)
    h, u = generate(a.size, a.world, a.seed, a.erosion)
    meta = write_outputs(a.out_base, h, u, a.size, a.world, a.seed)
    print(f"输出 {a.out_base.with_suffix('.raw')} {a.size}x{a.size} 高度 {h.min():.1f}..{h.max():.1f} m "
          f"(台地 {PLATFORM_H} m 以上最高 {h.max() - PLATFORM_H:.0f} m), height_scale={meta['height_scale_m']}")


if __name__ == "__main__":
    main()
