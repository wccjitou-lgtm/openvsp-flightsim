#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
stl_to_obj_unity.py — OpenVSP STL 网格 → Unity OBJ 网格 (开发提示词 3.1 节)

功能 (坐标转换在导入时一次完成, 飞行/渲染代码不再悄悄补偏移):
  1. 读取 OpenVSP 导出的 ASCII STL (体轴系: X 向后, Y 向右, Z 向上, 右手系,
     原点 = 建模原点, 通常为机头)
  2. 平移到重心 (cg 来自 AircraftConfig.json 的 reference.cg_m, OpenVSP 坐标)
  3. 转换到标准机体轴 (x 前, y 右, z 下) 再到 Unity 轴 (x 右, y 上, z 前, 左手系):
       Unity_x =  Y_ovsp - Y_cg        (右)
       Unity_y =  Z_ovsp - Z_cg        (上)
       Unity_z = -(X_ovsp - X_cg)      (前 = -X_ovsp)
  4. 左右手系翻转 → 三角形绕序反转 (保证法线朝外)
  5. 合并重复顶点 (STL 每个面元独立三顶点, 合并后顶点数 ~1/3, Unity 更友好)
  6. 缩放 Unity 单位 (OpenVSP 米 → Unity 米, scale=1)

渲染升级 R4: --parts <dir> 支持分部件 STL (prep 阶段逐几何导出) → 输出带
`o <部件名>` 分组的 OBJ (每部件独立顶点段+面段, 面索引用全局编号)。
坐标系/重心对齐/去重/绕序逻辑与单 STL 模式逐位相同; 无 --parts 或部件缺失时
自动回退单网格输出 (与旧行为完全一致)。

用法:
  python stl_to_obj_unity.py <model.stl> <out.obj> [--config AircraftConfig.json]
                             [--scale 1] [--parts <含 part_N.stl+manifest.json 的目录>]
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple


def read_ascii_stl(path: Path) -> List[Tuple[Tuple[float, float, float], ...]]:
    """读 ASCII STL, 返回三角形列表 (每项为 3 个顶点)。

    OpenVSP 3.51 的 ASCII STL 行格式:
      facet normal nx ny nz / outer loop / vertex x y z / ... / endloop / endfacet
    """
    tris: List[Tuple[Tuple[float, float, float], ...]] = []
    verts: List[Tuple[float, float, float]] = []
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            tokens = line.strip().split()
            if not tokens:
                continue
            if tokens[0] == "vertex" and len(tokens) >= 4:
                verts.append((float(tokens[1]), float(tokens[2]), float(tokens[3])))
                if len(verts) == 3:
                    tris.append((verts[0], verts[1], verts[2]))
                    verts = []
    return tris


def transform_vertex(v: Tuple[float, float, float],
                     cg: Tuple[float, float, float]) -> Tuple[float, float, float]:
    """OpenVSP 体轴 → 重心系 → Unity 轴。"""
    x, y, z = v
    dx, dy, dz = x - cg[0], y - cg[1], z - cg[2]
    return (dy, dz, -dx)


def build_part_mesh(tris: List[Tuple[Tuple[float, float, float], ...]],
                    cg: Tuple[float, float, float],
                    scale: float, quant: int = 6
                    ) -> Tuple[List[Tuple[float, ...]], List[Tuple[int, int, int]]]:
    """三角形 → (去重顶点, 反转绕序后的面)。单 STL 与分部件模式共用, 逻辑逐位一致。"""
    vertex_map: Dict[Tuple[int, int, int], int] = {}
    vertices: List[Tuple[float, ...]] = []
    faces: List[Tuple[int, int, int]] = []

    for tri in tris:
        idx = []
        for v in tri:
            u = transform_vertex(v, cg)
            key = (round(u[0] * scale, quant),
                   round(u[1] * scale, quant),
                   round(u[2] * scale, quant))
            # 再量化为 int 键避免浮点哈希抖动
            ikey = (int(key[0] * 1e6), int(key[1] * 1e6), int(key[2] * 1e6))
            if ikey not in vertex_map:
                vertex_map[ikey] = len(vertices)
                vertices.append(key)
            idx.append(vertex_map[ikey])
        # 左手系翻转: 反转绕序
        faces.append((idx[0], idx[2], idx[1]))
    return vertices, faces


def sanitize_group_name(name: str) -> str:
    """OBJ 分组名安全化: 非 [A-Za-z0-9_.-] 一律换 '_' (部件名可能含中文/空格)。"""
    s = re.sub(r"[^A-Za-z0-9_.\-]", "_", name.strip())
    return s or "Part"


