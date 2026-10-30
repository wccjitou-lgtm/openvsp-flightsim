# -*- coding: utf-8 -*-
"""stl_to_obj_unity.py 分部件 (R4) 测试: 分组输出 / 索引全局性 / 回退路径 / 命名安全化。"""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "Tools"))
from stl_to_obj_unity import main, sanitize_group_name  # noqa: E402


def stl_text(tris):
    lines = ["solid test"]
    for tri in tris:
        lines.append("facet normal 0 0 1")
        lines.append("  outer loop")
        for v in tri:
            lines.append(f"    vertex {v[0]} {v[1]} {v[2]}")
        lines.append("  endloop")
        lines.append("endfacet")
    lines.append("endsolid test")
    return "\n".join(lines) + "\n"


TRI_A = ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
TRI_B = ((2.0, 0.0, 0.0), (3.0, 0.0, 0.0), (2.0, 1.0, 0.0))


class StlToObjGroupsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        (self.dir / "parts").mkdir()
        (self.dir / "parts" / "part_0.stl").write_text(stl_text([TRI_A]), encoding="utf-8")
        (self.dir / "parts" / "part_1.stl").write_text(stl_text([TRI_B]), encoding="utf-8")
        (self.dir / "parts" / "manifest.json").write_text(json.dumps([
            {"file": "part_0.stl", "name": "Fuselage"},
            {"file": "part_1.stl", "name": "MainWing"},
        ]), encoding="utf-8")
        self.whole = self.dir / "whole.stl"
        self.whole.write_text(stl_text([TRI_A, TRI_B]), encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def _run(self, out_name, extra=()):
        out = self.dir / out_name
        rc = main([str(self.whole), str(out)] + list(extra))
        self.assertEqual(rc, 0)
        return out.read_text(encoding="utf-8")

    def test_parts_mode_writes_two_named_groups(self):
        text = self._run("grouped.obj", ["--parts", str(self.dir / "parts")])
        groups = [l for l in text.splitlines() if l.startswith("o ")]
        self.assertEqual(groups, ["o Fuselage", "o MainWing"])
        # 三角形守恒: 2 in → 2 out; 顶点 6 (部件间不共享顶点)
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("f ")]), 2)
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("v ")]), 6)

    def test_parts_mode_face_indices_are_global(self):
        text = self._run("grouped.obj", ["--parts", str(self.dir / "parts")])
        faces = [tuple(int(x) for x in l.split()[1:])
                 for l in text.splitlines() if l.startswith("f ")]
        # 第 2 组的面索引应引用全局编号 (>3), 而不是组内从 1 重排
        self.assertTrue(all(i > 3 for i in faces[1]), faces)
        self.assertEqual(faces[0], (1, 3, 2))   # 绕序反转保持

    def test_single_mesh_mode_has_no_groups(self):
        text = self._run("single.obj")
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("o ")]), 0)
        # 单网格模式整机去重 (两个三角形无共享顶点 → 6 顶点 2 面)
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("v ")]), 6)
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("f ")]), 2)

    def test_single_valid_part_falls_back_to_single_mesh(self):
        # 只留一个部件 → 有效组 < 2 → 回退单网格 (与旧输出一致)
        (self.dir / "parts" / "part_1.stl").unlink()
        text = self._run("fallback.obj", ["--parts", str(self.dir / "parts")])
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("o ")]), 0)
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("f ")]), 2)

    def test_missing_parts_dir_falls_back(self):
        text = self._run("fallback2.obj", ["--parts", str(self.dir / "nope")])
        self.assertEqual(len([l for l in text.splitlines() if l.startswith("o ")]), 0)

    def test_sanitize_group_name(self):
        self.assertEqual(sanitize_group_name("主翼 左"), "____")
        self.assertEqual(sanitize_group_name("A-B_2.c"), "A-B_2.c")
        self.assertEqual(sanitize_group_name("  "), "Part")


if __name__ == "__main__":
    unittest.main()
