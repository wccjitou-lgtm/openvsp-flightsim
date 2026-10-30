# OpenVSP 工作流

[English version](../openvsp-workflow.md)

## 配置外部工具

安装 OpenVSP **3.51.3** 和与其绑定兼容的 Python **3.13**，运行工具前设置安装根目录：

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
```

不要把 OpenVSP 安装目录复制进仓库。其可执行文件、Python 绑定、库与数据仍遵循各自许可证。

## 重新生成 TestUAV 参考数据

在仓库根目录执行：

```bat
py -3.13 Tools/run_all_analyses.py
```

也可以显式传入路径：

```bat
py -3.13 Tools/run_all_analyses.py --openvsp-dir "%VSPROOT%"
```

该管线构建 `Data/TestUAV.vsp3`、计算质量属性，并运行六个 VSPAERO 工况：默认稳定性、p/q/r 角速率、beta 扫描与 alpha 扫描。耗时取决于机器和求解设置。

命令会写入并替换 `Data` 下的生成文件。建议在独立 worktree 中运行，或仔细审查产生的 diff。不要提交求解暂存文件、日志、`.vsptime`、`.adb`、`.vspaero` 以及 `.gitignore` 排除的其他中间产物。

需要时将导出的 STL 转成 Unity OBJ 布局：

```bat
py -3.13 Tools/stl_to_obj_unity.py Data/TestUAV.stl Data/Meshes/TestUAV.obj --config Data/AircraftConfig.json
```

配置生成器为 `Tools/generate_aircraft_config.py`；解析器测试和随附的 TestUAV 参考文件展示了预期 schema。

## 导入其他机型

使用你拥有再分发权的 `.vsp3` 模型：

```bat
py -3.13 Tools/import_vsp.py --vsp path\to\MyAircraft.vsp3 --name MyAircraft --openvsp-dir "%VSPROOT%"
```

默认输出到 `VspImports/MyAircraft`。`work` 子目录保存可再生的求解中间产物并已被忽略。配置和合适的 OBJ 输出只有在核实来源并清除本机路径后才能提交。

几何名称应使用 ASCII 字母和数字。下划线与非 ASCII 名称可能破坏 VSPAERO 操纵面匹配。将导入机型视为可飞之前，必须检查质量属性、参考几何、操纵轴、稳定性、配平和数值行为；管线不保证任意 OpenVSP 模型都能产生安全或实用的飞行模型。

## 工坊操作

F8 工坊编辑独立的工坊模型，并调用相同的外部工具。worker 使用 Python 3.13 与 OpenVSP 绑定，气动计算使用独立 `vspscript` 进程。除非打算把模型作为经过审查的项目资产，否则应将用户模型保存到受跟踪的测试夹具目录之外。

OpenVSP 翼型数据可从用户本机安装读取。复制任何翼型文件进行再分发前，都必须单独核实来源和许可；不能仅凭 OpenVSP 可执行程序的许可证推定每个数据文件都可再分发。

## 数据与许可边界

TestUAV 和 `NOTICE` 明确列出的机型为项目原创。由这些模型得到的 VSPAERO 输出属于项目数据，但第三方几何、翼型坐标、库和求解器不会因为被处理或复制而转为 MIT。

添加模型、翼型、纹理、库或二进制前，请先阅读 `THIRD_PARTY_NOTICES.md`。
