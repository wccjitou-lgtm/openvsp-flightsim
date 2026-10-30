#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
terrain_island.py — 4 km 海岛海岸高度图生成器 (多场景 B1)

布局 (世界米, 中心 = 原点, 与 Unity/NED 一致; 跑道沿 +north, 中线 east=0, 朝北):
  * 海在东侧: 东海岸线大致南北向, 离跑道中线约 420 m (350~460 m), 与跑道平行;
  * 岛四周都是海 (西 -1450 / 北 +1650 / 南 -1450 附近为岸线), 岸线按解析曲线 + 低频噪声起伏;
  * 东海岸南段 (跑道一带) 为沙滩缓坡, 东北段 (north > ~950) 为 30~40 m 海蚀崖; 岸线以西为丘陵 (最高约海面上 100 m);
  * 机场台地高出海面 9 m, 进近 (南) / 起飞 (北) 走廊沿用森林的 1.5° / 2.0° 限制面 (只压陆地, 不影响海域)。
RAW 高度必须非负: 海面取 SEA_H, 近岸以外海底 = SEA_H - SEABED_DEPTH, 图边缘渐变到该海底高度。

输出 (同森林风格): island.raw / island.meta.json / island.coast.json (东海岸折线 + 灯塔/码头位置) /
island.slope.npy / island.coastdist.npy (到岸线的带符号距离 m, 陆正海负, 为侵蚀前的近似值) / island.preview.png。
全部确定性: 同种子逐字节一致。

用法 (项目 Python 3.11):
  python Tools/terrain_island.py Data/Terrain/island/island --size 2049 --world 4000 --seed 21
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

import terrain_airport as ta
from terrain_forest import _smoothstep, fbm, slope_deg, thermal_erosion

SEA_H = 40.0                     # 海面在 RAW 高度基准里的高度 (m)
SEABED_DEPTH = 35.0              # 近岸以外海深
SEABED_H = SEA_H - SEABED_DEPTH  # 5.0 = 图边缘常数高度
PLAT_ABOVE_SEA = 9.0
PLATFORM = ta.Platform(east=(-80.0, 170.0), north=(-110.0, 910.0), height=SEA_H + PLAT_ABOVE_SEA, feather=140.0)
CORRIDOR = ta.Corridor(half_width=100.0, feather=150.0, approach_deg=1.5, departure_deg=2.0)
EDGE_FADE = 500.0

COAST_EAST_M = 420.0             # 东海岸线 (跑道中线以东)
CLIFF_TOP_ABOVE_SEA = 36.0
HILL_MAX = 100.0                 # 丘陵相对海面的上限量级 (实际受噪声约束)


def _sm(x, lo, hi):
    return _smoothstep((x - lo) / (hi - lo))


def coast_distance(east, north):
    """到岸线的近似带符号距离 (陆 > 0): 四条解析岸线的软最小值。"""
    de = (COAST_EAST_M + 25.0 * np.sin(north / 380.0)) - east
    dw = east - (-1450.0 + 140.0 * np.sin(north / 700.0 + 1.0))
    dn = (1650.0 + 120.0 * np.sin(east / 500.0 + 2.0)) - north
    ds = north - (-1450.0 + 100.0 * np.sin(east / 600.0))
    stack = np.stack(np.broadcast_arrays(de, dw, dn, ds)).astype(np.float32)
    m = stack.min(axis=0)
    s = 220.0   # 软化半径: 拐角圆滑
    return (m - s * np.log(np.exp(-(stack - m) / s).sum(axis=0))).astype(np.float32)


def generate(size: int, world: float, seed: int, erosion_iters: int = 25):
    rng = np.random.default_rng(seed)
    east, north, cell = ta.world_axes(size, world)

    # 岸线抖动: 跑道一带的东岸只允许小幅 (保证离中线 350~460 m), 其余岸线放大成海湾/岬角, 免得岛形像方块
    free = np.maximum(np.maximum(_sm(north, 1000.0, 1400.0), _sm(-north, 300.0, 700.0)), _sm(-east, 200.0, 600.0))
    wob_amp = 35.0 + 215.0 * free
    d = coast_distance(east, north) + ((fbm(size, 3, 4, rng) - 0.5) * 2.0 * wob_amp).astype(np.float32)
    inl = np.maximum(d, 0.0)
    off = np.maximum(-d, 0.0)

    # 海蚀崖权重: 东北象限 (north > 950, east > -200) 的岸线为崖, 其余为沙滩
    cliff_w = (_sm(north, 950.0, 1250.0) * _sm(east, -200.0, 100.0)).astype(np.float32)

    # ---- 陆地 ----
    beach = 3.0 * _smoothstep(inl / 150.0)
    cliff = (6.0 + (CLIFF_TOP_ABOVE_SEA - 6.0) * (1.0 - _smoothstep(inl / 500.0))) * _smoothstep(inl / 30.0)
    ramp = beach * (1.0 - cliff_w) + cliff * cliff_w

    base = fbm(size, 3, 6, rng)
    ridge = 1.0 - np.abs(2.0 * fbm(size, 4, 5, rng) - 1.0)
    wx = ((fbm(size, 5, 3, rng) - 0.5) * 2.0 * 60).astype(np.int64)
    wy = ((fbm(size, 5, 3, rng) - 0.5) * 2.0 * 60).astype(np.int64)
    ii = np.clip(np.arange(size)[:, None] + wy, 0, size - 1)
    jj = np.clip(np.arange(size)[None, :] + wx, 0, size - 1)
    ridge = ridge[ii, jj]
    dist_air = np.hypot(east - 45.0, north - 400.0)
    airport_calm = 0.18 + 0.82 * _smoothstep((dist_air - 450.0) / 1100.0)   # 机场附近压低起伏
    hmask = _smoothstep((inl - 120.0) / 500.0) * airport_calm
    hills = hmask * (5.0 + 85.0 * ridge ** 2 + 50.0 * base)
    land = ramp + hills

    # ---- 海底 ----
    reach = 600.0 * (1.0 - cliff_w) + 100.0 * cliff_w
    ripples = (fbm(size, 4, 4, rng) - 0.5) * 6.0
    seabed = -SEABED_DEPTH * _smoothstep(off / reach) + ripples * _smoothstep(off / 200.0)

    rel = np.where(d >= 0.0, land, seabed)          # 相对海面的高度
    h = (SEA_H + rel).astype(np.float32)

    h = thermal_erosion(h, erosion_iters, talus=float(np.tan(np.radians(50.0)) * cell))
    h = ta.apply_edge_fade(h, east, north, world, SEABED_H, EDGE_FADE)
    h = ta.apply_corridor(h, east, north, PLATFORM, CORRIDOR)
    h = ta.apply_platform(h, east, north, PLATFORM)
    h = np.maximum(h, 0.5).astype(np.float32)
    return h, d


