# Release 0.1.0 checks (PRO-292)

The owner approved repository creation, publication and remaining release work on
2026-10-14. Completed evidence is recorded in [implementation validation](validation.md).
The GitHub release remains a draft while final persistence and live checks run.

1. Done: private `TechnicallyArtist/ForgeUnityBridge` repository created, origin
   configured and main pushed with LFS fixtures.
2. Done: dedicated licensed Windows runner registered with label `forge-unity`.
   Both Unity versions are installed at the test script's paths. This runner is
   separate from the Unreal repository's runner.
3. Done: initial CI passed 126 tests on each editor; the corrected commit's
   [CI run](https://github.com/TechnicallyArtist/ForgeUnityBridge/actions/runs/38629303814)
   passed on 6000.0.40f1 and 6000.4.6f1 with XML/logs retained.
4. After the normal Forge deployment, the owner runs the production Chrome smoke:
   install by git URL, allow the public origin, push a textured prop and an Auto
   Rig character with Idle/Walk/hair. Inspect layout and material maps; drag the
   character into a scene and verify Idle plays.
5. Create a Prefab Variant, move the generated folder, edit upstream and push
   again. Record unchanged GUIDs, surviving overrides and no duplicate assets.
   Open two Unity projects and verify both appear in Destination.
6. Repeat on 6000.4, plus HDRP or Built-in. Capture the session's production origin
   and the resulting assets. Attach screenshots to PRO-292.
7. Done: docs built and deployed through the normal Cloudflare Pages process,
   including private-install links and the labelled local Unity screenshot.
8. Approved: tag `0.1.0` points at `5d33816` and the private GitHub release is a
   draft. Done: fresh tagged installs of the corrected commit passed on both
   editors, and their saved scenes reopened in a new editor process and played Idle.

The runner uses a short work directory (`E:/b/fu/ci-work`) to stay within Unity's
Windows path limits. It runs as a user process, not a service. After a reboot,
start `E:/Gamedev/FAB/Publish/actions-runner-forge-unity/Start-UnityRunner.ps1`.
The adjacent `UNITY-RUNNER-README.md` records local restart instructions. No
system PATH or Windows service configuration was changed.

Deferred scope is filed: PRO-293 automatic pushes, PRO-294 Humanoid avatars,
PRO-295 migration of Unreal onto Engine Output, and PRO-296 public distribution.

See [implementation validation](validation.md) for the completed local checks.
