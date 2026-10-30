# 测试指南

[English version](../testing.md)

请从仓库根目录运行命令。在 Pull Request 中记录实际通过、失败和跳过数量，不要复制旧开发报告中的数字。

## 仓库卫生与核心同步

```bash
py -3.13 -B Tools/check_repository.py
```

该检查比较两份飞行动力学源码，并拒绝 Python 缓存、超限文件、已知开发机路径和常见凭据标记。它不能替代完整的密钥扫描或人工资源授权审查。

## Python 测试

安装直接开发依赖：

```bash
py -3.13 -m pip install -r requirements-dev.txt
py -3.13 -B -m unittest discover -s Tests -p "test_*.py"
```

默认测试使用随附夹具。需要真实 OpenVSP worker 或求解器的测试在未配置依赖时会明确跳过。

要包含集成测试，请设置 `VSPROOT`，并确保 Python 3.13 能加载 OpenVSP 绑定：

```bat
set VSPROOT=C:\Tools\OpenVSP-3.51.3
py -3.13 -B -m unittest discover -s Tests -p "test_*.py"
```

集成测试可能在 `work` 下生成已忽略文件。长时间求解建议使用一次性 worktree，生成输出必须审查后才能提交。

## 独立 C# 测试

安装带 Roslyn 的 Visual Studio 2022 BuildTools。构建脚本会搜索常见 VS2022 版本，也支持 `CSC_PATH`。

```bash
bash Tools/build_phase1.sh
(cd bin && ./FlightDynamics.Tests.exe)
(cd bin && ./ConsoleFlightSim.exe --headless 2)
```

脚本构建 `FlightDynamicsCore.dll`、`FlightDynamics.Tests.exe` 和 `ConsoleFlightSim.exe`，并将默认配置复制到 `bin`。请从 `bin` 运行可执行文件，以便找到配置。

## Unity PlayMode 测试

关闭正在使用该工程的其他 Unity 实例，设置 `UNITY_EDITOR_PATH` 后运行：

```bat
"%UNITY_EDITOR_PATH%" -batchmode -force-d3d12 -projectPath "%CD%\UnityProject" -runTests -testPlatform PlayMode -testResults "%CD%\UnityProject\Logs\playmode.xml" -logFile "%CD%\UnityProject\Logs\playmode.log"
```

不要添加 `-nographics`，HDRP 渲染测试需要真实图形设备；也不要在测试命令中添加 `-quit`。同时检查 XML 结果和编辑器日志。

## 手动发布检查

从没有 `Library`、`work` 和 `bin` 的全新克隆开始：

1. 导入 Unity 工程，确认没有编译错误、缺失脚本或默认资源丢失。
2. 进入 Play Mode，验证起飞、复位、相机切换和三个运行时场地。
3. 打开 F8；依赖可用时，验证 worker 和一个 OpenVSP 工况。
4. 使用 `GameBuild.BuildWindows` 构建独立程序，分别启动三个 `-scene` 参数。
5. 查看 `docs/verification.md`，确认当前候选版本实际验证了哪些内容，以及哪些项目未运行。