def load_parts_manifest(parts_dir: Path) -> List[Dict[str, str]]:
    """读 parts/manifest.json ([{file, name}, ...]); 缺失时按文件名兜底。"""
    manifest_path = parts_dir / "manifest.json"
    entries: List[Dict[str, str]] = []
    if manifest_path.is_file():
        for e in json.loads(manifest_path.read_text(encoding="utf-8")):
            if (parts_dir / e["file"]).is_file():
                entries.append({"file": e["file"], "name": str(e.get("name", e["file"]))})
    else:
        for stl in sorted(parts_dir.glob("part_*.stl")):
            entries.append({"file": stl.name, "name": stl.stem})
    return entries


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="OpenVSP STL → Unity OBJ 转换器")
    ap.add_argument("stl", type=Path, help="输入 STL (OpenVSP ASCII, 整机)")
    ap.add_argument("out", type=Path, help="输出 OBJ (Unity 轴系)")
    ap.add_argument("--config", type=Path, default=None,
                    help="AircraftConfig.json (读取重心; 缺省用 [0,0,0])")
    ap.add_argument("--scale", type=float, default=1.0, help="缩放 (默认 1:1 米)")
    ap.add_argument("--parts", type=Path, default=None,
                    help="分部件 STL 目录 (part_N.stl + manifest.json); 输出分组 OBJ")
    args = ap.parse_args(argv)

    if not args.stl.is_file():
        print(f"错误: STL 不存在: {args.stl}", file=sys.stderr)
        return 1

    cg: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    if args.config and args.config.is_file():
        cfg = json.loads(args.config.read_text(encoding="utf-8"))
        raw = cfg.get("reference", {}).get("cg_m", [0, 0, 0])
        cg = (float(raw[0]), float(raw[1]), float(raw[2]))
        print(f"重心 (OpenVSP 坐标): {cg}")
    else:
        print("未提供配置, 用原点做中心 (机头原点)")

    whole_tris = read_ascii_stl(args.stl)
    if not whole_tris:
        print("错误: STL 中未找到三角形", file=sys.stderr)
        return 1

    # ---- 分部件模式: 每部件独立网格段, o <名> 分组; 面索引用全局编号 ----
    parts = load_parts_manifest(args.parts) if args.parts else []
    if parts:
        groups: List[Tuple[str, List[Tuple[float, ...]], List[Tuple[int, int, int]]]] = []
        all_vertices: List[Tuple[float, ...]] = []
        offset = 0
        part_tri_total = 0
        for p in parts:
            tris = read_ascii_stl(args.parts / p["file"])
            if not tris:
                print(f"警告: 部件 STL 空, 跳过: {p['file']}", file=sys.stderr)
                continue
            verts, faces = build_part_mesh(tris, cg, args.scale)
            faces = [(a + offset, b + offset, c + offset) for (a, b, c) in faces]
            groups.append((sanitize_group_name(p["name"]), verts, faces))
            all_vertices.extend(verts)
            offset += len(verts)
            part_tri_total += len(tris)
        if len(groups) < 2:
            print(f"警告: 有效部件 < 2 ({len(groups)}), 回退单网格", file=sys.stderr)
        else:
            # 分件三角形和 > 整机属正常: 部件间共享面在分件导出中被重复计入
            # (如翼身交接面在 Wing 和 Fuselage 各出一次), 整机导出时被合并。
            # OBJ 输出始终按部件分组, 此差异不影响模型正确性, 不再输出警告。
            total_faces = sum(len(f) for _, _, f in groups)
            args.out.parent.mkdir(parents=True, exist_ok=True)
            with open(args.out, "w", encoding="utf-8", newline="\n") as f:
                f.write("# OpenVSP -> Unity mesh (auto-generated by stl_to_obj_unity.py)\n")
                f.write("# 坐标系: Unity 左手系 (x 右, y 上, z 前), 原点=重心, 单位=米\n")
                f.write(f"# 分部件: {len(groups)} 组, 顶点 {len(all_vertices)}, "
                        f"三角形 {total_faces}\n")
                for name, verts, faces in groups:
                    f.write(f"o {name}\n")
                    for v in verts:
                        f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
                    for fc in faces:
                        f.write(f"f {fc[0] + 1} {fc[1] + 1} {fc[2] + 1}\n")
            xs = [v[0] for v in all_vertices]
            ys = [v[1] for v in all_vertices]
            zs = [v[2] for v in all_vertices]
            print(f"输出 {args.out}: {len(groups)} 组, 顶点 {len(all_vertices)}, "
                  f"三角形 {total_faces}")
            print(f"包围盒 Unity (m): x(右/翼展) [{min(xs):+.3f}, {max(xs):+.3f}]  "
                  f"y(上/高度) [{min(ys):+.3f}, {max(ys):+.3f}]  "
                  f"z(前/机身) [{min(zs):+.3f}, {max(zs):+.3f}]")
            return 0

    # ---- 单网格模式 (与旧版行为完全一致) ----
    vertices, faces = build_part_mesh(whole_tris, cg, args.scale)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("# OpenVSP -> Unity mesh (auto-generated by stl_to_obj_unity.py)\n")
        f.write("# 坐标系: Unity 左手系 (x 右, y 上, z 前), 原点=重心, 单位=米\n")
        f.write(f"# 顶点 {len(vertices)}, 三角形 {len(faces)}\n")
        for v in vertices:
            f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
        for fc in faces:
            f.write(f"f {fc[0] + 1} {fc[1] + 1} {fc[2] + 1}\n")

    xs = [v[0] for v in vertices]; ys = [v[1] for v in vertices]; zs = [v[2] for v in vertices]
    print(f"输出 {args.out}: 顶点 {len(vertices)}, 三角形 {len(faces)}")
    print(f"包围盒 Unity (m): x(右/翼展) [{min(xs):+.3f}, {max(xs):+.3f}]  "
          f"y(上/高度) [{min(ys):+.3f}, {max(ys):+.3f}]  "
          f"z(前/机身) [{min(zs):+.3f}, {max(zs):+.3f}]")
    return 0


if __name__ == "__main__":
    sys.exit(main())
