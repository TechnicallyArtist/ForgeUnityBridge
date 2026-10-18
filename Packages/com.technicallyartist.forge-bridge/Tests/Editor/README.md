# EditMode tests

[Package overview](../../README.md)

`ForgeBridge.Editor.Tests` covers handshake status codes, strict origin/Host gates,
masked and fragmented frames, limits, sessions, port reuse, native import,
identity collisions, GUID preservation, Variants, user material assignments,
pipeline channel packing and Animator Controller ownership. It also checks that writing
a prefab's Forge identity leaves the asset database in sync with its `.meta`.

`ForgeAnimatorPersistenceTests` verifies native graph subassets before any global save,
unloads/reimports the controller, and enters Play mode to check default Idle and hair
motion. It also covers graph local IDs, appended states, dirty-transition rollback,
foreign state-machine ownership, and unrelated unsaved assets.

The small triangle fixture is generated in `ForgeImportTests`. Character fixture
files under the package's [`Fixtures/`](../../Fixtures/) are exported from Forge's Auto Rig/animation/hair path;
their expected joint samples come from Forge's pose evaluator. Binary fixtures
are tracked with Git LFS.

From the host, run `scripts/Test-EditMode.ps1`. Always run both supported editor
versions before release; 6000.4 uses a separate probe, never this host. Test outputs
are ignored under `TestResults/`. Tests create and remove their own Assets folders.

`ForgeFlatPushTests` covers shared image recipes (32 materials / 3 textures), packed map channels,
repeat GUIDs, legacy-key migration, shrinking pushes, referenced and user-assigned materials,
and removal of empty folders only when Forge created them.

ForgeUpdateTests covers install-source eligibility, strict versions, busy refusals and a fake Package Manager adapter. ForgeTransportTests exercises post-hello update dispatch, old-header refusal and pre-hello denial over real loopback sockets.
