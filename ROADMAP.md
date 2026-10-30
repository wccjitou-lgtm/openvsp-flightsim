# Roadmap / 路线图

This roadmap describes possible directions, not commitments or dates. Priorities may change based on contributor availability and verified user needs.

本路线图只描述可能方向，不承诺日期或交付。优先级会根据贡献者时间和经过验证的用户需求调整。

## Near-term / 近期

- Validate clean-clone Unity import, PlayMode tests, manual flight, and Windows player builds on additional machines.
- 在更多机器上验证干净克隆的 Unity 导入、PlayMode、手动飞行和独立程序构建。
- Improve screenshots, architecture diagrams, and onboarding examples without changing current behavior.
- 在不改变现有行为的前提下补充截图、架构图和入门示例。
- Add optional self-hosted Unity CI with appropriate license and graphics support.
- 在具备许可证和图形环境的条件下增加可选自托管 Unity CI。

## Packaging / 打包

- Replace the legacy installer with a clean, minimal, locked runtime environment.
- 用干净、最小且锁定版本的运行环境替换旧安装器。
- Generate dependency inventories, third-party notices, SBOM data, and checksums for every binary release.
- 为每个二进制发布生成依赖清单、第三方声明、SBOM 和校验和。
- Evaluate code signing and reproducible release builds before publishing installers.
- 发布安装包前评估代码签名和可复现构建。

## Simulation / 仿真

- Expand aerodynamic envelope and ground-model validation against documented reference cases.
- 根据可记录的参考工况扩展气动包线和地面模型验证。
- Improve crosswind landing, collision coverage, and model-specific validation guidance.
- 改进侧风降落、碰撞覆盖和机型特定验证指南。
- Investigate PX4/MAVLink/HITL only through a separately reviewed design and implementation plan.
- PX4/MAVLink/HITL 仅在单独评审设计和实施计划后开展。