def east_coast_polyline(h: np.ndarray, size: int, world: float, step_m: float = 60.0):
    """东海岸 (从跑道向东第一次穿过海面) 折线 [(north, east)], 线性插值。"""
    east, north, cell = ta.world_axes(size, world)
    e_axis = east[0]
    start = int(np.searchsorted(e_axis, 200.0))
    pts = []
    for nr in np.arange(-1300.0, 1601.0, step_m):
        r = int(np.argmin(np.abs(north[:, 0] - nr)))
        row = h[r, start:]
        below = np.nonzero(row < SEA_H)[0]
        if below.size == 0 or below[0] == 0:
            continue
        k = start + int(below[0])
        h0, h1 = float(h[r, k - 1]), float(h[r, k])
        t = (h0 - SEA_H) / max(h0 - h1, 1e-6)
        pts.append((float(nr), float(e_axis[k - 1] + t * (e_axis[k] - e_axis[k - 1]))))
    return pts


def place_features(coast, h, size, world):
    """灯塔: 东北崖上 (north~1250 处海岸内侧 20 m); 码头: 跑道南侧沙滩 (north~-300) 垂直岸线向海伸出。"""
    def at(n):
        return min(coast, key=lambda p: abs(p[0] - n))
    ln, le = at(1250.0)
    pn, pe = at(-300.0)
    return {"lighthouse": {"north": ln, "east": le - 20.0},
            "pier": {"north": pn, "east": pe, "length_m": 70.0, "width_m": 4.0}}


def write_outputs(base: Path, h, d, size, world, seed):
    base.parent.mkdir(parents=True, exist_ok=True)
    cell = world / (size - 1)
    scale = ta.write_raw(base.with_suffix(".raw"), h)
    meta = ta.make_meta(base.name, size, world, scale, seed, PLATFORM, CORRIDOR, SEABED_H,
                        biome="island", sea_level_m=SEA_H,
                        extra={"far_ground_rgb": [0.62, 0.55, 0.40], "coast_file": base.name + ".coast.json"})
    meta["terrain"] = "island_4km"
    base.with_suffix(".meta.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")

    coast = east_coast_polyline(h, size, world)
    cj = {"sea_level_m": SEA_H, "east_coast": [{"north": round(n, 2), "east": round(e, 2)} for n, e in coast],
          "features": place_features(coast, h, size, world)}
    base.with_suffix(".coast.json").write_text(json.dumps(cj, ensure_ascii=False), encoding="utf-8")
    np.save(str(base) + ".slope.npy", slope_deg(h, cell))
    np.save(str(base) + ".coastdist.npy", d)

    try:
        from PIL import Image
        rel = h - SEA_H
        t = np.clip(rel / 110.0, 0, 1)
        img = np.zeros(h.shape + (3,), np.float32)
        land = rel >= 0
        img[land, 0] = 0.30 + 0.45 * t[land]
        img[land, 1] = 0.45 + 0.35 * t[land]
        img[land, 2] = 0.22 + 0.35 * t[land]
        dep = np.clip(-rel / SEABED_DEPTH, 0, 1)
        img[~land, 0] = 0.10 + 0.05 * (1 - dep[~land])
        img[~land, 1] = 0.35 + 0.25 * (1 - dep[~land])
        img[~land, 2] = 0.75 - 0.25 * dep[~land]
        gy, gx = np.gradient(h, cell)
        img *= (0.65 + 0.35 * np.clip(-gx * 3.0 + 0.5, 0, 1))[..., None]
        Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).resize((1024, 1024)).save(str(base) + ".preview.png")
    except Exception as ex:
        print("预览生成跳过:", ex)
    return meta, cj


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out_base", type=Path)
    ap.add_argument("--size", type=int, default=2049)
    ap.add_argument("--world", type=float, default=4000.0)
    ap.add_argument("--seed", type=int, default=21)
    ap.add_argument("--erosion", type=int, default=25)
    a = ap.parse_args(argv)
    h, d = generate(a.size, a.world, a.seed, a.erosion)
    meta, cj = write_outputs(a.out_base, h, d, a.size, a.world, a.seed)
    water = float((h < SEA_H).mean())
    print(f"输出 {a.out_base.with_suffix('.raw')} {a.size}x{a.size} 高度 {h.min():.1f}..{h.max():.1f} m, "
          f"海面 {SEA_H} m, 水域占比 {water:.0%}, height_scale={meta['height_scale_m']}")


if __name__ == "__main__":
    main()
