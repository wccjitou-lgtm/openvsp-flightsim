#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
test_workshop_worker.py — 机型工坊 worker 测试 (W1)

两层:
  纯逻辑层: 常量/协议 JSON 约定 (任何 Python 可跑, 随 149 项回归)
  集成层  : 真起 worker 子进程走全流程 (仅 Python 3.13 + OpenVSP 环境, 否则 skip)

集成全流程 (冒烟即测试): open TestUAV → 槽位分配 → 排除语义 → 分件 STL →
MassProp 质量/重心 → save/reopen 往返 → 部件入库 → shutdown。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "Tools"))

import workshop_common as wc  # noqa: E402


def find_python313() -> str | None:
    """定位能跑 import openvsp 的解释器 (worker 硬依赖 3.13)。"""
    cands = [
        os.path.expandvars(r"%LOCALAPPDATA%\Programs\Python\Python313\python.exe"),
        r"C:\Python313\python.exe",
    ]
    for c in cands:
        if c and Path(c).is_file():
            return c
    out = subprocess.run(["py", "-3.13", "-c", "import sys;print(sys.executable)"],
                         capture_output=True, text=True)
    if out.returncode == 0 and out.stdout.strip():
        return out.stdout.strip().splitlines()[-1]
    return None


class WorkerProc:
    """worker 子进程驱动: 请求-响应按 id 配对 (日志事件旁路收集)。"""

    def __init__(self, py: str):
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        self.proc = subprocess.Popen(
            [py, str(ROOT / "Tools" / "workshop_worker.py")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", env=env, cwd=str(ROOT))
        self._id = 0
        self.logs: list[str] = []

    def call(self, cmd: str, args: dict | None = None, expect_ok: bool = True) -> dict:
        self._id += 1
        req = {"id": self._id, "cmd": cmd, "args": args or {}}
        self.proc.stdin.write(json.dumps(req, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("worker 提前退出")
            d = json.loads(line)
            if d.get("event") == "log":
                self.logs.append(d["msg"])
                continue
            if d.get("id") == self._id:
                if expect_ok and not d.get("ok"):
                    raise AssertionError(f"{cmd} 失败: {d.get('error')}")
                return d
            # 乱序响应 (不应发生): 忽略

    def close(self) -> None:
        try:
            self.call("shutdown", expect_ok=False)
        except Exception:
            pass
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


# ============================ 纯逻辑层 ============================

class TestConstants(unittest.TestCase):
    def test_slots_definition(self):
        self.assertEqual(wc.SLOTS[0], "fuselage")
        self.assertEqual(len(wc.SLOTS), 6)
        for s, types in wc.SLOT_TYPES.items():
            self.assertTrue(types, f"{s} 槽类型集为空")

    def test_fuselage_slot_accepts_pod(self):
        # TestUAV 机身即 Pod (闭合实体), 机身槽必须兼容
        self.assertIn("Pod", wc.SLOT_TYPES["fuselage"])
        self.assertIn("Fuselage", wc.SLOT_TYPES["fuselage"])

    def test_vsproot_override(self):
        old = os.environ.get("VSPROOT")
        try:
            os.environ["VSPROOT"] = r"C:\temp\fakevsp"
            self.assertEqual(str(wc.vsproot()), r"C:\temp\fakevsp")
        finally:
            if old is None:
                os.environ.pop("VSPROOT", None)
            else:
                os.environ["VSPROOT"] = old

    def test_project_root(self):
        self.assertEqual(wc.project_root(), ROOT)
        self.assertTrue((ROOT / "Tools" / "import_vsp.py").is_file())

    def test_ascii_name_sanitize(self):
        self.assertEqual(wc.ascii_name("W1测试翼"), "W1___")
        self.assertEqual(wc.ascii_name("My Wing-2.vsp3"), "My_Wing-2.vsp3")
        self.assertEqual(wc.ascii_name("  "), "Part")
        self.assertNotRegex(wc.ascii_name("主翼A"), r"[^A-Za-z0-9_.\-]")

    def test_geom_name_safety(self):
        """F1: 几何名消毒 — '_' 与非 ASCII 会断裂 VSPAERO 操纵面 tag 匹配。"""
        self.assertEqual(wc.geom_name_safe("C_Wing"), "CWing")
        self.assertEqual(wc.geom_name_safe("Main Wing 1"), "MainWing1")
        self.assertEqual(wc.geom_name_safe("我的机翼"), "Part")
        self.assertEqual(wc.geom_name_safe(""), "Part")
        self.assertTrue(wc.geom_name_ok("ExampleWing"))
        self.assertFalse(wc.geom_name_ok("C_Wing"))
        self.assertFalse(wc.geom_name_ok("主机翼"))

    def test_scan_vsp3_geom_names(self):
        """F1: vsp3 几何名抽取 (每个 <Geom> 首个 ParmContainer 的 Name)。"""
        names = wc.scan_vsp3_geom_names(ROOT / "Data" / "TestUAV.vsp3")
        self.assertEqual(names, ["Fuselage", "MainWing", "HTail", "VTail"])
        self.assertEqual(wc.bad_geom_names(names), [])
        self.assertEqual(wc.bad_geom_names(["Good", "Bad_Name", "中文名"]),
                         ["Bad_Name", "中文名"])



# ============================ 集成层 (3.13 only) ============================

PY313 = find_python313()
try:
    OPENVSP_AVAILABLE = (wc.vsproot() / "python" / "openvsp").is_dir()
except FileNotFoundError:
    OPENVSP_AVAILABLE = False
WORKER_AVAILABLE = PY313 is not None and OPENVSP_AVAILABLE


@unittest.skipUnless(WORKER_AVAILABLE, "需要 Python 3.13 和已通过 VSPROOT 配置的 OpenVSP")
class TestWorkerEditing(unittest.TestCase):
    """W2 编辑命令: 默认部件/参数/质量/机身半径/翼型/操纵面/对称/示例机。"""

    @classmethod
    def setUpClass(cls):
        cls.w = WorkerProc(PY313)
        cls.w.call("new")

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def _summary(self, slot):
        gid = self.w.call("list_geoms")["data"]["slots"][slot]
        return self.w.call("get_geom_summary", {"geom_id": gid})["data"]

    def test_10_add_part_defaults(self):
        d = self.w.call("add_part", {"slot": "wing1"})["data"]
        self.assertIn("wing1", d["slots"])
        s = self._summary("wing1")
        self.assertAlmostEqual(s["span_half"], 1.0, places=3)
        self.assertAlmostEqual(s["root_chord"], 0.25, places=3)
        self.assertAlmostEqual(s["tip_chord"], 0.15, places=3)
        self.assertTrue(s["symmetric"])
        self.assertAlmostEqual(s["x"], 0.70, places=3)
        self.assertGreater(s["mass_kg"], 0.5)   # 壳体面密度 1.5 kg/m2 × 浸润面积 (双面对称)
        self.assertAlmostEqual(s["point_mass"], 0.0, places=6)
        # 机身默认 6 截面圆 + 等分
        d = self.w.call("add_part", {"slot": "fuselage"})["data"]
        s = self._summary("fuselage")
        self.assertEqual(s["num_xsecs"], 6)
        self.assertTrue(s["all_round_or_ellipse"])
        self.assertEqual(s["shapes"][0], 0, "截面0应为点 (XS_POINT)")
        self.assertEqual(s["diameters"][0], 0.0)
        self.assertEqual(s["tail_cap"], 1, "尾部应为平面封盖 (FLAT_END_CAP)")
        self.assertAlmostEqual(s["length"], 1.6, places=3)
        for i, x in enumerate(s["xloc_percents"]):
            self.assertAlmostEqual(x, i / 5, places=3)
        # 配重: PointMass 且默认不对称 (镜像双计防御)
        self.w.call("add_part", {"slot": "ballast"})
        s = self._summary("ballast")
        self.assertAlmostEqual(s["point_mass"], 0.2, places=4)

    def test_11_set_parms_roundtrip(self):
        s = self._summary("wing1")
        d = self.w.call("set_parms", {"geom_id": s["id"], "parms": [
            {"name": "Span", "group": "XSec_1", "value": 1.25},
            {"name": "Dihedral", "group": "XSec_1", "value": 5.0},
        ]})["data"]
        s2 = self._summary("wing1")
        self.assertAlmostEqual(s2["span_half"], 1.25, places=3)
        self.assertAlmostEqual(s2["dihedral"], 5.0, places=3)

    def test_12_wing_mass_and_symmetry(self):
        self.w.call("set_mass", {"geom_id": self._summary("wing1")["id"], "mass_kg": 0.9})
        self.w.call("mass_props")
        d = self.w.call("mass_props")["data"]
        wing = [v for k, v in d["per_geom"].items()][0]
        # 壳体质量: 设定值即整翼 (含两侧) 总质量
        self.assertAlmostEqual(wing["mass_kg"], 0.9, delta=0.02)
        # 关对称 → 面密度不变, 单片面积减半
        self.w.call("set_symmetric", {"geom_id": self._summary("wing1")["id"], "on": False})
        d = self.w.call("mass_props")["data"]
        wing = [v for k, v in d["per_geom"].items()][0]
        self.assertAlmostEqual(wing["mass_kg"], 0.45, delta=0.02)

    def test_13_fuselage_radii_resample_and_even(self):
        s = self._summary("fuselage")
        # 截面数 6 → 8 (分布趋势保留: 首末直径不变)
        d0 = s["diameters"]
        d = self.w.call("fuselage_set_radii", {
            "geom_id": s["id"], "diameters": d0, "resample_to": 8})["data"]
        self.assertEqual(d["num_xsecs"], 8)
        self.assertAlmostEqual(d["diameters"][0], d0[0], places=4)
        self.assertAlmostEqual(d["diameters"][-1], d0[-1], places=4)
        self.assertEqual(d["shapes"][0], 0, "改截面数后截面0仍为点")
        self.assertEqual(d["tail_cap"], 1)
        for i, x in enumerate(d["xloc_percents"]):
            self.assertAlmostEqual(x, i / 7, places=3)
        # 回 6
        self.w.call("fuselage_set_radii", {
            "geom_id": s["id"], "diameters": d["diameters"], "resample_to": 6})

    def test_14_fuselage_mass_via_density(self):
        self.w.call("mass_props")   # 缓存体积
        s = self._summary("fuselage")
        self.w.call("set_mass", {"geom_id": s["id"], "mass_kg": 2.0})
        d = self.w.call("mass_props")["data"]
        fus = d["per_geom"].get(s["name"])
        self.assertAlmostEqual(fus["mass_kg"], 2.0, delta=0.02)

    def test_15_airfoil_naca4_and_file(self):
        s = self._summary("wing1")
        self.w.call("set_airfoil", {"geom_id": s["id"], "kind": "naca4",
                                    "thick": 0.12, "camber": 0.02, "camber_loc": 0.4})
        s2 = self._summary("wing1")
        self.assertEqual(s2["airfoil"]["kind"], "naca4")
        self.assertAlmostEqual(s2["airfoil"]["thick"], 0.12, places=3)
        # 文件翼型: 用 OpenVSP 自带 Selig .dat
        items = self.w.call("list_airfoils")["data"]["items"]
        e205 = [i for i in items if i["name"] == "e205_Selig"]
        self.assertTrue(e205, "airfoil 目录里找不到 e205_Selig")
        self.w.call("set_airfoil", {"geom_id": s["id"], "kind": "file",
                                    "file": e205[0]["path"]})
        s3 = self._summary("wing1")
        self.assertEqual(s3["airfoil"]["kind"], "file")
        self.assertFalse(s3["multi_segment"])

    def test_16_upload_airfoil_validation(self):
        # 合法 Selig .dat → 复制进工坊库并出现在列表
        src = wc.vsproot() / "airfoil" / "e205_Selig.dat"
        d = self.w.call("upload_airfoil", {"path": str(src)})["data"]
        self.assertTrue(d["ok"])
        try:
            items = self.w.call("list_airfoils")["data"]["items"]
            self.assertTrue(any(i["source"] == "user" for i in items))
        finally:
            Path(d["path"]).unlink()
        # 非法: 随便写几行的假文件 → 明确报错 (data.ok=false + 中文原因)
        bad = ROOT / "work" / "bad_airfoil.dat"
        bad.write_text("fake\n1 2\n3 4\n", encoding="utf-8")
        try:
            d2 = self.w.call("upload_airfoil", {"path": str(bad)}, expect_ok=False)["data"]
            self.assertFalse(d2["ok"])
            self.assertIn("点", d2["error"])
        finally:
            bad.unlink()

    def test_17_control_surface_crud_and_clamp(self):
        gid = self._summary("wing1")["id"]
        d = self.w.call("add_control", {"geom_id": gid, "role": "aileron",
                                        "eta_start": 0.55, "eta_end": 0.90,
                                        "len_c": 0.25})["data"]
        self.assertEqual(d["name"], "Aileron")
        # 越界钳制: eta_end < eta_start 时拉到 start+ε; len_c 上限
        d2 = self.w.call("edit_control", {
            "geom_id": gid, "subsurf_id": d["id"], "eta_start": 0.7,
            "eta_end": 0.5, "len_c": 1.5, "role": "aileron"})["data"]
        self.assertGreaterEqual(d2["eta_end"], d2["eta_start"])
        self.assertLessEqual(d2["len_c"], 0.95)
        # flap 角色可自定义名, 不与三轴冲突
        d3 = self.w.call("add_control", {"geom_id": gid, "role": "flap",
                                         "eta_start": 0.1, "eta_end": 0.4,
                                         "len_c": 0.3, "user_name": "襟翼1"})["data"]
        self.assertEqual(d3["name"], "襟翼1")
        # 删除
        d4 = self.w.call("del_control", {"geom_id": gid, "subsurf_id": d3["id"]})["data"]
        names = [c["name"] for c in d4["summary"]["controls"]]
        self.assertNotIn("襟翼1", names)

    def test_18_load_example_and_roundtrip(self):
        d = self.w.call("load_example")["data"]
        geoms = {g["name"]: g for g in d["geoms"]}
        self.assertEqual(len(d["geoms"]), 5)
        subs = set()
        for g in d["geoms"]:
            subs |= {s["name"] for s in g["subsurfs"]}
        self.assertTrue({"Aileron", "Elevator", "Rudder"} <= subs)
        # 质量: 三轴部件 PointMass 对称合成 + 配重 0.2, 总量自洽
        m = self.w.call("mass_props")["data"]
        self.assertAlmostEqual(m["mass_kg"], sum(v["mass_kg"] for v in m["per_geom"].values()),
                               delta=0.02)
        self.assertGreater(m["mass_kg"], 1.0)
        # 保存重开: 舵面/参数/槽位(workshop.json 由 UI 写, 这里只验 vsp3)
        out = ROOT / "work" / "w2_example_roundtrip.vsp3"
        self.w.call("save", {"path": str(out)})
        d2 = self.w.call("open", {"path": str(out)})["data"]
        subs2 = set()
        for g in d2["geoms"]:
            subs2 |= {s["name"] for s in g["subsurfs"]}
        self.assertTrue({"Aileron", "Elevator", "Rudder"} <= subs2)
        # open 后槽位恢复由 UI 按 workshop.json 做 (W1 语义), 这里按名取几何
        g2 = {g["name"]: g for g in d2["geoms"]}
        s = self.w.call("get_geom_summary", {"geom_id": g2["ExampleWing"]["id"]})["data"]
        self.assertAlmostEqual(s["span_half"], 1.0, places=3)

    def test_19_vtail_symmetric_double(self):
        """双垂尾: 开对称后质量应接近单片的 2 倍 (镜像真实存在)。"""
        self.w.call("load_example")
        s = self._summary("vtail")
        # 垂尾在 y=0 时镜像片与原片重合, 必须偏置到 y≠0 才是真双垂尾
        self.w.call("set_parms", {"geom_id": s["id"], "parms": [
            {"name": "Y_Location", "group": "XForm", "value": 0.3}]})
        m1 = self.w.call("mass_props")["data"]["per_geom"][s["name"]]["mass_kg"]
        self.w.call("set_symmetric", {"geom_id": s["id"], "on": True})
        m2 = self.w.call("mass_props")["data"]["per_geom"][s["name"]]["mass_kg"]
        self.assertAlmostEqual(m2, m1 * 2.0, delta=m1 * 0.2 + 0.01)
        self.w.call("set_symmetric", {"geom_id": s["id"], "on": False})

    def test_20_wing_to_single_taper_noop(self):
        s = self._summary("wing1")
        d = self.w.call("wing_to_single_taper", {"geom_id": s["id"]})["data"]
        self.assertFalse(d["changed"])   # 默认翼已是单段

    def test_21_open_failure_preserves_model(self):
        """R01: 打开损坏文件必须保全当前模型/槽位/质量 (审查复现路径)。"""
        d = self.w.call("open", {"path": str(ROOT / "Data" / "TestUAV.vsp3")})["data"]
        g = {x["name"]: x for x in d["geoms"]}
        for slot, nm in [("fuselage", "Fuselage"), ("wing1", "MainWing"),
                         ("htail", "HTail"), ("vtail", "VTail")]:
            self.w.call("assign_slot", {"slot": slot, "geom_id": g[nm]["id"]})
        m_before = self.w.call("mass_props")["data"]["mass_kg"]

        bad = ROOT / "work" / "bad_model_r01.vsp3"
        bad.write_bytes(b"this is not a vsp3 file - corrupted payload for R01")
        try:
            resp = self.w.call("open", {"path": str(bad)}, expect_ok=False)
            self.assertFalse(resp["ok"])
            self.assertIn("恢复", resp["error"])
        finally:
            bad.unlink()

        d2 = self.w.call("list_geoms")["data"]
        self.assertEqual(len(d2["geoms"]), 4, "失败导入后原模型必须保全")
        names = {x["name"] for x in d2["geoms"]}
        self.assertTrue({"Fuselage", "MainWing", "HTail", "VTail"} <= names)
        name_of = {x["id"]: x["name"] for x in d2["geoms"]}
        for slot, nm in [("fuselage", "Fuselage"), ("wing1", "MainWing"),
                         ("htail", "HTail"), ("vtail", "VTail")]:
            gid2 = d2["slots"][slot]
            self.assertIsNotNone(gid2, f"{slot} 槽位不能悬空")
            self.assertEqual(name_of.get(gid2), nm, f"{slot} 槽位按名重映射失败")
        m_after = self.w.call("mass_props")["data"]["mass_kg"]
        self.assertAlmostEqual(m_after, m_before, delta=1e-6,
                               msg="恢复后质量应与此前一致")
        # 恢复后保存守卫不再触发 (模型非空), 可正常保存
        out = ROOT / "work" / "r01_recovered.vsp3"
        d3 = self.w.call("save", {"path": str(out)})["data"]
        self.assertTrue((ROOT / "work" / "r01_recovered.vsp3").stat().st_size > 10000)
        out.unlink()


@unittest.skipUnless(WORKER_AVAILABLE, "需要 Python 3.13 和已通过 VSPROOT 配置的 OpenVSP")
class TestWorkerIntegration(unittest.TestCase):
    """全流程冒烟: 用 Data/TestUAV.vsp3 (4 几何, 已知三舵面齐全)。"""

    @classmethod
    def setUpClass(cls):
        cls.w = WorkerProc(PY313)
        cls.r_ping = cls.w.call("ping")["data"]

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def test_01_open_testuav(self):
        d = self.w.call("open", {"path": str(ROOT / "Data" / "TestUAV.vsp3")})["data"]
        geoms = {g["name"]: g for g in d["geoms"]}
        self.assertIn("Fuselage", geoms)          # TestUAV 机身 (Pod 型)
        self.assertIn("MainWing", geoms)
        self.assertEqual(geoms["MainWing"]["type"], "Wing")
        subs = {s["name"] for s in geoms["MainWing"]["subsurfs"]}
        self.assertIn("Aileron", subs)
        self.assertIn("Elevator", {s["name"] for s in geoms["HTail"]["subsurfs"]})
        self.assertIn("Rudder", {s["name"] for s in geoms["VTail"]["subsurfs"]})
        self.assertTrue(all(not g["hidden"] for g in d["geoms"]))  # 导入后全部可见

    def test_02_assign_and_exclusion(self):
        d = self.w.call("list_geoms")["data"]
        geoms = {g["name"]: g for g in d["geoms"]}
        for slot, name in [("fuselage", "Fuselage"), ("wing1", "MainWing"),
                           ("htail", "HTail"), ("vtail", "VTail")]:
            d2 = self.w.call("assign_slot", {"slot": slot, "geom_id": geoms[name]["id"]})["data"]
            m = {g["name"]: g for g in d2["geoms"]}
            self.assertEqual(m[name]["slot"], slot)
        # 同一几何进第二槽必须被拒 (W1 无独立副本)
        resp = self.w.call("assign_slot", {"slot": "wing2", "geom_id": geoms["MainWing"]["id"]},
                           expect_ok=False)
        self.assertFalse(resp["ok"])
        # 类型不符必须被拒
        resp = self.w.call("assign_slot", {"slot": "ballast", "geom_id": geoms["MainWing"]["id"]},
                           expect_ok=False)
        self.assertFalse(resp["ok"])
        # 清槽 = 排除 (隐藏 + 不再出现在质量/网格)
        d3 = self.w.call("clear_slot", {"slot": "vtail"})["data"]
        m3 = {g["name"]: g for g in d3["geoms"]}
        self.assertTrue(m3["VTail"]["hidden"])
        # 恢复
        self.w.call("assign_slot", {"slot": "vtail", "geom_id": geoms["VTail"]["id"]})

    def test_03_export_meshes_per_slot(self):
        d = self.w.call("export_meshes")["data"]
        meshes = d["meshes"]
        self.assertEqual(set(meshes), {"fuselage", "wing1", "htail", "vtail"})
        for slot, info in meshes.items():
            p = Path(info["path"])
            self.assertTrue(p.is_file(), f"{slot} STL 未产出")
            self.assertGreater(info["tris"], 0)
        # 清槽后导出: 该槽不再出现
        self.w.call("clear_slot", {"slot": "htail"})
        d2 = self.w.call("export_meshes")["data"]
        self.assertNotIn("htail", d2["meshes"])
        self.assertIn("wing1", d2["meshes"])

    def test_04_mass_props(self):
        d = self.w.call("mass_props")["data"]
        self.assertGreater(d["mass_kg"], 1.0)          # TestUAV 全重 ~14.8 kg
        self.assertLess(d["mass_kg"], 100.0)
        self.assertEqual(len(d["cg_m"]), 3)
        self.assertTrue(d["per_geom"], "按部件质量表为空")
        # 排除语义: 清掉机身后总质量显著下降, 再恢复
        before = self.w.call("mass_props")["data"]["mass_kg"]
        g = {x["name"]: x for x in self.w.call("list_geoms")["data"]["geoms"]}
        fus = g["Fuselage"]["id"]
        self.w.call("clear_slot", {"slot": "fuselage"})
        after = self.w.call("mass_props")["data"]["mass_kg"]
        self.assertLess(after, before * 0.9, "机身排除后总质量未下降")
        self.w.call("assign_slot", {"slot": "fuselage", "geom_id": fus})
        restored = self.w.call("mass_props")["data"]["mass_kg"]
        self.assertAlmostEqual(restored, before, delta=max(1e-6, before * 1e-6))

    def test_05_save_reopen_roundtrip(self):
        out = ROOT / "work" / "w1_test_roundtrip.vsp3"
        d = self.w.call("save", {"path": str(out)})["data"]
        self.assertTrue(Path(d["path"]).is_file())
        d2 = self.w.call("open", {"path": str(out)})["data"]
        names = {g["name"] for g in d2["geoms"]}
        self.assertTrue({"Fuselage", "MainWing", "HTail", "VTail"} <= names)
        subs = set()
        for g in d2["geoms"]:
            subs |= {s["name"] for s in g["subsurfs"]}
        self.assertTrue({"Aileron", "Elevator", "Rudder"} <= subs, f"舵面丢失: {subs}")

    def test_06_save_part_to_library(self):
        g = {x["name"]: x for x in self.w.call("list_geoms")["data"]["geoms"]}
        # 中文名落盘自动 ASCII 化 (vsp 窄字符写盘), 显示名存 part.json
        d = self.w.call("save_part", {"geom_id": g["MainWing"]["id"], "name": "W1测试翼",
                                      "type": "wing"})["data"]
        self.assertTrue(Path(d["path"]).is_file())
        self.assertNotIn("测", Path(d["path"]).name)
        self.assertEqual(d["meta"]["name"], "W1测试翼")
        lib = self.w.call("list_library")["data"]["items"]
        entry = [i for i in lib if i["name"] == "W1测试翼" and i["type"] == "wing"]
        self.assertEqual(len(entry), 1)
        # 快照恢复后活动模型不应受损
        d2 = self.w.call("list_geoms")["data"]
        self.assertEqual(len(d2["geoms"]), 4)
        # 清理测试产物
        Path(d["path"]).unlink()
        Path(d["path"]).with_suffix(".part.json").unlink()

    def test_07_new_clears(self):
        d = self.w.call("new")["data"]
        self.assertEqual(d["geoms"], [])
        self.assertTrue(all(v is None for v in d["slots"].values()))

    def test_08_insert_part_from_library(self):
        # 用 test_06 同款路径: 先存一个机翼部件, 再 new 空工程 → insert_part 实例化
        g = {x["name"]: x for x in self.w.call("open", {
            "path": str(ROOT / "Data" / "TestUAV.vsp3")})["data"]["geoms"]}
        d = self.w.call("save_part", {"geom_id": g["MainWing"]["id"], "name": "W1插入翼",
                                      "type": "wing"})["data"]
        part_path = d["path"]
        try:
            self.w.call("new")
            d2 = self.w.call("insert_part", {"slot": "wing1", "path": part_path})["data"]
            self.assertEqual(len(d2["geoms"]), 1, "空工程插入后应恰有 1 个几何")
            self.assertEqual(d2["geoms"][0]["slot"], "wing1")
            self.assertEqual(d2["geoms"][0]["name"], "MainWing")
            subs = {s["name"] for s in d2["geoms"][0]["subsurfs"]}
            self.assertIn("Aileron", subs, "部件应带子面 (Aileron) 入库并还原")
            # 类型不符的插入必须被回滚 (不留残件)
            resp = self.w.call("insert_part", {"slot": "ballast", "path": part_path},
                               expect_ok=False)
            self.assertFalse(resp["ok"])
            d3 = self.w.call("list_geoms")["data"]
            self.assertEqual(len(d3["geoms"]), 1, "类型不符回滚后不应残留几何")
        finally:
            Path(part_path).unlink()
            Path(part_path).with_suffix(".part.json").unlink()


@unittest.skipUnless(WORKER_AVAILABLE, "需要 Python 3.13 和已通过 VSPROOT 配置的 OpenVSP")
class TestAutoAssign(unittest.TestCase):
    """F4: 无 sidecar 导入时的启发式槽位预分配 (独立 worker, 不污染其它测试)。"""

    @classmethod
    def setUpClass(cls):
        cls.w = WorkerProc(PY313)

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def test_01_auto_assign_testuav(self):
        self.w.call("open", {"path": str(ROOT / "Data" / "TestUAV.vsp3")})
        d = self.w.call("auto_assign")["data"]
        a = d["assigned"]
        # TestUAV: Fuselage / MainWing / HTail / VTail → 机身/机翼1/平尾/垂尾
        self.assertEqual(a.get("fuselage"), "Fuselage")
        self.assertEqual(a.get("wing1"), "MainWing")
        self.assertEqual(a.get("htail"), "HTail")
        self.assertEqual(a.get("vtail"), "VTail")
        slots = d["slots"]
        for s in ("fuselage", "wing1", "htail", "vtail"):
            self.assertIsNotNone(slots[s], f"{s} 应被预分配")

    def test_02_auto_assign_skips_when_slots_taken(self):
        # test_01 已分配 — 再调用不得覆盖
        d = self.w.call("auto_assign")["data"]
        self.assertTrue(d.get("skipped"))
        self.assertEqual(d["assigned"], {})


@unittest.skipUnless(WORKER_AVAILABLE, "需要 Python 3.13 和已通过 VSPROOT 配置的 OpenVSP")
class TestSlotReplace(unittest.TestCase):
    """U16: 新建/替代后原部件仍在几何清单 (hidden, 无槽位), 重新分配可恢复。
    下拉框「现有部件」分组依赖这套语义 (当前✓/可用/已停用)。"""

    @classmethod
    def setUpClass(cls):
        cls.w = WorkerProc(PY313)

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def _geom(self, name):
        for g in self.w.call("list_geoms")["data"]["geoms"]:
            if g["name"] == name:
                return g
        return None

    def test_01_new_part_hides_but_keeps_old(self):
        self.w.call("load_example")
        old = self._geom("ExampleWing")
        self.assertIsNotNone(old)
        self.assertEqual(old["slot"], "wing1")
        # 新建默认机翼入 wing1 槽 (替代原机翼)
        self.w.call("add_part", {"slot": "wing1"})
        old2 = self._geom("ExampleWing")
        self.assertIsNotNone(old2, "被替代部件必须从几何清单保留 (下拉框可见)")
        self.assertTrue(old2["hidden"], "被替代部件应隐藏")
        self.assertIsNone(old2["slot"], "被替代部件应无槽位")
        cur = [g for g in self.w.call("list_geoms")["data"]["geoms"]
               if g["slot"] == "wing1"]
        self.assertEqual(len(cur), 1)
        self.assertNotEqual(cur[0]["name"], "ExampleWing")

    def test_02_reassign_restores_replaced_part(self):
        old = self._geom("ExampleWing")
        self.w.call("assign_slot", {"slot": "wing1", "geom_id": old["id"]})
        g = self._geom("ExampleWing")
        self.assertFalse(g["hidden"], "重新分配后应恢复可见")
        self.assertEqual(g["slot"], "wing1")
        # 新任部件反过来被停用
        others = [x for x in self.w.call("list_geoms")["data"]["geoms"]
                  if x["slot"] == "wing1"]
        self.assertEqual(len(others), 1)
        self.assertEqual(others[0]["name"], "ExampleWing")

    def test_03_clear_slot_parks_part(self):
        self.w.call("clear_slot", {"slot": "wing1"})
        g = self._geom("ExampleWing")
        self.assertTrue(g["hidden"])
        self.assertIsNone(g["slot"])



if __name__ == "__main__":
    unittest.main()


def _stl_bbox(path):
    """读 ASCII STL 顶点包围盒 (min/max 各 3 元)。"""
    mn = [float("inf")] * 3
    mx = [float("-inf")] * 3
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            tok = line.split()
            if tok and tok[0] == "vertex":
                v = [float(t) for t in tok[1:4]]
                for i in range(3):
                    mn[i] = min(mn[i], v[i])
                    mx[i] = max(mx[i], v[i])
    return mn, mx


@unittest.skipUnless(WORKER_AVAILABLE, "需要 Python 3.13 和已通过 VSPROOT 配置的 OpenVSP")
class TestReviewFixF3(unittest.TestCase):
    """审查修复 F3: R04 分件互异 / R05 绝对姿态 / R06 排除权威化。"""

    @classmethod
    def setUpClass(cls):
        cls.w = WorkerProc(PY313)
        d = cls.w.call("open", {"path": str(ROOT / "Data" / "TestUAV.vsp3")})["data"]
        g = {x["name"]: x for x in d["geoms"]}
        for slot, nm in [("fuselage", "Fuselage"), ("wing1", "MainWing"),
                         ("htail", "HTail"), ("vtail", "VTail")]:
            cls.w.call("assign_slot", {"slot": slot, "geom_id": g[nm]["id"]})

    @classmethod
    def tearDownClass(cls):
        cls.w.close()

    def test_r04_each_slot_stl_distinct(self):
        d = self.w.call("export_meshes")["data"]
        boxes = {}
        for slot, info in d["meshes"].items():
            mn, mx = _stl_bbox(info["path"])
            boxes[slot] = (round(mx[0] - mn[0], 2), round(mx[1] - mn[1], 2),
                           round(mx[2] - mn[2], 2))
        # 主翼/平尾 Y 向最宽, 机身 X 向最长, 垂尾 Z 向高 — 四件互异 (审查 R04 复现口径)
        self.assertGreater(boxes["wing1"][1], boxes["wing1"][2] * 2, "主翼应展向宽")
        self.assertGreater(boxes["htail"][1], boxes["htail"][2] * 2, "平尾应展向宽")
        self.assertGreater(boxes["fuselage"][0], boxes["fuselage"][1] * 2, "机身应细长")
        self.assertGreater(boxes["vtail"][2], boxes["vtail"][1], "垂尾应竖直")
        sigs = set(boxes.values())
        self.assertEqual(len(sigs), 4, f"四件包围盒应互异: {boxes}")

    def test_r05_vtail_absolute_rotation(self):
        self.w.call("new")
        self.w.call("add_part", {"slot": "vtail"})
        s = self.w.call("list_geoms")["data"]
        gid = s["slots"]["vtail"]
        summ = self.w.call("get_geom_summary", {"geom_id": gid})["data"]
        self.assertAlmostEqual(summ["rot_x"], 90.0, places=2,
                               msg="默认垂尾必须绝对旋转 90° (R05)")
        # 安装角 (Y_Rotation 绝对) 生效
        self.w.call("set_parms", {"geom_id": gid, "parms": [
            {"name": "Y_Rotation", "group": "XForm", "value": 12.0}]})
        summ2 = self.w.call("get_geom_summary", {"geom_id": gid})["data"]
        self.assertAlmostEqual(summ2["rot_y"], 12.0, places=2, msg="安装角必须生效 (R05)")
        # 垂尾网格竖直 (R05 端到端: Z 向 > Y 向)
        self.w.call("export_meshes")
        d = self.w.call("export_meshes")["data"]
        mn, mx = _stl_bbox(d["meshes"]["vtail"]["path"])
        self.assertGreater(mx[2] - mn[2], mx[1] - mn[1], "垂尾 STL 应竖直 (R05)")

    def test_r06_excluded_roundtrip_and_delete_hidden(self):
        self.w.call("new")
        self.w.call("load_example")
        g = {x["name"]: x for x in self.w.call("list_geoms")["data"]["geoms"]}
        m_before = self.w.call("mass_props")["data"]["mass_kg"]
        self.w.call("clear_slot", {"slot": "htail"})     # 排除平尾
        m_excl = self.w.call("mass_props")["data"]["mass_kg"]
        self.assertLess(m_excl, m_before * 0.95, "排除平尾后质量应明显下降")
        # 保存 → open 带 excluded_names → 排除状态必须保持
        out = ROOT / "work" / "r06_round.vsp3"
        self.w.call("save", {"path": str(out)})
        d = self.w.call("open", {"path": str(out),
                                 "excluded_names": ["ExampleHTail"]})["data"]
        hidden = {x["name"]: x["hidden"] for x in d["geoms"]}
        self.assertTrue(hidden.get("ExampleHTail", False), "重开后平尾必须保持排除 (R06)")
        m_re = self.w.call("mass_props")["data"]["mass_kg"]
        self.assertAlmostEqual(m_re, m_excl, delta=max(1e-6, m_excl * 1e-4))
        # delete_hidden: 副本物理删除排除件
        d2 = self.w.call("delete_hidden", {"path": str(out)})["data"]
        self.assertGreaterEqual(d2["removed"], 1)
        d3 = self.w.call("open", {"path": str(out)})["data"]
        names = {x["name"] for x in d3["geoms"]}
        self.assertNotIn("ExampleHTail", names, "副本不应再含排除件 (R06)")
        # 活动模型仍是重开态 (delete_hidden 不破坏本体)
        out.unlink()


@unittest.skipUnless(WORKER_AVAILABLE, "需要 Python 3.13 和已通过 VSPROOT 配置的 OpenVSP")
class TestElevatorMirrorGain(unittest.TestCase):
    """工坊命名的 Elevator 经 prep 后镜像侧增益必须为 -1 (否则 Cmde≈0)。"""

    def test_prep_sets_elevator_mirror_gain(self):
        import re
        import tempfile

        import import_vsp as iv

        w = WorkerProc(PY313)
        try:
            w.call("load_example")
            with tempfile.TemporaryDirectory() as td:
                src = Path(td) / "Ex.vsp3"
                w.call("save", {"path": str(src)})
                work = Path(td) / "work"
                work.mkdir()
                prep = work / "prep.as"
                prep.write_text(iv.render_prep(src, "Ex", work, False, 0, "thin"),
                                encoding="utf-8")
                rc, log = iv.run_vspscript(None, prep, work, work / "prep.log")
                self.assertIn("[PREP_OK]", log, f"rc={rc}; log tail={log[-800:]}")
                text = (work / "Ex_normalized.vsp3").read_text(encoding="utf-8", errors="replace")
        finally:
            w.close()
        ids = re.findall(r"<ID>(\w+)</ID>\s*<Name>Elevator</Name>", text)
        self.assertTrue(ids, "归一化模型里找不到 Elevator 子面")
        gains = [float(m) for i in ids for m in re.findall(
            rf'<Surf_{i}_1_Gain Value="([^"]+)"', text)]
        self.assertTrue(gains, "找不到 Elevator 的镜像侧增益参数")
        self.assertTrue(all(abs(g + 1.0) < 1e-9 for g in gains), f"镜像增益应为 -1: {gains}")
