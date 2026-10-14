# EditMode tests

[Package overview](../../README.md)

`ForgeBridge.Editor.Tests` covers handshake status codes, strict origin/Host gates,
masked and fragmented frames, limits, sessions, port reuse, native import,
identity collisions, GUID preservation, Variants, user material assignments,
pipeline channel packing and Animator Controller ownership.

The small triangle fixture is generated in `ForgeImportTests`. Character fixture
files under the package's [`Fixtures/`](../../Fixtures/) are exported from Forge's Auto Rig/animation/hair path;
their expected joint samples come from Forge's pose evaluator. Binary fixtures
are tracked with Git LFS.

From the host, run `scripts/Test-EditMode.ps1`. Always run both supported editor
versions before release; 6000.4 uses a separate probe, never this host. Test outputs
are ignored under `TestResults/`. Tests create and remove their own Assets folders.
