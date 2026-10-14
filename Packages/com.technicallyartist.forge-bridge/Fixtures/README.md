# Forge integration fixtures

[Tests](../Tests/Editor/README.md)

These fixtures come from Forge's actual Rust exporter and pose evaluator, rather
than a hand-authored approximation of its glTF output.

- `forge-character-idle-walk-hair.glb`: Auto Rig's 25-joint skeleton plus five
  simulated hair joints, skin weights, and bundled Idle/Walk clips.
- `forge-character-poses.json`: local and world joint transforms at three sample
  times per clip, in Forge's right-handed coordinates. The Unity tests apply the
  expected X mirror once and compare all 30 joints, including hair.
- `forge-textured-cube.glb`: Smart UV cube with baked base color, metallic/roughness,
  normal and emission textures.

Regenerate from the Forge repository using its ignored fixture generator:

```powershell
$env:FORGE_UNITY_FIXTURES = 'E:/Gamedev/ProceduralMeshDCC-UnityPlugin/Packages/com.technicallyartist.forge-bridge/Fixtures'
cargo test --manifest-path engine/Cargo.toml unity_bridge_fixtures -- --ignored --nocapture
```

The generator lives in `engine/src/operations/hair_physics/unity_fixtures.rs`.
Commit the two GLBs through Git LFS and the pose JSON as text. Keep each file's
existing Unity `.meta` when regenerating.
