#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
run_all_analyses.py — Phase 0 全流程驱动器

流程 (每个 VSPAERO 工况一个独立 vspscript 进程):
  1. build_model.as   -> TestUAV.vsp3 + TestUAV_MassProps.txt + TestUAV.stl
  2. run_case.as x6   -> STAB_DEFAULT / STAB_P / STAB_Q / STAB_R /
                         BETA_SWEEP / ALPHA_SWEEP
     每个进程结束后立即把输出改名保全 (VSPAERO 每次运行前会清掉上一轮输出):
       TestUAV.stab    -> TestUAV_default.stab
       TestUAV.pstab   -> TestUAV_p.pstab
       TestUAV.qstab   -> TestUAV_q.qstab
       TestUAV.rstab   -> TestUAV_r.rstab
       TestUAV.polar (beta 扫描)  -> TestUAV_beta.polar
       TestUAV.polar (alpha 扫描) -> TestUAV_alpha.polar

用法 (在 openvsp-flightsim/ 下):
  python Tools/run_all_analyses.py [--openvsp-dir <安装目录>] [--skip-build]
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

import workshop_common as wc

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "Data"
TOOLS = ROOT / "Tools"
BUNDLED = ROOT / "OpenVSP"
DEFAULT_OPENVSP = BUNDLED if (BUNDLED / "vspscript.exe").is_file() else None

MODEL = "TestUAV"

# (case 模式, 产出文件, 改名目标)
CASES = [
    ("STAB_DEFAULT", f"{MODEL}.stab",  f"{MODEL}_default.stab"),
    ("STAB_P",       f"{MODEL}.pstab", f"{MODEL}_p.pstab"),
    ("STAB_Q",       f"{MODEL}.qstab", f"{MODEL}_q.qstab"),
    ("STAB_R",       f"{MODEL}.rstab", f"{MODEL}_r.rstab"),
    ("BETA_SWEEP",   f"{MODEL}.polar", f"{MODEL}_beta.polar"),
    ("ALPHA_SWEEP",  f"{MODEL}.polar", f"{MODEL}_alpha.polar"),
]


def run_vspscript(openvsp_dir: Path, script: Path, log_name: str) -> None:
    """在 Data/ 目录下运行 vspscript 批处理脚本 (输出文件落在 CWD)。"""
    exe = openvsp_dir / "vspscript.exe"
    if not exe.is_file():
        raise FileNotFoundError(f"未找到 {exe}")
    if not script.is_file():
        raise FileNotFoundError(f"未找到 {script}")

    log_path = DATA / log_name
    print(f"[run] {script.name} -> 日志 {log_path}")
    with open(log_path, "w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.run(
            [str(exe), "-script", str(script)],
            cwd=str(DATA),
            stdout=log,
            stderr=subprocess.STDOUT,
            timeout=1800,  # 单步最长 30 分钟 (非定常分析较慢)
        )
    if proc.returncode != 0:
        # 输出日志末尾帮助定位
        tail = log_path.read_text(encoding="utf-8", errors="replace")[-2000:]
        raise RuntimeError(f"{script.name} 退出码 {proc.returncode}\n--- 日志末尾 ---\n{tail}")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Phase 0 全流程驱动器")
    ap.add_argument("--openvsp-dir", type=Path, default=DEFAULT_OPENVSP,
                    help="OpenVSP 安装目录 (含 vspscript.exe)；也可用 VSPROOT 环境变量")
    ap.add_argument("--skip-build", action="store_true", help="跳过建模步骤 (已有时)")
    args = ap.parse_args(argv)
    if args.openvsp_dir is None:
        args.openvsp_dir = wc.vsproot()

    DATA.mkdir(exist_ok=True)

    # ---- 1. 建模 + 质量属性 ----
    if not args.skip_build:
        run_vspscript(args.openvsp_dir, TOOLS / "build_model.as", "build_model.log")
        for f in ("TestUAV.vsp3", "TestUAV_MassProps.txt", "TestUAV.stl"):
            if not (DATA / f).is_file():
                print(f"错误: 建模步骤未产出 {f}", file=sys.stderr)
                return 1
        print("[ok] 模型 + 质量属性 + STL 完成")

    # ---- 2. 逐工况 VSPAERO ----
    failed: list[str] = []
    for mode, produced, kept in CASES:
        (DATA / "case.txt").write_text(mode, encoding="ascii")
        # 清掉上一轮可能残留的同名产物, 防止误判
        stale = DATA / kept
        if stale.is_file():
            stale.unlink()
        run_vspscript(args.openvsp_dir, TOOLS / "run_case.as", f"case_{mode}.log")
        src, dst = DATA / produced, DATA / kept
        if not src.is_file():
            print(f"错误: {mode} 未产出 {produced}", file=sys.stderr)
            failed.append(mode)
            continue
        shutil.move(str(src), str(dst))
        print(f"[ok] {mode}: {produced} -> {kept} ({dst.stat().st_size} bytes)")

    (DATA / "case.txt").unlink(missing_ok=True)

    if failed:
        print(f"\n失败工况: {failed}", file=sys.stderr)
        return 1
    print("\n全部工况完成。产物清单:")
    for _, _, kept in CASES:
        print(f"  Data/{kept}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
