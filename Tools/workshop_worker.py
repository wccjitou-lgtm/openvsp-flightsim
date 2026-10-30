#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
workshop_worker.py — 机型工坊常驻 OpenVSP worker (W1 骨架版)

架构 (开发提示词 §3): Unity (IMGUI 工坊) <--JSON-Lines over stdio--> 本进程。
worker 持有"唯一的活动模型", 串行处理请求 (同一时刻只处理一个)。

协议 (提示词 §9, W1 子集):
  请求  {"id": N, "cmd": "...", "args": {...}}          (每行一个 JSON)
  响应  {"id": N, "ok": true, "data": {...}}
        {"id": N, "ok": false, "error": "原始错误文本"}
  推送  {"event": "log", "msg": "..."}                   (W1 仅日志)

命令 (W1):
  ping                          健康检查
  new                           清空模型 (槽位全空, 工作名 Unnamed)
  open {path}                   读 vsp3 → 返回几何清单 + 同名 .workshop.json 的槽位分配 (若有)
  save {path}                   WriteVSPFile 整机 (含未分配几何, 排除语义=隐藏状态)
  list_geoms                    [{id,name,type,hidden,slot,subsurfs}]
  assign_slot {slot, geom_id}   几何→槽位 (入气动集3+显示; 同几何不得进两槽)
  clear_slot {slot}             槽位选「无」(移出集3+隐藏 = 从 3D/质量/气动排除)
  export_meshes {dir?}          每个激活槽位导出单件 STL → {slot:{path,tris,name}}
  mass_props                    MassProp → 解析文本报告 → {mass_kg, cg_m, per_geom}
  list_library                  扫描 Data/Workshop/library/<类型>/*.vsp3
  save_part {geom_id, name, type}  单几何另存入库 (快照-裁剪-另存-恢复)
  shutdown                      退出进程

排除语义 (W1): 几何隐藏 ⇔ 从 3D / 质量 / VSPAERO 排除; 气动集3 = 已分配槽位几何。
未分配几何保持显示 (质量计入、vsp3 写回), 不入气动集。

stdout 纪律: C++/SWIG 层的 printf 会污染协议通道 → 启动即把 fd1 重定向到 stderr,
JSON 走 dup 保存的句柄 (W0 冒烟验证过的模式)。
"""
from __future__ import annotations

import json
import math
import os
import shutil
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # workshop_common/parse_massprops
import workshop_common as wc
import parse_massprops

_json_fd = os.dup(1)
os.dup2(2, 1)
sys.stdout = os.fdopen(_json_fd, "w", encoding="utf-8", newline="\n")

wc.setup_openvsp()
import openvsp as vsp  # noqa: E402  (须在路径注入后)
import workshop_model as wm  # noqa: E402  (其内部 import openvsp, 同样须在注入后)

ROOT = wc.project_root()
MESH_DIR = ROOT / "work" / "workshop_mesh"
REPORT_STEM = ROOT / "work" / "workshop_report"


def send(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def log(msg: str) -> None:
    send({"event": "log", "msg": msg})


def _write_vsp_atomic(path: Path) -> None:
    """原子写 vsp3 (U7): 先写 .tmp 再 os.replace — 换机取消 taskkill 杀在
    写盘中途时不留截断的目标文件, 读回失败会被上层 0 几何守卫拦下。"""
    tmp = path.with_name(path.name + ".tmp")
    vsp.WriteVSPFile(str(tmp))
    os.replace(str(tmp), str(path))


class Worker:
    def __init__(self) -> None:
        self.slots: dict[str, str | None] = {s: None for s in wc.SLOTS}
        self.work_name = "Unnamed"
        self.dirty = False
        # 最近一次 MassProp 的部件体积 (m^3), 供机身 Density=质量/体积 换算 (方案A)
        self.last_volume: dict[str, float] = {}
        # 最近一次 MassProp 的壳体机翼面积 (m^2), 供 Mass_Area=质量/面积 换算
        self.last_area: dict[str, float] = {}

    # ---------- 工具 ----------
    def _geom_slot(self, geom_id: str) -> str | None:
        for s, g in self.slots.items():
            if g == geom_id:
                return s
        return None

    def _list_geoms(self) -> list[dict]:
        out = []
        for g in vsp.FindGeoms():
            if vsp.GetGeomTypeName(g) == "Mesh":
                continue
            subs = []
            for s in vsp.GetSubSurfIDVec(g):
                if int(vsp.GetSubSurfType(s)) == vsp.SS_CONTROL:
                    # 枚举法取 parm (保存重开后 (id,name,group) 三元寻址不可靠, W3 实测)
                    sv = {}
                    for pid in vsp.GetSubSurfParmIDs(s):
                        sv[vsp.GetParmName(pid)] = vsp.GetParmVal(pid)
                    subs.append({
                        "id": s,
                        "name": vsp.GetSubSurfName(g, s),
                        "type": int(vsp.GetSubSurfType(s)),
                        "eta_start": sv.get("EtaStart", 0.0),
                        "eta_end": sv.get("EtaEnd", 0.0),
                        "len_c": sv.get("Length_C_Start", 0.0),
                    })
                else:
                    subs.append({"id": s, "name": vsp.GetSubSurfName(g, s),
                                 "type": int(vsp.GetSubSurfType(s))})
            out.append({
                "id": g,
                "name": vsp.GetGeomName(g),
                "type": vsp.GetGeomTypeName(g),
                "hidden": not bool(vsp.GetSetFlag(g, wc.SET_SHOWN)),
                "slot": self._geom_slot(g),
                "subsurfs": subs,
            })
        return out

    def _delete_mesh_geoms(self) -> None:
        dels = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) == "Mesh"]
        if dels:
            vsp.DeleteGeomVec(dels)

    # ---------- 命令 ----------
    def cmd_ping(self, args: dict) -> dict:
        return {"ready": True, "python": sys.version.split()[0],
                "vsproot": str(wc.vsproot()), "name": self.work_name}

    def cmd_new(self, args: dict) -> dict:
        vsp.ClearVSPModel()
        self.slots = {s: None for s in wc.SLOTS}
        self.work_name = "Unnamed"
        self.dirty = False
        return {"geoms": [], "slots": self.slots, "name": self.work_name}

    def cmd_open(self, args: dict) -> dict:
        path = Path(args["path"])
        if not path.is_file():
            raise FileNotFoundError(f"vsp3 不存在: {path}")
        # R01 数据保护: 读新文件前快照当前模型 (含未保存编辑), 失败恢复;
        # 槽位几何名必须在 Clear 前收集 (恢复后几何 id 全变, 按名重映射)
        backup = ROOT / "work" / "open_backup.vsp3"
        had_model = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"]
        names_by_slot = {s: vsp.GetGeomName(g) for s, g in self.slots.items() if g}
        if had_model:
            backup.parent.mkdir(parents=True, exist_ok=True)
            vsp.WriteVSPFile(str(backup))
        vsp.ClearVSPModel()
        vsp.ReadVSPFile(str(path))   # 返回 None (void), 成败以几何数为准 (R4 教训)
        vsp.Update()
        self.last_volume = {}
        self.last_area = {}
        geoms = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"]
        if not geoms:
            # R01: 恢复快照 (槽位按名重映射; 找不到的槽清空)
            if had_model and backup.is_file():
                vsp.ClearVSPModel()
                vsp.ReadVSPFile(str(backup))
                vsp.Update()
                restored = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"]
                name_to_id = {vsp.GetGeomName(g): g for g in restored}
                lost = [s for s, nm in names_by_slot.items() if nm not in name_to_id]
                for s, nm in names_by_slot.items():
                    self.slots[s] = name_to_id.get(nm)
                if lost:
                    log(f"模型恢复但以下槽位几何名失配, 已清空: {lost}")
                raise RuntimeError(
                    f"ReadVSPFile 失败 (0 几何): {path} — 原模型已恢复"
                    f" ({len(restored)} 几何, 槽位按名重映射)")
            raise RuntimeError(f"ReadVSPFile 失败 (0 几何): {path}")
        # 导入后全部几何默认"未排除": 可见 + 入质量集4 (不入气动集3, 槽位分配后才入)
        for g in geoms:
            vsp.SetSetFlag(g, wc.SET_SHOWN, True)
            vsp.SetSetFlag(g, wc.MASS_SET, True)
            vsp.SetSetFlag(g, wc.AERO_SET, False)
        # R06: sidecar 的排除名单按名应用 (排除状态权威在 sidecar, 不靠 vsp3 隐藏位)
        excluded = [str(n) for n in (args.get("excluded_names") or [])]
        if excluded:
            gnames = {vsp.GetGeomName(g): g for g in geoms}
            for nm in excluded:
                gid2 = gnames.get(nm)
                if gid2:
                    vsp.SetSetFlag(gid2, wc.SET_SHOWN, False)
                    vsp.SetSetFlag(gid2, wc.MASS_SET, False)
                    vsp.SetSetFlag(gid2, wc.AERO_SET, False)
                else:
                    log(f"排除名单中的几何 {nm} 不存在 (忽略)")
        vsp.Update()
        # F1: 导入文件部件名含 '_' 或非 ASCII 时提前警告 (VSPAERO 操纵面匹配会断裂)
        bad = [vsp.GetGeomName(g) for g in geoms
               if not wc.geom_name_ok(vsp.GetGeomName(g))]
        if bad:
            log("⚠ 部件名含 '_' 或非 ASCII, VSPAERO 操纵面/气动计算会失败: "
                + ", ".join(bad) + " — 请先在 OpenVSP 中改名 (仅字母数字) 再导入")
        self.slots = {s: None for s in wc.SLOTS}
        self.work_name = path.stem
        self.dirty = False
        data = {"geoms": self._list_geoms(), "slots": self.slots, "name": self.work_name}
        side = path.with_suffix(".workshop.json")
        if side.is_file():
            try:
                data["workshop_json"] = json.loads(side.read_text(encoding="utf-8"))
            except Exception as e:
                log(f"workshop.json 读取失败 (忽略): {e}")
        return data

    def cmd_save(self, args: dict) -> dict:
        path = Path(args["path"])
        # R01 守卫: 模型意外为空且槽位非空 → 拒绝保存 (防止把空模型写进工程文件)
        actives = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"]
        if not actives and any(self.slots.values()):
            raise RuntimeError("活动模型为 0 几何但槽位非空 — 拒绝保存 (疑似状态错乱)")
        path.parent.mkdir(parents=True, exist_ok=True)
        _write_vsp_atomic(path)   # U7: 截断保护
        self.work_name = path.stem
        self.dirty = False
        return {"path": str(path), "name": self.work_name}

    def cmd_list_geoms(self, args: dict) -> dict:
        return {"geoms": self._list_geoms(), "slots": self.slots, "name": self.work_name}

    def cmd_assign_slot(self, args: dict) -> dict:
        slot, geom_id = args["slot"], args["geom_id"]
        if slot not in self.slots:
            raise ValueError(f"未知槽位: {slot}")
        if geom_id and geom_id not in [g["id"] for g in self._list_geoms()]:
            raise ValueError(f"未知几何: {geom_id}")
        if geom_id:
            other = self._geom_slot(geom_id)
            if other and other != slot:
                raise ValueError(
                    f"该几何已分配到 {other} 槽 (W1 尚不支持同件入双槽的独立副本)")
            gtype = vsp.GetGeomTypeName(geom_id)
            if gtype not in wc.SLOT_TYPES[slot]:
                raise ValueError(f"{slot} 槽不接受 {gtype} 类型几何")
        # 旧几何 (若有): 移出气动集+质量集 + 隐藏 = 「无」排除语义
        old = self.slots[slot]
        if old:
            vsp.SetSetFlag(old, wc.AERO_SET, False)
            vsp.SetSetFlag(old, wc.MASS_SET, False)
            vsp.SetSetFlag(old, wc.SET_SHOWN, False)
        self.slots[slot] = geom_id or None
        if geom_id:
            vsp.SetSetFlag(geom_id, wc.AERO_SET, True)
            vsp.SetSetFlag(geom_id, wc.MASS_SET, True)
            vsp.SetSetFlag(geom_id, wc.SET_SHOWN, True)
        vsp.Update()
        self.dirty = True
        return {"geoms": self._list_geoms(), "slots": self.slots}

    def cmd_auto_assign(self, args: dict) -> dict:
        """F4 启发式槽位预分配 (无 sidecar 的导入场景, 提示词 §5.1):
        第一个 Fuselage/Pod → 机身; 展长最大的水平 Wing → 机翼1;
        其余水平翼中最靠后的 → 平尾; 竖直翼 (|X 旋转|>45°) 最靠后的 → 垂尾;
        再余下的水平翼 → 机翼2。已有任何分配时不覆盖 (skipped)。"""
        if any(self.slots.values()):
            return {"assigned": {}, "skipped": True,
                    "geoms": self._list_geoms(), "slots": self.slots}
        geoms = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"]

        def span(g):
            try:
                return vsp.GetParmVal(g, "Span", "XSec_1")
            except Exception:
                return 0.0

        def xloc(g):
            try:
                return vsp.GetParmVal(g, "X_Location", "XForm")
            except Exception:
                return 0.0

        def is_vert(g):
            try:
                return abs(vsp.GetParmVal(g, "X_Rotation", "XForm")) > 45.0
            except Exception:
                return False

        assigned: dict[str, str] = {}

        def grab(slot: str, gid: str) -> None:
            self.cmd_assign_slot({"slot": slot, "geom_id": gid})
            assigned[slot] = vsp.GetGeomName(gid)

        for g in geoms:
            if vsp.GetGeomTypeName(g) in ("Fuselage", "Pod"):
                grab("fuselage", g)
                break
        wings = [g for g in geoms if vsp.GetGeomTypeName(g) == "Wing"]
        verts = sorted([g for g in wings if is_vert(g)], key=xloc, reverse=True)
        hors = [g for g in wings if not is_vert(g)]
        if verts:
            grab("vtail", verts[0])          # 最靠后的竖直翼
        if hors:
            w1 = max(hors, key=span)         # 展长最大 → 机翼1
            grab("wing1", w1)
            hors.remove(w1)
            if hors:
                ht = max(hors, key=xloc)     # 最靠后的水平翼 → 平尾
                grab("htail", ht)
                hors.remove(ht)
                if hors:
                    grab("wing2", max(hors, key=span))
        if assigned:
            self.dirty = True
            log("启发式预分配: " + ", ".join(f"{k}←{v}" for k, v in assigned.items()))
        return {"assigned": assigned,
                "geoms": self._list_geoms(), "slots": self.slots}

    def cmd_clear_slot(self, args: dict) -> dict:
        slot = args["slot"]
        if slot not in self.slots:
            raise ValueError(f"未知槽位: {slot}")
        old = self.slots[slot]
        if old:
            vsp.SetSetFlag(old, wc.AERO_SET, False)
            vsp.SetSetFlag(old, wc.MASS_SET, False)
            vsp.SetSetFlag(old, wc.SET_SHOWN, False)
            vsp.Update()
        self.slots[slot] = None
        self.dirty = True
        return {"geoms": self._list_geoms(), "slots": self.slots}

    def cmd_export_meshes(self, args: dict) -> dict:
        MESH_DIR.mkdir(parents=True, exist_ok=True)
        self._delete_mesh_geoms()
        out: dict[str, dict] = {}
        try:
            for slot, geom_id in self.slots.items():
                if not geom_id:
                    continue
                # 每件: 只留该件入集3 (导出用), 导完恢复
                for s2, g2 in self.slots.items():
                    if g2:
                        vsp.SetSetFlag(g2, wc.AERO_SET, s2 == slot)
                p = MESH_DIR / f"{slot}.stl"
                vsp.ExportFile(str(p), wc.AERO_SET, vsp.EXPORT_STL)
                # ExportFile 烘 MESH 时会隐藏集合内源几何 (AddMeshGeom 替代显示), 立即恢复;
                # 且必须逐件删除烘焙 Mesh — 否则后续导出复用第一件缓存 (审查 R04)
                vsp.SetSetFlag(geom_id, wc.SET_SHOWN, True)
                self._delete_mesh_geoms()
                out[slot] = {"path": str(p), "tris": wc.stl_triangle_count(p),
                             "name": vsp.GetGeomName(geom_id), "geom_id": geom_id}
        finally:
            # U7: 中途异常也清烘焙 Mesh + 恢复全部槽位的集3/可见位
            self._delete_mesh_geoms()
            for s2, g2 in self.slots.items():
                if g2:
                    vsp.SetSetFlag(g2, wc.AERO_SET, True)
                    vsp.SetSetFlag(g2, wc.SET_SHOWN, True)
        return {"meshes": out, "dir": str(MESH_DIR)}

    def cmd_mass_props(self, args: dict) -> dict:
        # 与 import_vsp.py RunIdentifiedMassProps 同款: 先写盘 (SetVSP3FileName 级联
        # 决定报告落点且要求真实文件), SetAnalysisInputDefaults 后再跑, 铁律 2 恢复显示
        REPORT_STEM.parent.mkdir(parents=True, exist_ok=True)
        report = Path(str(REPORT_STEM) + "_MassProps.txt")
        if report.exists():
            report.unlink()
        vis = [g for g in vsp.FindGeoms()
               if vsp.GetGeomTypeName(g) != "Mesh" and vsp.GetSetFlag(g, wc.MASS_SET)]
        if not vis:
            return {"mass_kg": 0.0, "cg_m": [0, 0, 0], "per_geom": {}, "num_comps": 0}
        names = {g: vsp.GetGeomName(g) for g in vis}
        for g in vis:
            vsp.SetSetFlag(g, wc.SET_SHOWN, True)
        vsp.Update()
        vsp.SetVSP3FileName(str(REPORT_STEM) + ".vsp3")
        vsp.WriteVSPFile(str(REPORT_STEM) + ".vsp3")
        vsp.SetAnalysisInputDefaults("MassProp")
        vsp.SetIntAnalysisInput("MassProp", "Set", [wc.MASS_SET], 0)   # 质量集=用户集4
        vsp.ExecAnalysis("MassProp")
        # U8: MassProp 会生成 MESH 型几何 (体网格), 不删会随 save 进 vsp3 —
        # 管线 prep 的 ExportFile 见 set 内已有 MESH 就直接导出这份陈旧网格
        # 而不重新烘整机 (实测整机 STL 缺前机身 → auto_gear 轮位错 → 起飞翻覆)
        self._delete_mesh_geoms()
        # 副作用恢复: MassProp 会 HideAllExcept → 恢复原可见态, 隐藏槽照旧隐藏
        for g in vsp.FindGeoms():
            if vsp.GetGeomTypeName(g) == "Mesh":
                continue
            vsp.SetSetFlag(g, wc.SET_SHOWN, g in names)
        vsp.Update()
        if not report.exists():
            cands = sorted(REPORT_STEM.parent.glob("workshop_report*MassProps*.txt"))
            if cands:
                report = cands[-1]
            else:
                raise RuntimeError(f"MassProp 未产出报告: {report}")
        parsed = parse_massprops.parse_massprops(report)
        per = {}
        self.last_volume = {}
        self.last_area = {}
        for g, nm in names.items():
            if nm not in parsed["components"]:
                continue
            c = parsed["components"][nm]
            # MassProps 部件表只列体积质量; PointMass 只进总量 (W2 实测)。
            # 这里合成部件实际质量 = 表值 + PointMass×(对称片数), 供编辑窗显示。
            pm = vsp.GetParmVal(g, "PointMass", "Mass_Props")
            sym = 2.0 if vsp.GetParmVal(vsp.FindParm(g, "Sym_Planar_Flag", "Sym")) > 0.5 else 1.0
            per[nm] = {"mass_kg": c["Mass"] + pm * sym,
                       "cg_m": [c["cgX"], c["cgY"], c["cgZ"]],
                       "volume_m3": c["Volume"]}
            if c["Volume"] and c["Volume"] > 1e-12:
                self.last_volume[nm] = c["Volume"]
            shell = vsp.GetParmVal(g, "Shell_Flag", "Mass_Props") > 0.5
            mass_area = vsp.GetParmVal(g, "Mass_Area", "Mass_Props")
            if shell and mass_area > 1e-9 and c["Mass"] > 1e-12:
                self.last_area[nm] = c["Mass"] / mass_area
        tot = parsed["totals"]
        return {"mass_kg": tot["mass_kg"], "cg_m": tot["cg_m"], "per_geom": per,
                "num_comps": parsed["num_comps"]}

    def cmd_insert_part(self, args: dict) -> dict:
        """把库部件 (单几何 vsp3) 实例化到槽位: InsertVSPFile + 差集找新几何 + 分配。"""
        slot, path = args["slot"], args["path"]
        if slot not in self.slots:
            raise ValueError(f"未知槽位: {slot}")
        p = Path(path)
        if not p.is_file():
            raise FileNotFoundError(f"部件文件不存在: {p}")
        before = {g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"}
        vsp.InsertVSPFile(str(p), "")
        vsp.Update()
        # 新几何入"未排除"默认态 (可见 + 质量集), 气动集由 assign 决定
        new_geoms = [g for g in vsp.FindGeoms()
                     if vsp.GetGeomTypeName(g) != "Mesh" and g not in before]
        if not new_geoms:
            raise RuntimeError("InsertVSPFile 未新增几何")
        for g in new_geoms:
            vsp.SetSetFlag(g, wc.SET_SHOWN, True)
            vsp.SetSetFlag(g, wc.MASS_SET, True)
        if len(new_geoms) > 1:
            log(f"部件含 {len(new_geoms)} 个几何, 仅第一个进入 {slot} 槽 (其余保留未分配)")
        geom_id = new_geoms[0]
        gtype = vsp.GetGeomTypeName(geom_id)
        if gtype not in wc.SLOT_TYPES[slot]:
            # 类型不符: 回滚插入, 不留残件
            vsp.DeleteGeomVec(new_geoms)
            vsp.Update()
            raise ValueError(f"{slot} 槽不接受 {gtype} 类型部件")
        self.dirty = True
        # 复用分配逻辑 (旧几何排除 + 集/显示维护)
        return self.cmd_assign_slot({"slot": slot, "geom_id": geom_id})

    def cmd_list_library(self, args: dict) -> dict:
        lib_dir = ROOT / "Data" / "Workshop" / "library"
        items = []
        for t in wc.LIBRARY_TYPES:
            d = lib_dir / t
            if not d.is_dir():
                continue
            for f in sorted(d.glob("*.vsp3")):
                meta = {}
                side = f.with_suffix(".part.json")
                if side.is_file():
                    try:
                        meta = json.loads(side.read_text(encoding="utf-8"))
                    except Exception:
                        meta = {}
                items.append({"type": t, "name": meta.get("name", f.stem),
                              "file": f.stem, "path": str(f), "meta": meta})
        return {"items": items}

    def cmd_save_part(self, args: dict) -> dict:
        geom_id, name, ptype = args["geom_id"], args["name"], args["type"]
        if ptype not in wc.LIBRARY_TYPES:
            raise ValueError(f"未知库类型: {ptype}")
        if geom_id not in [g["id"] for g in self._list_geoms()]:
            raise ValueError(f"未知几何: {geom_id}")
        lib_dir = ROOT / "Data" / "Workshop" / "library" / ptype
        lib_dir.mkdir(parents=True, exist_ok=True)
        # vsp 库是窄字符写盘, 非 ASCII 文件名会崩溃 → 落盘名 ASCII 化, 显示名存侧车
        safe = wc.ascii_name(name)
        out_vsp = lib_dir / f"{safe}.vsp3"
        # 快照-裁剪-另存-恢复 (避免动活动模型; W0 教训: 绝不拿空模型覆盖)
        # U7: 中段异常也恢复快照 (finally), 另存原子化 — 同 cmd_delete_hidden
        snap = ROOT / "work" / "workshop_snapshot.vsp3"
        vsp.WriteVSPFile(str(snap))
        try:
            keep = [g for g in vsp.FindGeoms() if g != geom_id]
            if keep:
                vsp.DeleteGeomVec(keep)
            # F1: 几何名不安全时才消毒改写 (安全名如 MainWing 原样保留,
            # 快照恢复后活动模型不受影响)
            cur_name = vsp.GetGeomName(geom_id)
            if not wc.geom_name_ok(cur_name):
                vsp.SetGeomName(geom_id, wc.geom_name_safe(name))
            vsp.Update()
            _write_vsp_atomic(out_vsp)
        finally:
            vsp.ClearVSPModel()
            vsp.ReadVSPFile(str(snap))   # 返回 None, 成败以几何数为准
            vsp.Update()
        if not vsp.FindGeoms():
            raise RuntimeError("快照恢复失败 (活动模型可能受损, 建议重新 open)")
        meta = {"type": ptype, "name": name, "file": safe,
                "saved_at": time.strftime("%Y-%m-%d %H:%M:%S")}
        (lib_dir / f"{safe}.part.json").write_text(
            json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8")
        return {"path": str(out_vsp), "meta": meta}


    # ---------- W2 编辑命令 ----------
    def cmd_add_part(self, args: dict) -> dict:
        slot = args["slot"]
        if slot not in self.slots:
            raise ValueError(f"未知槽位: {slot}")
        name = args.get("name") or None
        gid = wm.add_part(slot, name)
        self.dirty = True
        return self.cmd_assign_slot({"slot": slot, "geom_id": gid})

    def cmd_get_geom_summary(self, args: dict) -> dict:
        gid = args["geom_id"]
        if gid not in [g["id"] for g in self._list_geoms()]:
            raise ValueError(f"未知几何: {gid}")
        s = wm.get_geom_summary(gid)
        if s["type"] == "Wing":
            shell = vsp.GetParmVal(gid, "Shell_Flag", "Mass_Props") > 0.5
            if shell and s["name"] not in self.last_area:
                self.cmd_mass_props({})
            if shell:
                s["mass_kg"] = (self.last_area.get(s["name"], 0.0)
                                * vsp.GetParmVal(gid, "Mass_Area", "Mass_Props"))
            else:
                s["mass_kg"] = s["point_mass"] * (2.0 if s["symmetric"] else 1.0)
        s["last_volume_m3"] = self.last_volume.get(s["name"])
        return s

    def cmd_set_parms(self, args: dict) -> dict:
        """批量改参 (几何级 + XSec 级); UI 节流后合并发送。"""
        gid = args["geom_id"]
        if gid not in [g["id"] for g in self._list_geoms()]:
            raise ValueError(f"未知几何: {gid}")
        for p in args.get("parms") or []:
            # XForm 位置/旋转默认是相对模式 (*_Location/*_Rotation 相对系派生只读),
            # 写前切绝对模式 (W2/W3 实测; 审查 R05: 相对旋转在绝对模式下静默无效)
            if p.get("group", "") == "XForm" and (p["name"].endswith("_Location") or
                                                  p["name"].endswith("_Rotation")):
                vsp.SetParmValUpdate(gid, "Abs_Or_Relitive_flag", "XForm", 0.0)
            vsp.SetParmValUpdate(gid, p["name"], p.get("group", ""), float(p["value"]))
        for idx_s, plist in (args.get("xsec_parms") or {}).items():
            surf = vsp.GetXSecSurf(gid, 0)
            xs = vsp.GetXSec(surf, int(idx_s))
            for p in plist:
                pid = vsp.GetXSecParm(xs, p["name"])
                if not pid:
                    raise ValueError(f"XSec[{idx_s}] 无参数 {p['name']}")
                vsp.SetParmVal(pid, float(p["value"]))
        vsp.Update()
        self.dirty = True
        return {"geoms": self._list_geoms(), "slots": self.slots}

    def cmd_set_mass(self, args: dict) -> dict:
        gid = args["geom_id"]
        name = vsp.GetGeomName(gid)
        if vsp.GetGeomTypeName(gid) == "Wing":
            self.cmd_mass_props({})   # 浸润面积随几何变化, 每次按当前几何重算 (Mass / Mass_Area)
        r = wm.set_mass(gid, float(args["mass_kg"]), self.last_volume.get(name),
                        self.last_area.get(name))
        if not r.get("ok"):
            raise RuntimeError(r.get("error", "set_mass 失败"))
        self.dirty = True
        return r

    def cmd_fuselage_set_radii(self, args: dict) -> dict:
        """机身直径列表 (调用前 UI 已做转圆提示)。count 变化时先重采样保趋势。"""
        gid = args["geom_id"]
        diams = [float(d) for d in args["diameters"]]
        if "resample_to" in args and args["resample_to"]:
            diams = wm.resample_radii(diams, int(args["resample_to"]))
        wm.fuselage_set_radii(gid, diams)
        self.dirty = True
        return wm.get_geom_summary(gid)

    def cmd_set_airfoil(self, args: dict) -> dict:
        wm.set_airfoil(args["geom_id"], args["kind"], float(args.get("thick", 0.10)),
                       float(args.get("camber", 0.0)), float(args.get("camber_loc", 0.2)),
                       args.get("file", ""))
        self.dirty = True
        return wm.get_geom_summary(args["geom_id"])

    def cmd_list_airfoils(self, args: dict) -> dict:
        return {"items": wm.list_airfoils()}

    def cmd_upload_airfoil(self, args: dict) -> dict:
        src = Path(args["path"])
        ok, msg = wm.validate_airfoil(src)
        if not ok:
            return {"ok": False, "error": msg}
        dst_dir = ROOT / "Data" / "Workshop" / "airfoils"
        dst_dir.mkdir(parents=True, exist_ok=True)
        dst = dst_dir / (wc.ascii_name(src.stem) + ".dat")
        if dst.exists():
            return {"ok": False, "error": f"已存在同名翼型 {dst.stem}, 请先重命名"}
        shutil.copyfile(src, dst)
        return {"ok": True, "path": str(dst), "detail": msg}

    def cmd_add_control(self, args: dict) -> dict:
        r = wm.add_control(args["geom_id"], args["role"], float(args["eta_start"]),
                           float(args["eta_end"]), float(args["len_c"]),
                           args.get("user_name", ""))
        self.dirty = True
        r["summary"] = wm.get_geom_summary(args["geom_id"])
        return r

    def cmd_edit_control(self, args: dict) -> dict:
        r = wm.edit_control(args["geom_id"], args["subsurf_id"],
                            float(args["eta_start"]), float(args["eta_end"]),
                            float(args["len_c"]), args["role"],
                            args.get("user_name", ""))
        self.dirty = True
        r["summary"] = wm.get_geom_summary(args["geom_id"])
        return r

    def cmd_del_control(self, args: dict) -> dict:
        wm.del_control(args["geom_id"], args["subsurf_id"])
        self.dirty = True
        return {"summary": wm.get_geom_summary(args["geom_id"])}

    def cmd_set_symmetric(self, args: dict) -> dict:
        wm.set_symmetric(args["geom_id"], bool(args["on"]))
        self.dirty = True
        return {"geoms": self._list_geoms(), "slots": self.slots}

    def cmd_set_color(self, args: dict) -> dict:
        """OpenVSP 侧材质 (36 预设, 无自由 RGB — W0 结论); 真色存 workshop.json。"""
        gid = args["geom_id"]
        mat = args.get("material", "")
        names = list(vsp.GetMaterialNames())
        if mat not in names:
            raise ValueError(f"未知材质 {mat} (可用: {', '.join(names[:6])}...)")
        vsp.SetGeomMaterialName(gid, mat)
        vsp.Update()
        self.dirty = True
        return {"material": vsp.GetGeomMaterialName(gid)}

    def cmd_list_materials(self, args: dict) -> dict:
        return {"items": list(vsp.GetMaterialNames())}

    def cmd_wing_to_single_taper(self, args: dict) -> dict:
        gid = args["geom_id"]
        slot = self._geom_slot(gid)
        r = wm.wing_to_single_taper(gid)
        if r.get("changed"):
            self.dirty = True
            # 新几何入"未排除"态并接管槽位
            vsp.SetSetFlag(r["new_id"], wc.MASS_SET, True)
            vsp.Update()
            if slot:
                self.slots[slot] = None
                return self.cmd_assign_slot({"slot": slot, "geom_id": r["new_id"]})
            r["geoms"] = self._list_geoms()
            r["slots"] = self.slots
        return r

    def cmd_estimate_np(self, args: dict) -> dict:
        """气动中性点实时估算 (设计参考, 不参与飞控):
        机翼+平尾升力线面积加权 + 机身失稳修正 (经验公式)。
        Xnp ≈ (CLaW·SW·xACW + CLaT·ST·xACT·(1-dε/dα)) / (CLaW·SW + CLaT·ST·(1-dε/dα))
        机身修正: ΔX_np = -K_f × (d_f² × L_f) / (S_w × MAC_w), K_f ≈ 0.033 (Multhopp 汇总)
        2026-10-10 精度修复: 气动中心计入后掠/锥度 — AC 取 MAC 弦的 25% 弦点
        (x_LE 沿前缘后掠角走到 MAC 展向站位), 此前 AC 放根弦 25%, 对后掠翼 NP 偏前
        达 1 MAC (示例机 30° 后掠: 估算 0.782 vs VSPAERO 1.042)。6 机型回归:
        最大误差 0.40→0.11 m, 均值 -0.167→+0.058 m (下洗因子取 0.8)。"""
        def ac_x(gid: str, mac: float, span_half: float, root: float, tip: float) -> float:
            sweep = vsp.GetParmVal(gid, "Sweep", "XSec_1")
            sloc = vsp.GetParmVal(gid, "Sweep_Location", "XSec_1")
            lam = tip / root if root > 1e-9 else 1.0
            y_mac = span_half / 3 * (1 + 2 * lam) / (1 + lam)
            # 前缘后掠角: 由给定站位 (sloc) 的后掠角与弦长线性变化率换算
            tan_le = math.tan(math.radians(sweep)) + sloc * (root - tip) / max(span_half, 1e-9)
            return vsp.GetParmVal(gid, "X_Location", "XForm") + tan_le * y_mac + 0.25 * mac

        wing = self.slots.get("wing1")
        htail = self.slots.get("htail")
        if not wing:
            # 槽位未恢复 (刚 open) 时回落: 最大半展的 Wing 作主翼, 位置最靠后的作平尾
            wings = [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) == "Wing"]
            if not wings:
                raise RuntimeError("模型中无机翼, 无法估算中性点")
            wing = max(wings, key=lambda g: vsp.GetParmVal(g, "Span", "XSec_1"))
            rearmost = max(wings, key=lambda g: vsp.GetParmVal(g, "X_Location", "XForm"))
            if rearmost != wing:
                htail = rearmost
        wing_span = vsp.GetParmVal(wing, "Span", "XSec_1")
        wing_root = vsp.GetParmVal(wing, "Root_Chord", "XSec_1")
        wing_tip = vsp.GetParmVal(wing, "Tip_Chord", "XSec_1")
        sw = (wing_root + wing_tip) / 2 * wing_span * 2
        mac_w = 2 / 3 * (wing_root * wing_root + wing_root * wing_tip + wing_tip * wing_tip) \
            / (wing_root + wing_tip)
        ar_w = (2 * wing_span) ** 2 / sw if sw > 1e-9 else 1.0
        claw = 2 * math.pi * ar_w / (ar_w + 2)     # 升力线斜率 (per rad)
        x_ac_w = ac_x(wing, mac_w, wing_span, wing_root, wing_tip)
        num, den = claw * sw * x_ac_w, claw * sw
        if htail:
            t_span = vsp.GetParmVal(htail, "Span", "XSec_1")
            t_root = vsp.GetParmVal(htail, "Root_Chord", "XSec_1")
            t_tip = vsp.GetParmVal(htail, "Tip_Chord", "XSec_1")
            st = (t_root + t_tip) / 2 * t_span * 2
            ar_t = (2 * t_span) ** 2 / st if st > 1e-9 else 1.0
            clat = 2 * math.pi * ar_t / (ar_t + 2)
            t_lam = t_tip / t_root if t_root > 1e-9 else 1.0
            t_mac = 2 / 3 * t_root * (1 + t_lam + t_lam * t_lam) / (1 + t_lam)
            x_ac_t = ac_x(htail, t_mac, t_span, t_root, t_tip)
            downwash = 0.8                          # (1 - dε/dα) 常数近似 (6 机型回归最优)
            num += clat * st * x_ac_t * downwash
            den += clat * st * downwash
        if den < 1e-9:
            raise RuntimeError("估算退化 (面积和为 0)")
        xnp = num / den

        # 机身失稳修正 (经验公式): ΔX_np = -K_f × (d²L) / (S_w × MAC)
        fuse = self.slots.get("fuselage")
        if not fuse:
            fuses = [g for g in vsp.FindGeoms()
                     if vsp.GetGeomTypeName(g) in ("Fuselage", "Pod")]
            fuse = fuses[0] if fuses else None
        fuse_shift = 0.0
        fuse_len = 0.0
        fuse_dia = 0.0
        if fuse:
            fuse_len = vsp.GetParmVal(fuse, "Length", "Design")
            # 取最大截面直径 (圆形 = Circle_Diameter; 椭圆 = max(H,W))
            surf = vsp.GetXSecSurf(fuse, 0)
            for i in range(int(vsp.GetNumXSec(surf))):
                xs = vsp.GetXSec(surf, i)
                d = 0.0
                p = vsp.GetXSecParm(xs, "Circle_Diameter")
                if p:
                    d = vsp.GetParmVal(p)
                else:
                    ph = vsp.GetXSecParm(xs, "Ellipse_Height")
                    pw = vsp.GetXSecParm(xs, "Ellipse_Width")
                    h = vsp.GetParmVal(ph) if ph else 0.0
                    w = vsp.GetParmVal(pw) if pw else 0.0
                    d = max(h, w)
                if d > fuse_dia:
                    fuse_dia = d
            if fuse_dia > 1e-4 and fuse_len > 1e-4 and sw > 1e-4 and mac_w > 1e-4:
                k_f = 0.033   # Multhopp 汇总经验常数
                fuse_shift = -k_f * (fuse_dia * fuse_dia * fuse_len) / (sw * mac_w)
                xnp += fuse_shift

        return {"xnp_m": xnp, "method": "estimate",
                "sref_m2": sw, "bref_m": wing_span * 2, "mac_m": mac_w,
                "components": {"wing_x": x_ac_w, "tail_x": x_ac_t if htail else None,
                               "fuse_shift": fuse_shift, "fuse_len": fuse_len,
                               "fuse_dia": fuse_dia}}

    def cmd_delete_hidden(self, args: dict) -> dict:
        """R06: 在副本 vsp3 上物理删除隐藏 (排除) 几何, 供换机管线只用活动件。
        快照-加载-删-存回-恢复活动模型 (不动工坊本体, 同 save_part 骨架)。
        U7 加固: 中段任何异常也恢复快照 (finally — 否则裁剪后的副本留在内存,
        下一次 save 会把它写进用户工程); 副本写盘原子化 (tmp+replace, taskkill
        杀在写盘中途不再留截断文件); 同名部件分摊 map 合计质量 (防总质量翻倍);
        多段机翼 CGy 展开用总半展 (单段口径对多段会低估 Ixx)。"""
        p = Path(args["path"])
        if not p.is_file():
            raise FileNotFoundError(f"副本不存在: {p}")
        snap = ROOT / "work" / "delete_hidden_snap.vsp3"
        vsp.WriteVSPFile(str(snap))
        removed, nrm = 0, 0
        try:
            vsp.ClearVSPModel()
            vsp.ReadVSPFile(str(p))
            vsp.Update()
            hidden = [g for g in vsp.FindGeoms()
                      if vsp.GetGeomTypeName(g) != "Mesh" and not vsp.GetSetFlag(g, wc.SET_SHOWN)]
            if hidden:
                vsp.DeleteGeomVec(hidden)
                removed = len(hidden)
            # 质量口径统一: 全部部件按工坊 mass_props 的部件质量烘为纯 PointMass。
            # 工坊 Shell 面密度和 Density 体积质量在管线 tess 统一后均有偏差
            # (Shell 丢失, Density×体积随 tess 变化), 纯 PM 不受 tess 影响。
            mass_map = {str(k): float(v) for k, v in (args.get("mass_map") or {}).items()}
            targets = [g for g in vsp.FindGeoms()
                       if vsp.GetGeomTypeName(g) in ("Wing", "Pod", "Fuselage")]
            name_count: dict[str, int] = {}
            for g in targets:
                nm_ = vsp.GetGeomName(g)
                name_count[nm_] = name_count.get(nm_, 0) + 1
            for g in targets:
                gname = vsp.GetGeomName(g)
                pm_total = mass_map.get(gname)
                if pm_total is None or pm_total <= 1e-6:
                    continue
                # 工坊合成质量含对称片 (表+PM×片数): PM 设单侧值, 管线对称展开后总量
                # 一致; 同名部件 (报告表同名行) 按份数分摊合计质量, 总量不翻倍
                sym = 2.0 if vsp.GetParmVal(vsp.FindParm(g, "Sym_Planar_Flag", "Sym")) > 0.5 else 1.0
                pm = pm_total / (sym * max(1, name_count[gname]))
                vsp.SetParmValUpdate(g, "PointMass", "Mass_Props", pm)
                vsp.SetParmValUpdate(g, "Density", "Mass_Props", 0.0)
                if vsp.GetGeomTypeName(g) == "Fuselage":
                    vsp.SetParmValUpdate(g, "CGx", "Mass_Props", 0.0)
                    vsp.SetParmValUpdate(g, "CGy", "Mass_Props", 0.0)
                    vsp.SetParmValUpdate(g, "CGz", "Mass_Props", 0.0)
                sp = vsp.FindParm(g, "Shell_Flag", "Mass_Props")
                if sp:
                    vsp.SetParmValUpdate(sp, 0.0)
                # PM 的 CG 展开到半展 1/2 处 (默认堆在几何原点会使 Ixx≈0 → 滚转数值发散)
                span_half = wm.wing_total_span(g) if vsp.GetGeomTypeName(g) == "Wing" else 0.0
                vsp.SetParmValUpdate(g, "CGy", "Mass_Props", span_half * 0.5)
                nrm += 1
            if hidden or nrm:
                vsp.Update()
                _write_vsp_atomic(p)
        finally:
            vsp.ClearVSPModel()
            vsp.ReadVSPFile(str(snap))
            vsp.Update()
        if not [g for g in vsp.FindGeoms() if vsp.GetGeomTypeName(g) != "Mesh"]:
            raise RuntimeError("快照恢复失败 (活动模型可能受损, 建议重新 open)")
        return {"removed": removed, "mass_normalized": nrm, "path": str(p)}

    def cmd_load_example(self, args: dict) -> dict:
        ids = wm.load_example()
        self.slots = {s: None for s in wc.SLOTS}
        self.work_name = "示例机"
        self.dirty = True
        out = {"placed": {}}
        for slot, gid in ids.items():
            r = self.cmd_assign_slot({"slot": slot, "geom_id": gid})
            out["placed"][slot] = gid
        out["geoms"] = self._list_geoms()
        out["slots"] = self.slots
        out["name"] = self.work_name
        return out


COMMANDS = {
    "ping": Worker.cmd_ping,
    "new": Worker.cmd_new,
    "open": Worker.cmd_open,
    "save": Worker.cmd_save,
    "list_geoms": Worker.cmd_list_geoms,
    "assign_slot": Worker.cmd_assign_slot,
    "auto_assign": Worker.cmd_auto_assign,
    "clear_slot": Worker.cmd_clear_slot,
    "export_meshes": Worker.cmd_export_meshes,
    "mass_props": Worker.cmd_mass_props,
    "list_library": Worker.cmd_list_library,
    "insert_part": Worker.cmd_insert_part,
    "save_part": Worker.cmd_save_part,
    # W2 编辑
    "add_part": Worker.cmd_add_part,
    "get_geom_summary": Worker.cmd_get_geom_summary,
    "set_parms": Worker.cmd_set_parms,
    "set_mass": Worker.cmd_set_mass,
    "fuselage_set_radii": Worker.cmd_fuselage_set_radii,
    "set_airfoil": Worker.cmd_set_airfoil,
    "list_airfoils": Worker.cmd_list_airfoils,
    "upload_airfoil": Worker.cmd_upload_airfoil,
    "add_control": Worker.cmd_add_control,
    "edit_control": Worker.cmd_edit_control,
    "del_control": Worker.cmd_del_control,
    "set_symmetric": Worker.cmd_set_symmetric,
    "set_color": Worker.cmd_set_color,
    "list_materials": Worker.cmd_list_materials,
    "wing_to_single_taper": Worker.cmd_wing_to_single_taper,
    "estimate_np": Worker.cmd_estimate_np,
    "delete_hidden": Worker.cmd_delete_hidden,
    "load_example": Worker.cmd_load_example,
}


def main() -> int:
    worker = Worker()
    log(f"worker ready python={sys.version.split()[0]} vsproot={wc.vsproot()}")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            send({"id": None, "ok": False, "error": f"请求不是合法 JSON: {e}"})
            continue
        rid, cmd = req.get("id"), req.get("cmd", "")
        if cmd == "shutdown":
            send({"id": rid, "ok": True, "data": {"bye": True}})
            return 0
        fn = COMMANDS.get(cmd)
        if not fn:
            send({"id": rid, "ok": False, "error": f"未知命令: {cmd}"})
            continue
        t0 = time.perf_counter()
        try:
            data = fn(worker, req.get("args") or {})
            send({"id": rid, "ok": True, "data": data,
                  "ms": round((time.perf_counter() - t0) * 1000)})
        except Exception as e:
            send({"id": rid, "ok": False, "error": f"{type(e).__name__}: {e}",
                  "trace": traceback.format_exc(limit=3)})
    return 0


if __name__ == "__main__":
    sys.exit(main())
