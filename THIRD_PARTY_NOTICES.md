# Third-party notices

This file distinguishes external tools from files redistributed by the source repository. Third-party content does not become MIT by being referenced, processed, or copied.

## External tools and packages (not redistributed here)

| Component | Project use | License/source handling |
|---|---|---|
| Unity 2022.3.62f3c1, HDRP 14.0.12, Input System 1.7.0, Test Framework 1.1.33 and resolved Unity packages | Editor, rendering, input, and PlayMode tests | Users install Unity and resolve packages from the configured Unity registry. Unity and package licenses apply; see the installed package license files. |
| OpenVSP/VSPAERO 3.51.3 | Offline geometry, mass, stability, and polar analysis | External installation. OpenVSP is distributed upstream under NASA Open Source Agreement 1.3; its source and license are available from the OpenVSP project. No OpenVSP binaries or libraries are included in this repository. |
| Python 3.13 | Workshop worker and tools | External installation under the Python Software Foundation license and included third-party licenses. |
| NumPy 2.4.4 | Terrain generation and tests | Installed through `requirements-dev.txt`; NumPy's BSD-3-Clause license applies. |
| Pillow 12.2.0 | Optional terrain previews and tests | Installed through `requirements-dev.txt`; Pillow's Historical Permission Notice and Disclaimer applies. |
| Visual Studio 2022 BuildTools / Roslyn | Standalone C# compilation | External Microsoft build tools; Microsoft and included-component licenses apply. |
| NUnit through Unity Test Framework | Unity PlayMode tests | Resolved by Unity; the Unity package and NUnit licenses apply. |
| Windows system fonts (Consolas, Microsoft YaHei, Segoe UI, Arial, Courier New) | Runtime HUD text | Loaded from the user's operating system with fallbacks. No font files are redistributed. |
| Blender (`bpy`) | Optional local probe scripts in development workflows | Optional external tool; not required for the default simulator or tests and not redistributed. |

If these components are later bundled into an installer, the installer must carry all required copyright texts, license notices, source-availability information, and dependency manifests. The source-repository MIT license is not sufficient for such a bundle.

## Data and assets included in the repository

### Original project assets

TestUAV, the original models listed in `NOTICE`, procedural terrain and textures, and formula-generated NACA airfoil coordinates are original project content under MIT. See `NOTICE` for the ownership scope.

### Excluded Clark Y and Eppler E205 airfoil data

`Data/Workshop/airfoils/Clark Y.dat` and `Data/Workshop/airfoils/Eppler E205.dat` are intentionally excluded from the public snapshot. They had been copied from an OpenVSP installation, and their data-specific redistribution permission was not established.

The workshop may read these airfoils from the user's own OpenVSP installation. Do not add copied coordinate files to the repository without recording the upstream author, source, and redistribution license. The OpenVSP executable's NOSA text alone is not treated as independent proof of permission for those data files.

### Excluded aircraft with embedded third-party airfoil coordinates

LogisticV0928, logisitc, logisti, uav_71770e, and 弹簧刀 are excluded from the public snapshot. Their `.vsp3` files embed NASA-LANGLEY LS(1)-0413 (GA(W)-2), EPPLER 396, or related coordinate data whose redistribution permission has not been established. Associated OBJ files, solver configurations, and split-group fixtures derived from those aircraft are excluded as well. They may be restored only after the relevant data rights are documented and the models are reviewed again.

## Technical references

MIL-F-8785C Dryden turbulence, ISA atmosphere, NACA airfoil formulas, and other public technical references inform project algorithms. These references are not, by themselves, a license to copy third-party code, models, coordinates, or artwork.
