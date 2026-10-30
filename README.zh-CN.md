# OpenVSP FlightSim

[English README](README.md)

OpenVSP FlightSim 是一个 Windows 飞行模拟研究原型，将 OpenVSP/VSPAERO 气动数据、独立 C# 六自由度飞行动力学核心与 Unity/HDRP 可视化环境连接起来。

运行时**不执行 CFD**。OpenVSP 与 VSPAERO 在离线阶段生成稳定性与操纵导数，模拟器再将这些数据用于线性化气动模型。

## 主要功能

- OpenVSP 数据管线：`.vsp3` 几何、质量属性、VSPAERO 扫描与 `AircraftConfig.json` 生成。
- double 精度 C# 六自由度核心，采用 RK4 积分，不依赖 `UnityEngine`。
- ISA 大气、稳定性导数气动模型、简化推力与起落架，以及定常风、风切变、Dryden 紊流和离散阵风。
- 森林、海岛、雪谷三个程序化场地，在运行时切换。
- 键盘操纵、三种相机、HUD、返航、盘旋、自稳、自动降落与 CSV 遥测。
- F8 机型工坊：编辑模型、运行 VSPAERO、预览结果并热换机。
- Python、独立 C# 与 Unity PlayMode 三层测试。

## 当前限制

- 当前仅支持 Windows x64 与 Direct3D 12。
- 气动模型在选定工况附近线性化，不是全包线非线性模型，也不是实时 CFD。
- 推力、起落架、地面摩擦与碰撞行为采用简化工程模型。
- 自动降落尚无完整的侧风偏流/拉平修正控制律。
- 场地为程序化生成，不对应真实地理位置。
- PX4/MAVLink/HITL 是未来设计议题，当前尚未实现。
- 当前不提供安装包或二进制 Release。旧安装器正在重新设计依赖与再分发声明，因此未纳入公开版本。
- 本项目用于研究与开发，不是经过认证的飞行训练或适航工具。

## 环境要求

### 仅试飞随附的 TestUAV

- Windows 10/11 x64，支持 Direct3D 12 的 GPU。
- Unity **2022.3.62f3c1**，与 `UnityProject/ProjectSettings/ProjectVersion.txt` 一致。
- Unity 许可证及 Unity 包注册表访问能力。
- `UnityProject/Packages/manifest.json` 中的包，包括 HDRP 14.0.12 与 Input System 1.7.0。

默认 TestUAV 的配置、模型与地形资源已随仓库提供，因此仅试飞默认机不需要安装 OpenVSP。

### 重新生成机型与完整工坊

- OpenVSP **3.51.3**，包括 `vspscript.exe`、VSPAERO 与 Python 绑定。
- 与 OpenVSP Python 绑定兼容的 Python **3.13**。
- 地形工具与 Python 测试需要 `requirements-dev.txt` 中的包。
- 独立 C# 构建需要 Visual Studio 2022 BuildTools 与 Roslyn `csc.exe`。

OpenVSP、Unity、Python 及其依赖均为外部工具，本源仓库不再分发这些工具。

## 快速开始

1. 克隆仓库，用指定 Unity 版本打开 `UnityProject`。
2. 等待 Unity 恢复包并编译脚本。
3. 打开 `UnityProject/Assets/Scenes/Flight_Phase2.unity`，进入 Play Mode。默认是森林跑道上的 TestUAV。

也可以在仓库根目录通过终端配置并启动：

```bat
set UNITY_EDITOR_PATH=C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe
launch_flightsim.bat
```

上述路径仅为示例，请将 `UNITY_EDITOR_PATH` 设置为实际 Unity 可执行文件。`启动飞行模拟.bat` 会转发到同一启动器。

