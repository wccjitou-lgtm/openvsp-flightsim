# Security Policy / 安全政策

## English

### Reporting a vulnerability

Use the repository's **private GitHub Security Advisory** to report a suspected vulnerability. Do not open a public Issue for an unpatched security problem.

Include the affected commit or release, reproduction steps, expected and actual impact, relevant file paths, and a minimal proof of concept where safe. Remove credentials, personal data, and unrelated user models from logs and attachments.

The project currently maintains fixes on `main`; older snapshots may not receive backports. Reports are reviewed on a best-effort basis, and no response-time or fix-time guarantee is made. Please allow maintainers to investigate and release a fix before public disclosure.

### Scope

Reports may cover project C#, Python, Unity, build, or workflow code. Vulnerabilities in Unity, OpenVSP, Python, GitHub Actions dependencies, or other third-party tools should also be reported to the appropriate upstream project. Do not run destructive tests, access other users' data, or disrupt external services while investigating.

## 中文

### 报告安全漏洞

请通过仓库的 **GitHub 私密 Security Advisory** 报告疑似漏洞，不要为尚未修复的安全问题创建公开 Issue。

请提供受影响的提交或版本、复现步骤、预期与实际影响、相关文件路径，并在安全的前提下提供最小复现。日志和附件中应删除凭据、个人数据及无关的用户模型。

项目目前只在 `main` 上维护修复，旧快照可能不会获得回溯修复。报告按维护者可用时间尽力处理，不承诺响应或修复时限。公开披露前，请给维护者调查和发布修复的机会。

### 范围

可以报告项目 C#、Python、Unity、构建或工作流代码中的问题。Unity、OpenVSP、Python、GitHub Actions 依赖及其他第三方工具的漏洞也应报告给对应上游项目。调查时不得进行破坏性测试、访问他人数据或干扰外部服务。
