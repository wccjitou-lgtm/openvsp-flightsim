#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
terrain_airport.py — 机场台地 / 进近走廊 / 图边缘渐变 的通用模块 (多场景 P2)

海岛 (terrain_island.py)、雪谷 (terrain_alpine.py) 生成器共用; 森林生成器
terrain_forest.py 保持自带的同等逻辑不动 (其产物与测试是基线, 不允许被本模块改变)。
数学与 terrain_forest.generate 的 "图边缘渐变 / 走廊净空面 / 机场台地" 三段等价。

坐标: east = (1,N) 行向量, north = (N,1) 列向量, 世界米, 中心 = 原点 (与 terrain_forest 一致)。
所有 apply_* 返回新数组 (float32), 不修改入参。
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from terrain_forest import _smoothstep


@dataclass(frozen=True)
class Platform:
    """机场台地: 矩形范围 (east/north 区间, 含跑道/滑行道/停机坪/机库+余量) + 常数高度 + 外围羽化。"""
    east: tuple = (-80.0, 170.0)
    north: tuple = (-110.0, 910.0)
    height: float = 62.0
    feather: float = 140.0


@dataclass(frozen=True)
class Corridor:
    """进近 (南端) / 起飞 (北端) 净空限制面: 台地边缘外沿中线 east=0 的斜面, 走廊内地形不得高于它。"""
    half_width: float = 100.0
    feather: float = 150.0
    approach_deg: float = 1.5
    departure_deg: float = 2.0


def world_axes(size: int, world: float):
    """返回 (east (1,N) float32, north (N,1) float32, cell)。RAW 行 r: north=+half-r*cell, 列 c: east=-half+c*cell。"""
    half = world / 2.0
    cell = world / (size - 1)
    east = (-half + np.arange(size) * cell).astype(np.float32)[None, :]
    north = (half - np.arange(size) * cell).astype(np.float32)[:, None]
    return east, north, cell


def platform_mask(east, north, plat: Platform) -> np.ndarray:
    """台地矩形内部 (含边界) 的布尔掩码, shape (N,N)。"""
    e0, e1 = plat.east
    n0, n1 = plat.north
    return (east >= e0) & (east <= e1) & (north >= n0) & (north <= n1)


def apply_edge_fade(h, east, north, world: float, edge_h: float, fade: float = 500.0):
    """图边缘一圈渐变到常数高度 edge_h (陆地场景: 与远景地面同高; 海岛: 取海底高度)。"""
    half = world / 2.0
    edge_d = np.minimum(np.minimum(east + half, half - east), np.minimum(north + half, half - north))
    w = 1.0 - _smoothstep(edge_d / fade)
    return (h * (1.0 - w) + np.float32(edge_h) * w).astype(np.float32)


def apply_corridor(h, east, north, plat: Platform, cor: Corridor):
    """台地南/北边缘外、走廊 (|east| <= half_width, 外侧羽化) 内, 把高于限制面的地形压向限制面。
    只会降低地形, 不会抬高, 所以低于限制面的水域/谷底不受影响。"""
    lateral = np.maximum(np.abs(east) - cor.half_width, 0.0)
    w_corr = 1.0 - _smoothstep(lateral / cor.feather)
    n0, n1 = plat.north
    out = h
    for dist_out, deg in ((np.maximum(n0 - north, 0.0), cor.approach_deg),
                          (np.maximum(north - n1, 0.0), cor.departure_deg)):
        cap = plat.height + dist_out * float(np.tan(np.radians(deg)))
        w = np.where(dist_out > 0.0, w_corr, 0.0)
        out = np.where(out > cap, out * (1.0 - w) + cap * w, out)
    return out.astype(np.float32)


def apply_platform(h, east, north, plat: Platform):
    """机场平整台地 (必须最后写入): 内部为精确常数, 外围 feather 米内平滑过渡。"""
    e0, e1 = plat.east
    n0, n1 = plat.north
    dx = np.maximum(np.maximum(e0 - east, east - e1), 0.0)
    dz = np.maximum(np.maximum(n0 - north, north - n1), 0.0)
    dd = np.hypot(dx, dz)
    w = 1.0 - _smoothstep(dd / plat.feather)
    inside = (dx == 0.0) & (dz == 0.0)
    out = h * (1.0 - w) + np.float32(plat.height) * w
    return np.where(inside, np.float32(plat.height), out).astype(np.float32)


def write_raw(raw_path: Path, h: np.ndarray):
    """16 位大端 RAW (行主序北->南), 与 terrain_forest.write_outputs 同格式。
    量程 height_scale = ceil(max+1), 高度不得为负 (海岛的海底由生成器整体抬高)。返回 height_scale_m。"""
    raw_path = Path(raw_path)
    raw_path.parent.mkdir(parents=True, exist_ok=True)
    scale = float(np.ceil(h.max() + 1.0))
    q = np.rint(np.clip(h / scale, 0.0, 1.0) * 65535.0).astype(">u2")
    raw_path.write_bytes(q.tobytes())
    return scale


def make_meta(terrain: str, size: int, world: float, height_scale: float, seed: int,
              plat: Platform, cor: Corridor, edge_h: float,
              biome: str, sea_level_m: float | None = None, extra: dict | None = None) -> dict:
    """meta.json 字典: 字段与 forest.meta.json 同名同义 (Unity TerrainFromHeightmap 读 size_px/world_size_m/
    height_scale_m/airport_height_m/edge_height_m), 新增 biome 与可选 sea_level_m (无海则不写)。"""
    meta = {
        "terrain": terrain, "size_px": size, "world_size_m": world,
        "height_scale_m": height_scale, "sea_level_offset_m": 0.0, "seed": seed,
        "airport_height_m": plat.height, "edge_height_m": edge_h,
        "biome": biome,
        "corridor": {"half_width_m": cor.half_width, "approach_deg": cor.approach_deg,
                     "departure_deg": cor.departure_deg},
        "airport_platform": {"east": list(plat.east), "north": list(plat.north), "feather_m": plat.feather},
        "raw_file": terrain + ".raw",
        "row_order": "north_to_south", "byte_order": "big-endian uint16",
        "axis_convention": "行=北向索引增(向南), 列=东向索引增; 中心=世界原点",
    }
    if sea_level_m is not None:
        meta["sea_level_m"] = sea_level_m
    if extra:
        meta.update(extra)
    return meta


def corridor_cap(plat: Platform, cor: Corridor, north_m: float) -> float:
    """沿中线某北向坐标处的净空限制面高度 (台地内返回台地高度); 测试与生成器自检用。"""
    if north_m < plat.north[0]:
        return plat.height + (plat.north[0] - north_m) * math.tan(math.radians(cor.approach_deg))
    if north_m > plat.north[1]:
        return plat.height + (north_m - plat.north[1]) * math.tan(math.radians(cor.departure_deg))
    return plat.height
