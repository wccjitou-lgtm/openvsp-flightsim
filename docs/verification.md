# Verification record / 验证记录

Date / 日期: **2026-10-30; player build and smoke tests completed 2026-10-31**
Candidate / 候选版本: public-source staging snapshot based on `fc080e2`, with the portability and documentation changes in this directory.
Platform / 平台: Windows 11 x64

This file records only checks actually executed. “Not run” is not a passing result.
本文件只记录实际执行的检查，“未运行”不代表通过。

## Results / 结果

| Check / 检查 | Environment / 环境 | Result / 结果 |
|---|---|---|
| Python default suite / 默认 Python 测试 | Python 3.13; no `VSPROOT` | **198 run, 168 passed, 30 skipped**. OpenVSP-dependent tests skipped explicitly. / 198 项运行，168 通过，30 项明确跳过。 |
| Python integration suite / Python 集成测试 | Installed OpenVSP 3.51.3 selected by `VSPROOT` | **198 run, 196 passed, 1 skipped, 1 failed**. The installed `vspscript.exe` exited with code 3221226505 and an empty log in `test_prep_sets_elevator_mirror_gain`. / 安装版该用例异常退出，不能记为完整通过。 |
| Python integration suite / Python 集成测试 | Self-built OpenVSP selected by `VSPROOT` | **198 run, 168 passed, 30 skipped**. The runtime lacks `python/openvsp`, so worker tests skipped; the previously failing direct `vspscript` case passed. / 自编译版缺少 Python 绑定，worker 测试跳过；直接调用 vspscript 的用例通过。 |
| Standalone C# build / 独立 C# 构建 | VS2022 Roslyn `csc.exe` | Core library, test executable, and ConsoleFlightSim built successfully. One existing CS0169 warning for `MissionTests.touchdownTime`. / 三个目标构建成功，保留一个既有未使用字段警告。 |
| Standalone C# tests / 独立 C# 测试 | `FlightDynamics.Tests.exe` from `bin` | **40 passed, 0 failed**. / 40 通过，0 失败。 |
| Console smoke test / 控制台烟测 | `ConsoleFlightSim.exe --headless 2` | Passed; completed the two-second automatic takeoff demonstration. / 完成 2 秒自动起飞演示。 |
| Repository hygiene and local links / 仓库卫生与本地链接 | `Tools/check_repository.py` | Passed after creating this verification file. / 通过。 |
| Clean staged archive / 干净暂存归档 | Fresh `git archive` with no `Library`, `work`, or `bin` | **Python 198 run, 168 passed, 30 skipped; C# 40 passed; ConsoleFlightSim smoke passed; repository check passed.** The mass-properties fixture is tracked and the JSON test is line-ending independent. / 干净归档中 Python、C#、控制台烟测和仓库检查均通过，夹具完整，JSON 测试不受行尾影响。 |
| Clean Unity import and PlayMode / 干净 Unity 导入与 PlayMode | Unity 2022.3.62f3c1, D3D12, no previous `Library` | **451 run, 443 passed, 1 failed, 7 skipped** on the full run. The failure was `R03_EndToEnd_SetParmsViaBuilderActuallyApplies` because OpenVSP was not configured. After adding the explicit dependency skip, the filtered test **passed 1/1 with `VSPROOT`** and **skipped 1/1 without it**. The complete suite was not rerun after that test-only change. / 全套 443 通过、1 失败、7 跳过；唯一失败在配置 OpenVSP 后单独通过，未配置时正确跳过。修改后未重跑全套。 |
| Manual flight and workshop UI / 手动飞行与工坊界面 | Unity Editor | **Not run.** / 未运行。 |
| Windows player build and three scene arguments / 独立程序与三个场地参数 | `GameBuild.BuildWindows` | **Build succeeded 2026-10-31.** Forest, island, and alpine each launched and exited through a 5-second FpsProbe; no exceptions or shader errors were logged. A separate 10-second forest `-autofly` run also exited normally. The environment produced only about 0.4 FPS and the autofly probe did not establish takeoff, so interactive flight and visual quality remain unverified. / 构建成功，三个场地均启动并正常退出，未见异常日志；当前环境帧率极低，不能把烟测视为交互飞行或视觉验证通过。 |
| Full six-case TestUAV regeneration / TestUAV 六工况完整重算 | `Tools/run_all_analyses.py` | **Not run** in this candidate; integration tests do not replace the full pipeline. / 本候选版未运行，集成测试不能替代完整管线。 |

## Distribution boundary / 分发边界

- `Clark Y.dat` and `Eppler E205.dat` are excluded from the public snapshot; the workshop can use the user's local OpenVSP airfoil library. / 两份坐标文件已排除，工坊可使用用户本机的 OpenVSP 翼型库。
- LogisticV0928, logisitc, logisti, uav_71770e, 弹簧刀, and their derived fixtures are excluded because their embedded airfoil-coordinate redistribution rights were not established. / 因内嵌翼型坐标授权未确认，受影响机型及派生夹具均已排除。
- No installer or binary release is part of this candidate. / 本候选版不包含安装包或二进制发布。