使用 OpenVSP 功能时还需设置：

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
```

`VSPROOT` 目录必须包含 `vspscript.exe` 与 `python/openvsp`。相关命令行工具也支持 `--openvsp-dir` 参数。

独立程序构建、环境配置与故障排查见[入门指南](docs/zh-CN/getting-started.md)。

## 操作键位

| 输入 | 功能 |
|---|---|
| Shift / Ctrl | 增加 / 减少油门 |
| W / S 或上 / 下箭头 | 俯仰 |
| A / D 或左 / 右箭头 | 滚转 |
| Q / E | 方向舵 |
| B | 刹车 |
| R / F | 俯仰配平 |
| 1 / 2 / 3 / 4 | 返航 / 降落 / 盘旋 / 自稳 |
| 0 | 退出自动模式 |
| V | 切换追尾、座舱与自由相机 |
| F8 | 打开机型工坊 |
| K / H | 键位说明 / 隐藏 HUD |
| F9 | 开关遥测记录 |
| Backspace | 复位飞机 |
| Esc | 暂停菜单 |

## 构建与测试

通过 `FlightSim.EditorTools.GameBuild.BuildWindows` 构建 Windows 独立程序，完整命令见[入门指南](docs/zh-CN/getting-started.md)。

```bat
Game\OpenVSPFlightSim.exe -scene forest
Game\OpenVSPFlightSim.exe -scene island
Game\OpenVSPFlightSim.exe -scene alpine
```

运行默认仓库检查：

```bash
py -3.13 -m pip install -r requirements-dev.txt
py -3.13 -B -m unittest discover -s Tests -p "test_*.py"
bash Tools/build_phase1.sh
(cd bin && ./FlightDynamics.Tests.exe)
py -3.13 -B Tools/check_repository.py
```

OpenVSP 集成测试需要 `VSPROOT`；Unity PlayMode 测试需要 Unity 编辑器与真实图形设备。详见[测试指南](docs/zh-CN/testing.md)和最新[验证记录](docs/verification.md)。历史文档中的固定测试数量不视为当前结果。

## 目录结构

```text
FlightDynamics/                    独立 C# 飞行动力学核心
UnityProject/
  Assets/Scripts/                  Unity 集成层及同步的核心副本
  Assets/Editor/                   场景与 Windows 构建工具
  Assets/Tests/PlayMode/           Unity NUnit 测试
  Assets/StreamingAssets/          运行时模型、配置与地形数据
Tools/                             OpenVSP、地形、构建与校验脚本
Tests/                             Python、C# 测试与固定夹具
Data/                              TestUAV 源模型与生成的参考数据
VspImports/                        已确认可再分发的机型库
.github/                           Issue/PR 模板与 CI
docs/                              中英双语开发文档
```

独立 `FlightDynamics` 源码有意镜像到 `UnityProject/Assets/Scripts/FlightDynamics`。`Tools/check_repository.py` 会阻止意外漂移；仅独立端使用的 JSON 加载器是明确的例外。

## 文档导航

- [入门指南](docs/zh-CN/getting-started.md) · [Getting started](docs/getting-started.md)
- [架构说明](docs/zh-CN/architecture.md) · [Architecture](docs/architecture.md)
- [OpenVSP 工作流](docs/zh-CN/openvsp-workflow.md) · [OpenVSP workflow](docs/openvsp-workflow.md)
- [测试指南](docs/zh-CN/testing.md) · [Testing](docs/testing.md)
- [贡献指南](CONTRIBUTING.md)
- [路线图](ROADMAP.md)
- [第三方声明](THIRD_PARTY_NOTICES.md)

## 贡献与支持

提交 Pull Request 前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。改动应保持聚焦，补充相关测试，维持两份核心源码同步，不提交生成物、本机路径或没有再分发权的资源。

Bug 与功能请求使用 GitHub Issues，设计讨论使用 Discussions。支持方式与私密漏洞报告见 [SUPPORT.md](SUPPORT.md) 和 [SECURITY.md](SECURITY.md)。

## 许可证

版权所有 (c) 2026 wccjitou。项目原创代码、文档、原创机型与程序化资产依据 [MIT 许可证](LICENSE) 提供，权属范围见 [NOTICE](NOTICE)。

第三方工具与数据保留其自身许可证，详见[第三方声明](THIRD_PARTY_NOTICES.md)。OpenVSP FlightSim 是独立项目，不隶属于 NASA 或 OpenVSP 项目，也未获得其背书。
