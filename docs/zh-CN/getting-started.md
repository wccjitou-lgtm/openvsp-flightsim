# 入门指南

[English version](../getting-started.md)

## 1. 安装必需工具

仅试飞随附 TestUAV 时，在 Windows x64 上安装 Unity **2022.3.62f3c1**。项目使用 HDRP 14.0.12 与 Direct3D 12；渲染和 PlayMode 渲染测试需要真实图形设备。

重新生成机型时，还需安装 OpenVSP **3.51.3** 和与其 Python 绑定匹配的 Python **3.13**。仅在需要时安装 Python 开发依赖：

```bash
py -3.13 -m pip install -r requirements-dev.txt
```

运行 OpenVSP 工具时，建议让仓库路径只包含 ASCII 字符；部分 OpenVSP 脚本与网格路径不能可靠处理非 ASCII 目录。

## 2. 在 Unity 中试飞 TestUAV

1. 在 Unity Hub 中打开 `UnityProject`。
2. 等待包恢复与脚本编译完成。
3. 打开 `Assets/Scenes/Flight_Phase2.unity`。
4. 进入 Play Mode。TestUAV 默认停在森林跑道，初始无风。

默认配置、OBJ 模型和地形资源已随仓库提供，此流程不需要 OpenVSP。

也可以在仓库根目录启动：

```bat
set UNITY_EDITOR_PATH=C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe
launch_flightsim.bat
```

如果 Unity 安装在其他位置，必须替换示例路径。启动器会按需创建 `UnityProject/Logs`，并使用 `-force-d3d12` 打开主场景。

## 3. 为工坊配置 OpenVSP

将 `VSPROOT` 设置为 OpenVSP 安装目录：

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
```

该目录必须包含：

- `vspscript.exe` 与 VSPAERO 可执行文件；
- `python/openvsp` 及其他随附 Python 包；
- 使用 OpenVSP 翼型库时所需的 `airfoil` 目录。

按 F8 打开工坊。其 Python worker 需要 Python 3.13 与 OpenVSP 绑定。可在工坊设置中配置 worker Python 和 OpenVSP 路径；命令行工具也支持 `--openvsp-dir`。

## 4. 构建独立程序

关闭正在使用该工程的其他 Unity 实例，在仓库根目录执行：

```bat
if not exist UnityProject\Logs mkdir UnityProject\Logs
"%UNITY_EDITOR_PATH%" -batchmode -force-d3d12 -projectPath "%CD%\UnityProject" -executeMethod FlightSim.EditorTools.GameBuild.BuildWindows -logFile "%CD%\UnityProject\Logs\build.log" -quit
```

构建入口为 `UnityProject/Assets/Editor/GameBuild.cs`，只构建 `Flight_Phase2.unity`，目标为 `StandaloneWindows64`，输出 `Game/OpenVSPFlightSim.exe`。

运行独立程序：

```bat
Game\OpenVSPFlightSim.exe -scene forest
Game\OpenVSPFlightSim.exe -scene island
Game\OpenVSPFlightSim.exe -scene alpine
Game\OpenVSPFlightSim.exe -scene forest -fpsprobe -probeseconds 60
```

默认模型、配置和地形通过 StreamingAssets 随独立程序分发。完整工坊还需要仓库中的 `Tools`、`VspImports`、Python 和 OpenVSP 目录布局。目前不提供可再分发安装包。

## 5. 故障排查

### 找不到 Unity

将 `UNITY_EDITOR_PATH` 设置为实际的 `Editor\Unity.exe`，不要把个人机器路径写回仓库。

### OpenVSP 或 worker 无法启动

确认 `VSPROOT` 指向安装根目录，而不是 `python` 子目录。配置 OpenVSP 包与 DLL 路径后，验证 Python 3.13 能导入 `openvsp`。

### 地形或材质显示为洋红

使用指定的 HDRP 包版本与 D3D12，并通过 `GameBuild.BuildWindows` 构建。该入口会为运行时生成内容建立显式着色器引用材质。

### 测试找不到本地文件

从仓库根目录运行命令，不依赖旧的 `work`、`bin`、`Library` 或安装器目录。默认 Python 和 C# 测试使用随附夹具；未配置 OpenVSP 时，相关集成测试会明确跳过。

### 批处理渲染测试失败

不要添加 `-nographics`。HDRP 渲染测试需要真实图形设备。使用 `-force-d3d12`，并检查 Unity 日志中的驱动或设备错误。

更多验证命令见[测试指南](testing.md)。
