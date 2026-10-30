# Changelog

All notable public-project changes are recorded here. The project follows a chronological, human-readable changelog rather than inventing releases for earlier private development snapshots.

## Unreleased

### Added

- Initial public-source candidate with Unity/HDRP visualization, standalone C# 6DOF dynamics, OpenVSP/VSPAERO data tools, aircraft workshop, and three procedural environments.
- English and Chinese README, architecture, setup, OpenVSP workflow, and testing documentation.
- Contribution, conduct, support, security, roadmap, and third-party notice files.
- Self-contained Python fixtures, portable OpenVSP/Unity path configuration, and standalone ConsoleFlightSim build entry.
- Repository hygiene checks and Windows CI for Python, standalone C#, and source synchronization.

### Known verification limits

- Clark Y and Eppler E205 coordinate files are excluded; the workshop can use the user's local OpenVSP airfoil library.
- Aircraft containing embedded third-party airfoil coordinates with unverified redistribution rights, and their derived fixtures, are excluded from the initial public snapshot.
- Unity PlayMode, manual flight, Windows player, and full OpenVSP integration results are recorded accurately in `docs/verification.md`; unrun checks are not claimed as passing.
