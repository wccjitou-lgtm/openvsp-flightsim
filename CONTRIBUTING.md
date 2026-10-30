# Contributing / 贡献指南

## English

Thank you for helping improve OpenVSP FlightSim. Keep changes focused and reviewable; a pull request should address one problem or one coherent feature.

### Workflow

1. Search existing Issues and Discussions before starting major work.
2. Fork the repository and create a branch from `main`.
3. Read the relevant documentation and existing tests before editing.
4. Make the smallest change that solves the problem, following nearby naming, formatting, comments, and error handling.
5. Run the relevant checks and report actual results, including failures and skips.
6. Update English and Chinese user-facing documentation together.
7. Submit a pull request describing the motivation, scope, verification, and remaining limitations.

Do not commit unless you are authorized to share the work. Do not push generated player builds, installers, `Library`, `Temp`, `work`, telemetry, Python caches, or local editor settings.

### Code and tests

- Preserve existing public behavior unless the change explicitly intends to alter it; search callers first.
- Do not upgrade Unity, HDRP, OpenVSP, or other dependencies as part of an unrelated change.
- Keep `FlightDynamics` and `UnityProject/Assets/Scripts/FlightDynamics` synchronized. Run `py -3.13 -B Tools/check_repository.py`.
- Add or update tests for logic changes. Use the existing Python `unittest`, standalone C# `TestSuite`, and Unity NUnit patterns rather than introducing another framework.
- Treat OpenVSP integration, Unity PlayMode, and manual flight checks separately. Never describe an unrun check as passing.
- See `docs/testing.md` for commands and `docs/verification.md` for the current candidate record.

### Assets and licenses

Every new model, airfoil, texture, sound, font, plug-in, or copied code snippet needs an identifiable author, source, and redistribution license. Add the appropriate entry to `NOTICE` or `THIRD_PARTY_NOTICES.md`. Unknown provenance is not permission to redistribute. Do not bundle OpenVSP, Unity, Python, or other external runtimes in the source repository.

### Security and privacy

Do not include credentials, personal paths, private URLs, user telemetry, or real-user data in code, tests, issues, or logs. Report vulnerabilities privately according to `SECURITY.md`.

## 中文

感谢你参与 OpenVSP FlightSim。请让改动保持聚焦、便于审查；一个 Pull Request 只解决一个问题或完成一项连贯功能。

### 工作流程

1. 开始较大改动前，先搜索已有 Issue 和 Discussion。
2. Fork 仓库，并从 `main` 创建分支。
3. 修改前阅读相关文档、源码与测试。
4. 采用解决问题所需的最小改动，遵循周围的命名、格式、注释和错误处理方式。
5. 运行相关检查，如实报告通过、失败和跳过结果。
6. 同步更新中英文用户文档。
7. 在 Pull Request 中说明动机、范围、验证过程和剩余限制。

不要提交玩家构建、安装包、`Library`、`Temp`、`work`、遥测、Python 缓存或本机编辑器设置。

### 代码与测试

- 除非任务明确需要改变公共行为，否则应保持兼容；修改公共接口前先搜索调用方。
- 不在无关改动中升级 Unity、HDRP、OpenVSP 或其他依赖。
- 保持 `FlightDynamics` 与 `UnityProject/Assets/Scripts/FlightDynamics` 同步，并运行 `py -3.13 -B Tools/check_repository.py`。
- 逻辑改动应补充或更新测试，沿用现有 Python `unittest`、C# `TestSuite` 和 Unity NUnit，不引入另一套测试框架。
- 区分 OpenVSP 集成、Unity PlayMode 和手动飞行检查，未运行的项目不得声称通过。
- 命令见 `docs/zh-CN/testing.md`，当前候选版本记录见 `docs/verification.md`。

### 资源与许可

每个新增模型、翼型、纹理、音频、字体、插件或复制的代码片段都必须有明确的作者、来源和再分发许可，并更新 `NOTICE` 或 `THIRD_PARTY_NOTICES.md`。来源未知不代表可以再分发。不要把 OpenVSP、Unity、Python 或其他外部运行时捆绑进源码仓库。

### 安全与隐私

代码、测试、Issue 和日志中不得包含凭据、个人路径、私有 URL、用户遥测或真实用户数据。安全漏洞请按 `SECURITY.md` 私下报告。
