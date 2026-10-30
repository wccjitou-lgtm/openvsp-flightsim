#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
terrain_forest.py — 4 km 森林地貌高度图生成器 (森林场景重建 S2)

输出与 terrain_tools.py 同格式 (16 位大端 RAW, 行主序北->南, 列=东向增) + meta.json,
另输出: river.json (河流中线/宽度/水面高程/瀑布位置), slope.npy (坡度 deg),
riverdist.npy (到河中线的水平距离 m), preview.png (着色预览)。
全部确定性: 同种子同参数 -> 逐字节一致 (只用 numpy.random.default_rng + 纯 numpy 运算)。

坐标: world_size 正方形, 中心 = Unity/NED 原点。
  RAW 行 r: north = +half - r*cell;  列 c: east = -half + c*cell  (cell = world/(size-1))
地貌: 多尺度 fBm + 脊状噪声 + 整数位移域扭曲 (机场附近压低起伏) -> 热侵蚀 (numpy 向量化) ->
  刻蜿蜒河谷 (河床沿程单调下降, 中途一级瀑布台阶) -> 机场平整台地 (常数高度 + 羽化过渡, 最后写入)。
注意: 侵蚀只有热侵蚀 (滑坡式削坡), 没有水力侵蚀; 冲沟感来自脊状噪声与河谷刻蚀。

用法 (项目 Python 3.11, 需要 numpy):
  python Tools/terrain_forest.py Data/Terrain/forest --size 2049 --world 4000 --seed 11
