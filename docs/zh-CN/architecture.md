# 架构说明

[English version](../architecture.md)

## 数据流

```text
OpenVSP .vsp3 几何
  -> AngelScript / MassProp / VSPAERO 稳定性与极曲线扫描
  -> Python 解析与回归
  -> AircraftConfig.json + 分部件 OBJ 网格
  -> 独立 C# 六自由度核心
  -> Unity 飞机、环境、HUD、工坊与遥测
```

VSPAERO 在离线阶段运行。Unity Player 使用生成的系数，飞行过程中不调用 CFD 求解器。

## 飞行动力学核心

`FlightDynamics` 是独立的 double 精度实现。其 Unity 程序集设置了 `noEngineReferences: true`，因此数值核心不依赖 `UnityEngine`。

主要职责：

- `AircraftState.cs`：NED 位置、体轴速度、角速率与四元数姿态。
- `RigidBodyIntegrator.cs`：平动与转动方程、四元数运动学和 RK4 积分。
- `AeroModel.cs`：线性化稳定性与操纵导数，以及工程限制。
- `AtmosphereModel.cs`：ISA 大气与机场标高偏移。
- `WindModel.cs`、`DrydenTurbulence.cs`：定常风、风切变、紊流和阵风。
- `GroundContactModel.cs`、`TerrainModel.cs`：简化起落架接触与高度图地形。
- `Trimmer.cs`：定常飞行配平。

Unity 以 120 Hz 调用飞行更新，每次内部再分四个积分子步，不使用 Unity `Rigidbody` 推进飞机。

核心源码镜像到 `UnityProject/Assets/Scripts/FlightDynamics`。当前版本有意保留这种重复，以同时支持独立 Roslyn 构建和 Unity。修改时必须同步两份，并运行 `Tools/check_repository.py`。独立端的 `IO/AircraftConfigJson.cs` 不镜像，因为 Unity 使用自己的配置加载器。

## Unity 集成层

- `Scripts/Aircraft`：控制器、输入映射、自动模式与边界保护。
- `Scripts/DataPipeline`：JSON 校验、加载与坐标转换。
- `Scripts/Import`：运行时 OBJ 装配与换机。
- `Scripts/Workshop`：工坊 UI、预览、worker 协议与模型状态。
- `Scripts/Environment`：程序化地形、跑道、水面、风、植被与动物。
- `Scripts/SceneSwitching`：场地 profile 与运行时环境重建。
- `Scripts/Camera`、`Scripts/UI`、`Scripts/Telemetry`：相机、HUD 与 CSV 输出。

主场景为 `Assets/Scenes/Flight_Phase2.unity`。森林、海岛和雪谷是运行时环境 profile，不是三个独立 `.unity` 场景。`SceneSwitcher` 重建环境，同时保留飞机、相机和当前机型。

显示地形与飞行接触高度图来自同一源数据。默认飞机视觉可回退到 StreamingAssets，因此场景不依赖编辑器机器上的 OBJ 路径。

## Python 与 OpenVSP 工具

`Tools/run_all_analyses.py` 重新生成 TestUAV 参考数据；`Tools/import_vsp.py` 导入合适的 `.vsp3` 机型。工坊有两条进程路径：

1. 常驻 Python worker，通过 stdio 上的 JSON Lines 完成模型编辑与导出；
2. 独立 `vspscript` 进程执行 VSPAERO 工况，在下一轮前保存上一轮结果。

`Tools/workshop_common.py` 负责解析仓库根目录和 OpenVSP 位置。OpenVSP 由 `VSPROOT`、`--openvsp-dir` 或明确支持的捆绑布局提供，不再使用机器特定的默认路径。

## 能力边界

气动模型是局部线性化模型；推力、起落架、轮胎摩擦、碰撞响应和部分控制律参数采用简化处理。这些设计适合研究和迭代，但不代表认证级保真度。数据生成见 [OpenVSP 工作流](openvsp-workflow.md)，验证层次见[测试指南](testing.md)。
