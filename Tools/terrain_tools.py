#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
terrain_tools.py — 地形数据管线 (Phase 4, 开发提示词 5.7 节)

两个功能:
  1. gen   : fBm 分形噪声合成山地高度图 -> Unity Terrain 16-bit RAW (big-endian)
             + 元数据 JSON (尺寸/高度比例/地理范围)
  2. srtm  : 解析真实 SRTM HGT 数字高程文件 (.hgt, NASA/USGS 标准格式,
             SRTM1 3601x3601 / SRTM3 1201x1201, 16-bit big-endian, 高程米)
             -> 同样的 Unity RAW + 元数据 (供真实地形导入)

输出约定 (与 C# HeightmapTerrain / Unity TerrainFromHeightmap 一致):
  - RAW: 16-bit 无符号 big-endian, 行主序, 北->南 (第一行是最北)
  - 元数据 JSON: { size_px, world_size_m, height_scale_m, terrain,
                   lat_min/lat_max/lon_min/lon_max (srtm 时), sea_level_offset_m }

用法:
  python terrain_tools.py gen  Data/Terrain/mountain --size 513 --world 2000 --peak 450 --seed 7
  python terrain_tools.py srtm Data/Terrain/real --hgt N28E086.hgt --world 8000 --sample 4
"""

from __future__ import annotations

import argparse
import json
import math
import random
import struct
import sys
from pathlib import Path
from typing import List, Optional


# ============================================================
# fBm 分形山地生成
# ============================================================

def _value_noise_2d(x: float, y: float, seed: int) -> float:
    """确定性 2D value noise (哈希格点 + 平滑双线性插值), 输出 [0,1]。"""
    def hash_grid(ix: int, iy: int) -> float:
        h = (ix * 374761393 + iy * 668265263 + seed * 1442695041) & 0xFFFFFFFF
        h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0

    x0, y0 = math.floor(x), math.floor(y)
    fx, fy = x - x0, y - y0
    # 平滑插值权重 (quintic)
    sx = fx * fx * fx * (fx * (fx * 6 - 15) + 10)
    sy = fy * fy * fy * (fy * (fy * 6 - 15) + 10)

    n00 = hash_grid(x0, y0)
    n10 = hash_grid(x0 + 1, y0)
    n01 = hash_grid(x0, y0 + 1)
    n11 = hash_grid(x0 + 1, y0 + 1)
    top = n00 + sx * (n10 - n00)
    bot = n01 + sx * (n11 - n01)
    return top + sy * (bot - top)


def fbm(x: float, y: float, seed: int, octaves: int = 6,
        lacunarity: float = 2.0, gain: float = 0.5) -> float:
    """分形布朗运动, 输出约 [0,1]。"""
    amp, freq, total, norm = 1.0, 1.0, 0.0, 0.0
    for _ in range(octaves):
        total += amp * _value_noise_2d(x * freq, y * freq, seed)
        norm += amp
        amp *= gain
        freq *= lacunarity
    return total / norm


def gen_mountain_heightmap(size_px: int, peak_m: float, seed: int,
                           ridge_count: int = 3) -> List[List[float]]:
    """生成山地高程 (米), 返回 size_px x size_px 行主序 (北->南)。

    山体 = 径向山脊基座 (cos 形) x fBm 起伏, 山谷平原过渡到边缘 0。
    """
    random.seed(seed)
    ridges = []
    for _ in range(ridge_count):
        ridges.append((random.uniform(0.25, 0.75) * size_px,
                       random.uniform(0.25, 0.75) * size_px,
                       random.uniform(0.25, 0.5) * size_px,   # 半径
                       random.uniform(0.5, 1.0)))             # 相对峰高

    heights: List[List[float]] = []
    max_h = 0.0
    for j in range(size_px):
        row: List[float] = []
        for i in range(size_px):
            base = 0.0
            for (cx, cy, rad, ph) in ridges:
                d = math.hypot(i - cx, j - cy) / rad
                if d < 1.0:
                    base += ph * (0.5 + 0.5 * math.cos(math.pi * d))
            # fBm 细节: 山脊上振幅大, 平原振幅小
            detail = fbm(i / size_px * 6.0, j / size_px * 6.0, seed, octaves=6)
            h = base * (0.55 + 0.7 * detail) * peak_m
            # 边缘衰减 (避免地图像孤岛一样陡然截断)
            edge = min(i, j, size_px - 1 - i, size_px - 1 - j) / (size_px * 0.15)
            edge = min(edge, 1.0)
            h *= edge * edge * (3 - 2 * edge)
            row.append(h)
            max_h = max(max_h, h)
        heights.append(row)
    return heights


# ============================================================
# 输出 RAW + 元数据
# ============================================================

def write_unity_raw(heights_m: List[List[float]], out_base: Path,
                    world_size_m: float, terrain_name: str,
                    geo: Optional[dict] = None, sea_level_offset_m: float = 0.0):
    size_px = len(heights_m)
    peak = max(max(r) for r in heights_m)
    height_scale = max(peak, 1.0)

    out_base.parent.mkdir(parents=True, exist_ok=True)
    raw_path = out_base.with_suffix(".raw")
    with open(raw_path, "wb") as f:
        for row in heights_m:
            f.write(struct.pack(f">{size_px}H", *[
                int(round(max(0.0, min(height_scale, h - sea_level_offset_m)) / height_scale * 65535))
                for h in row]))

    meta = {
        "terrain": terrain_name,
        "size_px": size_px,
        "world_size_m": world_size_m,
        "height_scale_m": height_scale,
        "sea_level_offset_m": sea_level_offset_m,
        "raw_file": raw_path.name,
        "row_order": "north_to_south",
        "byte_order": "big-endian uint16",
        "axis_convention": "行=北向索引增(向南), 列=东向索引增",
    }
    if geo:
        meta.update(geo)
    meta_path = out_base.with_suffix(".meta.json")
    meta_path.write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"输出 {raw_path.name} ({size_px}x{size_px}, 峰值 {peak:.1f} m, "
          f"世界 {world_size_m} m)")
    print(f"元数据 {meta_path}")
    return meta


# ============================================================
# SRTM HGT 解析
# ============================================================

def parse_hgt(hgt_path: Path, sample: int = 1) -> List[List[float]]:
    """解析 SRTM .hgt -> 高程米 (行主序, 北->南)。

  HGT: 16-bit big-endian 有符号高程 (米), void = -32768。
  文件名如 N28E086.hgt -> 左下角 (28N, 86E), 1 度网格。
  sample: 抽样因子 (SRTM3 在 8000m 场景常取 4 -> ~287x287)。
    """
    n = int(math.sqrt(hgt_path.stat().st_size // 2))
    if n not in (1201, 3601):
        raise ValueError(f"非标准 HGT 尺寸: {n}x{n} (期望 1201 或 3601)")
    with open(hgt_path, "rb") as f:
        data = f.read()

    heights: List[List[float]] = []
    for j in range(0, n, sample):
        row: List[float] = []
        for i in range(0, n, sample):
            v = struct.unpack_from(">h", data, (j * n + i) * 2)[0]
            row.append(0.0 if v == -32768 else float(v))   # void -> 海平面
        heights.append(row)
    return heights


def hgt_geo_from_name(name: str) -> dict:
    """N28E086 -> {lat_min:28, lat_max:29, lon_min:86, lon_max:87}"""
    import re
    m = re.match(r"([NS])(\d+)([EW])(\d+)", name)
    if not m:
        return {}
    lat = int(m.group(2)) * (1 if m.group(1) == "N" else -1)
    lon = int(m.group(4)) * (1 if m.group(3) == "E" else -1)
    return {"lat_min": lat, "lat_max": lat + 1, "lon_min": lon, "lon_max": lon + 1}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="地形数据管线")
    sub = ap.add_subparsers(dest="cmd", required=True)

    g = sub.add_parser("gen", help="fBm 合成山地")
    g.add_argument("out", type=Path)
    g.add_argument("--size", type=int, default=513)
    g.add_argument("--world", type=float, default=2000)
    g.add_argument("--peak", type=float, default=450)
    g.add_argument("--seed", type=int, default=7)

    s = sub.add_parser("srtm", help="解析 SRTM HGT")
    s.add_argument("out", type=Path)
    s.add_argument("--hgt", type=Path, required=True)
    s.add_argument("--world", type=float, default=8000)
    s.add_argument("--sample", type=int, default=4)
    s.add_argument("--offset", type=float, default=0.0,
                   help="海平面偏移 (m): 削去基准高度, 让机场平原可放低处")

    args = ap.parse_args(argv)
    if args.cmd == "gen":
        heights = gen_mountain_heightmap(args.size, args.peak, args.seed)
        write_unity_raw(heights, args.out, args.world, "mountain_fbm")
    else:
        if not args.hgt.is_file():
            print(f"错误: HGT 不存在: {args.hgt}", file=sys.stderr)
            return 1
        heights = parse_hgt(args.hgt, args.sample)
        geo = hgt_geo_from_name(args.hgt.stem)
        write_unity_raw(heights, args.out, args.world, f"srtm_{args.hgt.stem}",
                        geo=geo, sea_level_offset_m=args.offset)
    return 0


if __name__ == "__main__":
    sys.exit(main())
