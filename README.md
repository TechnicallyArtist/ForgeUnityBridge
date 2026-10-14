# Forge Bridge for Unity

Unity 6000.0.40f1 development host for the editor-only
[Forge Bridge package](Packages/com.technicallyartist.forge-bridge/README.md).
The package turns an Engine Output push from Forge into native Unity assets.

Open this project in **6000.0.40f1**. Product code is entirely under `Packages/`;
`Assets/` contains host fixtures and ignored live-push output.

- [Package overview, installation and asset ownership](Packages/com.technicallyartist.forge-bridge/README.md)
- [Transport](Packages/com.technicallyartist.forge-bridge/Editor/Server/README.md)
- [Protocol](Packages/com.technicallyartist.forge-bridge/Editor/Protocol/README.md)
- [Import and animation](Packages/com.technicallyartist.forge-bridge/Editor/Import/README.md)
- [Editor UI](Packages/com.technicallyartist.forge-bridge/Editor/UI/README.md)
- [Architectural decisions](Packages/com.technicallyartist.forge-bridge/Documentation~/adr/README.md)
- [Release checks](Packages/com.technicallyartist.forge-bridge/Documentation~/release-checks.md)

Run `./scripts/Test-EditMode.ps1` for the floor editor. Run
`./scripts/Test-EditMode.ps1 -Version 6000.4.6f1 -CreateProbe` once to create and test
the separate `E:/b/fu/probe-6000.4` project; omit `-CreateProbe` on later runs.
Close an interactive editor using that project first. Never open this host in a
newer editor. The CI workflow runs the package in fresh projects on a licensed
Windows self-hosted runner labelled `forge-unity`, with both editor versions
installed at `E:/Unity/<version>/Editor/Unity.exe`.

The private repository is [TechnicallyArtist/ForgeUnityBridge](https://github.com/TechnicallyArtist/ForgeUnityBridge).
Release validation and production smoke are tracked by PRO-292 and the
[validation record](Packages/com.technicallyartist.forge-bridge/Documentation~/validation.md).
