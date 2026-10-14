# Release 0.1.0 checks (PRO-292)

The implementation is prepared locally. This checklist does not claim publication
or a production smoke has happened.

1. Obtain approval to create the private `TechnicallyArtist/ForgeUnityBridge`
   repository, add origin and push main. Do not make the repository public.
2. Register a licensed Windows self-hosted runner for this repository with label
   `forge-unity`. Both Unity versions must be installed at the paths used by the
   test script. The Unreal runner is registered to its own repository and cannot
   be silently reused.
3. Run the EditMode workflow on 6000.0.40f1 and 6000.4.6f1; retain XML and logs.
4. After the normal Forge deployment, the owner runs the production Chrome smoke:
   install by git URL, allow the public origin, push a textured prop and an Auto
   Rig character with Idle/Walk/hair. Inspect layout and material maps; drag the
   character into a scene and verify Idle plays.
5. Create a Prefab Variant, move the generated folder, edit upstream and push
   again. Record unchanged GUIDs, surviving overrides and no duplicate assets.
   Open two Unity projects and verify both appear in Destination.
6. Repeat on 6000.4, plus HDRP or Built-in. Capture the session's production origin
   and the resulting assets. Attach screenshots to PRO-292.
7. Build the docs site. The owner deploys it through the normal process.
8. Obtain approval to create tag `0.1.0` and publish the private GitHub release.
   Verify installation with the tag in a fresh project.

Deferred scope is filed: PRO-293 automatic pushes, PRO-294 Humanoid avatars,
PRO-295 migration of Unreal onto Engine Output, and PRO-296 public distribution.

See [implementation validation](validation.md) for the completed local checks.
