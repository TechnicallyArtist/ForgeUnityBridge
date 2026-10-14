# Release 0.1.0 checks (PRO-292)

The owner approved repository creation, publication and remaining release work on
2026-10-14. Completed evidence is recorded in [implementation validation](validation.md).
Every check below is done; the GitHub release is published.

1. Done: private `TechnicallyArtist/ForgeUnityBridge` repository created, origin
   configured and main pushed with LFS fixtures.
2. Done: dedicated licensed Windows runner registered with label `forge-unity`.
   Both Unity versions are installed at the test script's paths. This runner is
   separate from the Unreal repository's runner.
3. Done: initial CI passed 126 tests on each editor; the corrected commit's
   [CI run](https://github.com/TechnicallyArtist/ForgeUnityBridge/actions/runs/38629303814)
   passed on 6000.0.40f1 and 6000.4.6f1 with XML/logs retained.
4. Done: production Chrome smoke from `https://forge.technicallyartist.com` on
   both editors (git-URL installs, public-origin Allow, textured prop and Auto Rig
   character with Idle/Walk/Jog pushed, Idle plays). See
   [validation](validation.md#production-smoke).
5. Done: Prefab Variant, folder move, upstream edit and re-push refreshed in
   place with every GUID unchanged and no duplicates; both projects appeared in
   Destination together.
6. Done: Built-in on both editors, then URP on 6000.4. Screenshots are on PRO-292.
7. Done: docs built and deployed through the normal Cloudflare Pages process,
   including private-install links and the labelled local Unity screenshot.
8. Done: tag `0.1.0` points at `6c1bce5`. Its
   [CI run](https://github.com/TechnicallyArtist/ForgeUnityBridge/actions/runs/38637520860)
   passed on both editors, fresh tagged installs passed 134 tests on each, and
   their saved scenes reopened in a new editor process and played Idle.

The runner uses a short work directory (`E:/b/fu/ci-work`) to stay within Unity's
Windows path limits. It runs as a user process, not a service. After a reboot,
start `E:/Gamedev/FAB/Publish/actions-runner-forge-unity/Start-UnityRunner.ps1`.
The adjacent `UNITY-RUNNER-README.md` records local restart instructions. No
system PATH or Windows service configuration was changed.

Deferred scope is filed: PRO-293 automatic pushes, PRO-294 Humanoid avatars,
PRO-295 migration of Unreal onto Engine Output, and PRO-296 public distribution.

See [implementation validation](validation.md) for the completed local checks.