"""
from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path

import numpy as np

# ---- 机场 (Unity x 东 / z 北 = 世界 east / north; 与 RunwayEnvironment 布局一致) ----
AIRPORT_H = 62.0                              # 台地高度 (m, 与全图同基准)
AIRPORT_EAST = (-80.0, 170.0)                 # 台地范围 (含跑道/滑行道/停机坪/机库 + 余量)
AIRPORT_NORTH = (-110.0, 910.0)               # 跑道 0..800 + 两端缓冲
AIRPORT_FEATHER = 140.0                       # 台地外羽化带宽度 (m)

# ---- 进近/起飞走廊净空面 (类似障碍物限制面): 跑道两端外侧地形不得高于从台地边缘起算的斜面 ----
CORRIDOR_HALF_W = 100.0                       # 走廊半宽 (跑道中线 east=0 两侧, m)
CORRIDOR_FEATHER = 150.0                      # 走廊侧向羽化 (m)
APPROACH_DEG = 1.5                            # 南端 (进近) 限制面坡度, 明显低于 3.5° 下滑道
DEPARTURE_DEG = 2.0                           # 北端 (起飞爬升) 限制面坡度

# ---- 图边缘: 外围一圈渐变到常数高度, 场景在图外铺同高度的远景地面, 看不到"世界边缘"悬崖 ----
EDGE_H = 72.0                                 # 边缘常数高度 (与全图同基准)
EDGE_FADE = 500.0                             # 渐变带宽度 (m)

# ---- 河流 ----
RIVER_CTRL = [(700, 2050), (880, 1450), (640, 850), (980, 250), (1240, -450),
              (900, -1050), (1290, -1700), (1120, -2050)]    # (east, north), 自北向南
BED_UP, BED_SLOPE_TOTAL = 48.0, 16.0          # 上游河床高 / 全程缓降量
FALL_DROP, FALL_LEN, FALL_FRAC = 22.0, 40.0, 0.45   # 瀑布落差 / 台阶长度 / 位置(占全长比例)
WATER_DEPTH = 1.2


def _smoothstep(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3.0 - 2.0 * x)


def lattice_noise(n: int, cells: int, rng) -> np.ndarray:
    """n×n 值噪声: (cells+1)² 随机格点 + smoothstep 双线性插值, 输出 [0,1)。"""
    g = rng.random((cells + 1, cells + 1)).astype(np.float32)
    t = np.linspace(0.0, cells, n, dtype=np.float32)
    i0 = np.minimum(t.astype(np.int64), cells - 1)
    f = _smoothstep(t - i0)
    a = g[i0][:, i0] * (1 - f)[None, :] + g[i0][:, i0 + 1] * f[None, :]
    b = g[i0 + 1][:, i0] * (1 - f)[None, :] + g[i0 + 1][:, i0 + 1] * f[None, :]
    return a * (1 - f)[:, None] + b * f[:, None]


def fbm(n: int, base_cells: int, octaves: int, rng, gain: float = 0.5) -> np.ndarray:
    out = np.zeros((n, n), np.float32)
    amp, tot = 1.0, 0.0
    for o in range(octaves):
        out += amp * lattice_noise(n, base_cells * (2 ** o), rng)
        tot += amp
        amp *= gain
    return out / tot


def thermal_erosion(h: np.ndarray, iters: int, talus: float, rate: float = 0.45) -> np.ndarray:
    """向量化热侵蚀: 与四邻高差超过 talus 的部分按 rate 比例搬给较低邻居。"""
    h = h.copy()
    for _ in range(iters):
        delta = np.zeros_like(h)
        for axis, shift in ((0, 1), (0, -1), (1, 1), (1, -1)):
            nb = np.roll(h, shift, axis=axis)
            diff = h - nb
            move = np.maximum(diff - talus, 0.0) * (rate * 0.25)
            delta -= move
            delta += np.roll(move, -shift, axis=axis)
        h += delta
    return h


# ============================================================ 河流

def build_river(world: float, seed: int, spacing: float = 12.0) -> dict:
    """Catmull-Rom 蜿蜒中线 (带确定性横向抖动) + 沿程河床/水面/宽度。"""
    rng = np.random.default_rng(seed + 101)
    pts = np.array(RIVER_CTRL, np.float64)
    pts[1:-1, 0] += rng.uniform(-60, 60, len(pts) - 2)
    p = np.vstack([2 * pts[0] - pts[1], pts, 2 * pts[-1] - pts[-2]])
    out = []
    for i in range(1, len(p) - 2):
        p0, p1, p2, p3 = p[i - 1], p[i], p[i + 1], p[i + 2]
        for t in np.linspace(0, 1, 60, endpoint=False):
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t
                              + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    out.append(pts[-1])
    c = np.array(out)
    seg = np.hypot(*np.diff(c, axis=0).T)
    s = np.concatenate([[0.0], np.cumsum(seg)])
    L = s[-1]
    ss = np.arange(0.0, L, spacing)
    east = np.interp(ss, s, c[:, 0])
    north = np.interp(ss, s, c[:, 1])
    s_fall = FALL_FRAC * L
    step = _smoothstep((ss - s_fall) / FALL_LEN)
    bed = BED_UP - BED_SLOPE_TOTAL * (ss / L) - FALL_DROP * step
    width = 14.0 + 10.0 * (ss / L)
    return {"s": ss, "east": east, "north": north, "bed": bed, "width": width,
            "water": bed + WATER_DEPTH, "length_m": float(L), "fall_s": float(s_fall)}


def river_distance(n: int, world: float, river: dict, chunk: int = 64):
    """每个格点到中线的水平最近距离 d 与最近点索引 k (分块暴力, float32)。"""
    half = world / 2.0
    cell = world / (n - 1)
    east_axis = (-half + np.arange(n) * cell).astype(np.float32)
    re = river["east"].astype(np.float32)[None, None, :]
    rn = river["north"].astype(np.float32)[None, None, :]
    d = np.empty((n, n), np.float32)
    k = np.empty((n, n), np.int32)
    for r0 in range(0, n, chunk):
        r1 = min(r0 + chunk, n)
        north = (half - np.arange(r0, r1) * cell).astype(np.float32)[:, None, None]
        d2 = (east_axis[None, :, None] - re) ** 2 + (north - rn) ** 2
        idx = d2.argmin(axis=2)
        k[r0:r1] = idx
        d[r0:r1] = np.sqrt(np.take_along_axis(d2, idx[:, :, None], axis=2)[:, :, 0])
    return d, k


# ============================================================ 主生成

def generate(size: int, world: float, seed: int, erosion_iters: int = 40):
    rng = np.random.default_rng(seed)
    half = world / 2.0
    cell = world / (size - 1)
    east = (-half + np.arange(size) * cell).astype(np.float32)[None, :]
    north = (half - np.arange(size) * cell).astype(np.float32)[:, None]

    # --- 基础起伏: fBm + 脊状噪声, 机场附近压低振幅 ---
    base = fbm(size, 3, 6, rng) - 0.5
    ridge = 1.0 - np.abs(2.0 * fbm(size, 4, 5, rng) - 1.0)
    # 整数位移域扭曲 (最近邻, 随后热侵蚀会抹平块状感)
    wx = ((fbm(size, 5, 3, rng) - 0.5) * 2.0 * 60).astype(np.int64)
    wy = ((fbm(size, 5, 3, rng) - 0.5) * 2.0 * 60).astype(np.int64)
    ii = np.clip(np.arange(size)[:, None] + wy, 0, size - 1)
    jj = np.clip(np.arange(size)[None, :] + wx, 0, size - 1)
    ridge = ridge[ii, jj]
    dist_air = np.hypot(east - 45.0, north - 400.0)
    mask = 0.18 + 0.82 * _smoothstep((dist_air - 450.0) / 1100.0)
    h = 72.0 + mask * (base * 150.0 + (ridge ** 2) * 70.0)

    # --- 热侵蚀 (talus = 35° 对应每格高差) ---
    h = thermal_erosion(h, erosion_iters, talus=float(np.tan(np.radians(35.0)) * cell))

    # --- 图边缘渐变到常数高度 (在河谷刻蚀之前: 河流仍可穿出图边, 峡谷不被抹平) ---
    edge_d = np.minimum(np.minimum(east + half, half - east), np.minimum(north + half, half - north))
    w_edge = 1.0 - _smoothstep(edge_d / EDGE_FADE)
    h = h * (1.0 - w_edge) + EDGE_H * w_edge

    # --- 河谷刻蚀 ---
    river = build_river(world, seed)
    d, k = river_distance(size, world, river)
    bed = river["bed"][k]
    half_w = river["width"][k] * 0.5
    valley = bed + np.maximum(d - half_w, 0.0) * 0.35          # 河床平底 + 约 19° 谷坡
    bank = 0.5 * (1.0 + np.tanh((valley - h) / 6.0))           # 平滑最小值: 谷底低于地表处取谷底
    h = h * bank + valley * (1.0 - bank)

    # --- 机场平整台地 (最后写入, 保证常数) ---
    ex0, ex1 = AIRPORT_EAST
    nz0, nz1 = AIRPORT_NORTH

    # --- 进近/起飞走廊净空面: 台地南/北边缘外, 沿跑道中线 (east=0) 的走廊内把地形压到斜面以下 ---
    lateral = np.maximum(np.abs(east) - CORRIDOR_HALF_W, 0.0)
    w_corr = 1.0 - _smoothstep(lateral / CORRIDOR_FEATHER)
    for dist_out, deg in ((np.maximum(nz0 - north, 0.0), APPROACH_DEG),
                          (np.maximum(north - nz1, 0.0), DEPARTURE_DEG)):
        cap = AIRPORT_H + dist_out * float(np.tan(np.radians(deg)))
        w = np.where(dist_out > 0.0, w_corr, 0.0)
        h = np.where(h > cap, h * (1.0 - w) + cap * w, h)
    dx = np.maximum(np.maximum(ex0 - east, east - ex1), 0.0)
    dz = np.maximum(np.maximum(nz0 - north, north - nz1), 0.0)
    dd = np.hypot(dx, dz)
    w = 1.0 - _smoothstep(dd / AIRPORT_FEATHER)
    inside = (dx == 0.0) & (dz == 0.0)
    h = h * (1.0 - w) + AIRPORT_H * w
    h = np.where(inside, np.float32(AIRPORT_H), h)

    h = np.maximum(h, 0.0).astype(np.float32)
    return h, river, d


def slope_deg(h: np.ndarray, cell: float) -> np.ndarray:
    gy, gx = np.gradient(h, cell)
    return np.degrees(np.arctan(np.hypot(gx, gy))).astype(np.float32)


def write_outputs(base: Path, h, river, dist, size, world, seed):
    base.parent.mkdir(parents=True, exist_ok=True)
    cell = world / (size - 1)
    scale = float(np.ceil(h.max() + 1.0))
    q = np.rint(np.clip(h / scale, 0.0, 1.0) * 65535.0).astype(">u2")
    raw = base.with_suffix(".raw")
    raw.write_bytes(q.tobytes())

    meta = {
        "terrain": "forest_4km", "size_px": size, "world_size_m": world,
        "height_scale_m": scale, "sea_level_offset_m": 0.0, "seed": seed,
        "airport_height_m": AIRPORT_H, "edge_height_m": EDGE_H,
        "corridor": {"half_width_m": CORRIDOR_HALF_W, "approach_deg": APPROACH_DEG, "departure_deg": DEPARTURE_DEG},
        "airport_platform": {"east": list(AIRPORT_EAST), "north": list(AIRPORT_NORTH),
                              "feather_m": AIRPORT_FEATHER},
        "raw_file": raw.name, "riverdist_u8": base.name + ".riverdist.u8", "riverdist_step_m": 2.0,
        "row_order": "north_to_south", "byte_order": "big-endian uint16",
        "axis_convention": "行=北向索引增(向南), 列=东向索引增; 中心=世界原点",
    }
    base.with_suffix(".meta.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")

    rj = {"length_m": river["length_m"], "fall_s_m": river["fall_s"],
          "fall_drop_m": FALL_DROP, "water_depth_m": WATER_DEPTH, "points": [
              {"s": round(float(a), 2), "east": round(float(b), 2), "north": round(float(c), 2),
               "width": round(float(w), 2), "bed": round(float(d), 3), "water": round(float(e), 3)}
              for a, b, c, w, d, e in zip(river["s"], river["east"], river["north"],
                                          river["width"], river["bed"], river["water"])]}
    base.with_suffix(".river.json").write_text(json.dumps(rj, ensure_ascii=False), encoding="utf-8")
    np.save(str(base) + ".slope.npy", slope_deg(h, cell))
    np.save(str(base) + ".riverdist.npy", dist)
    # Unity 读的河距图: uint8, 每级 2 m, 封顶 508 m (npy 只给 Python 侧用)
    Path(str(base) + ".riverdist.u8").write_bytes(
        np.clip(np.rint(dist / 2.0), 0, 254).astype(np.uint8).tobytes())

    try:
        from PIL import Image
        sh = np.clip((np.gradient(h, cell)[1] * -2.0 + 0.5), 0, 1)
        t = np.clip(h / scale, 0, 1)
        img = np.stack([(0.25 + 0.6 * t) * (0.6 + 0.4 * sh), (0.40 + 0.45 * t) * (0.6 + 0.4 * sh),
                        (0.22 + 0.4 * t) * (0.6 + 0.4 * sh)], axis=-1)
        img[dist < river["width"].max() * 0.5] = (0.2, 0.45, 0.85)
        Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).resize((1024, 1024)).save(
            str(base) + ".preview.png")
    except Exception as ex:   # 预览只是辅助
        print("预览生成跳过:", ex)
    return meta


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out_base", type=Path)
    ap.add_argument("--size", type=int, default=2049)
    ap.add_argument("--world", type=float, default=4000.0)
    ap.add_argument("--seed", type=int, default=11)
    ap.add_argument("--erosion", type=int, default=40)
    a = ap.parse_args(argv)
    h, river, dist = generate(a.size, a.world, a.seed, a.erosion)
    meta = write_outputs(a.out_base, h, river, dist, a.size, a.world, a.seed)
    print(f"输出 {a.out_base.with_suffix('.raw')} {a.size}x{a.size} 高度 {h.min():.1f}..{h.max():.1f} m, "
          f"河长 {river['length_m']:.0f} m, height_scale={meta['height_scale_m']}")


if __name__ == "__main__":
    main()
