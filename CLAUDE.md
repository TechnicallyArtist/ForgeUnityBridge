# CLAUDE.md

Guidance for working in this repository. Read the package README
(`Packages/com.technicallyartist.forge-bridge/README.md`) for the plugin overview once it exists;
until then this file is the entry point.

## What this repository is

`ProceduralMeshDCC-UnityPlugin` is a **thin Unity 6000.0 host project** whose only purpose is to
develop and test **Forge Bridge for Unity**, the embedded UPM package
`Packages/com.technicallyartist.forge-bridge`. The host's `Assets/` holds test scenes and fixtures
only and never product code. The package must never depend on host-project code or content. The
package is the product: users install it by git URL, and it must work in any Unity 6000.0+
project.

**Forge Bridge for Unity** is the Unity half of the *Unity bridge*. A Forge browser tab (the
web-based node-graph DCC at `E:\Gamedev\ProceduralMeshDCC`, live at forge.technicallyartist.com)
connects to this package over a loopback WebSocket and **pushes** a mesh or rigged character. The
package turns the push into saved native Unity assets under the **Forge folder** (`Assets/Forge/`)
and shows what is connected in a **Window ▸ Forge** window. The Forge half (the **Engine Output**
node) lives in that repo under `Plugins/Unity/`.

The design is settled and written down. Read these before changing shape:

- **Tickets (canonical scope):** Linear epic **PRO-286** with slices PRO-287 … PRO-292. Each
  ticket says what to build and its acceptance criteria.
- **Plan:** `E:\Gamedev\ProceduralMeshDCC\planning\unity-bridge\00-plan.md`.
- **Vocabulary:** `E:\Gamedev\ProceduralMeshDCC\CONTEXT.md` › *Engine bridges* (Engine Output node,
  Destination, Unity bridge, Forge Bridge for Unity, Forge folder, Folder, Forge-owned) and
  › *Unreal bridge* (push, session, allowed origin). Use these words in code, comments and docs;
  do not invent synonyms.
- **Decision records:** Forge ADR 0077 (this plugin's shape) and ADR 0064 (the transport and
  consent model it reuses). This repo's own ADRs go in the package's `Documentation~/adr/`.
- **The wire contract:** `E:\Gamedev\ProceduralMeshDCC\Plugins\PROTOCOL.md`. **The Forge repo owns
  it**, and this package implements it. (Until PRO-287 moves it there, the spec lives in the Unreal
  plugin repo at `E:\UE\Projects\ForgePluginProject\Plugins\ForgeBridge\PROTOCOL.md`.)
- **Reference implementation:** the Unreal plugin, Forge Bridge
  (`E:\UE\Projects\ForgePluginProject\Plugins\ForgeBridge`). Its handshake, frame codec, consent
  gate and refresh-in-place behaviour are what this package mirrors in C#. Read the matching file
  there before writing the C# equivalent.

## General rules

1. Ask, don't assume. If something is unclear, ask before writing a single line. Never make silent
   assumptions about intent, architecture, or requirements. When running unattended, pick the most
   reasonable interpretation, proceed, and record the assumption rather than blocking.
2. Implement the simplest solution for simple problems, better solutions for harder problems. Do
   not over-engineer or add flexibility that isn't needed yet.
3. Don't touch unrelated code, but surface bad code or design smells you discover so they can be
   addressed as a separate issue.
4. Flag uncertainty explicitly. If unsure, see point 1. Where it makes sense, run a small,
   localised, low-risk experiment and bring the hypothesis and results to discuss. Confidence
   without certainty causes more damage than admitting a gap.
5. Suggestions for better ways, or ones with long-lasting impact over a tactical change, are
   always welcome.
6. **Everything implemented must be completely functional.** No stubs, no placeholders, no
   "good enough for now" shortcuts, no silently dropped data. A feature is done when it works
   end-to-end for real inputs.
7. If a full implementation is too large for one change, **split it into functional slices**, each
   complete and shippable on its own — never a half-built feature hidden behind a flag.

## Version control

Work on `main`. Do not create or switch branches, do not open pull requests, and never create git
worktrees unless explicitly asked. Commit directly to `main` in this checkout. The repo has **no
remote yet**: PRO-292 creates the private GitHub repo, so ask before adding one or pushing. Binary
fixtures and images (`.glb`, `.png`, …) are tracked with **Git LFS** (`.gitattributes`). Unity
YAML assets are committed as text with LF endings.

Never commit `Library/`, `Temp/`, `Logs/`, `UserSettings/`, generated `.csproj`/`.sln`, or
anything under `Assets/Forge/` (smoke-test pushes land there). All of these are in `.gitignore`.

## Repository structure (target — PRO-287 creates it)

```
ProjectSettings/                 host project settings; ProjectVersion.txt pins 6000.0
Packages/manifest.json           host manifest: glTFast + the embedded package, listed in "testables"
Assets/                          host test scenes/fixtures only — never product code
Packages/com.technicallyartist.forge-bridge/     THE PRODUCT — see its README.md for the map:
  package.json                     name, version, unity "6000.0", dependency com.unity.cloud.gltfast
  README.md                        what it is, install (git URL), supported versions, links to every area
  CHANGELOG.md
  Documentation~/adr/              decision records for the package
  Editor/                          ForgeBridge.Editor.asmdef (Editor platform only)
    Server/     README.md          loopback TcpListener + RFC 6455 (own implementation), port range
    Protocol/   README.md          framing, header JSON, version rule, message types
    Import/     README.md          glb (via glTFast, in memory) → native assets; one translator per push kind
    UI/         README.md          Window ▸ Forge, Allow dialog, Project Settings ▸ Forge
  Tests/Editor/                    ForgeBridge.Editor.Tests.asmdef (EditMode) + Fixtures/
.github/workflows/               CI (PRO-292)
```

## Coding rules

These extend the Forge repo's rules (single responsibility, one file one job, don't
over-fragment, top-down story order, names say what) to C#/Unity:

1. **Editor-only package.** All product code lives in Editor assemblies. Anything the package
   would put into a player build needs a ticket that says so, because it ends up in the user's
   shipped game.
2. **Threads.** Socket I/O runs off the main thread. Every Unity API call (AssetDatabase,
   EditorWindow, PlayerSettings, dialogs) runs on the main thread, through a queue drained from
   `EditorApplication.update`. No Unity API from a worker, ever.
3. **Domain reloads are normal.** A script recompile tears down every static. The listener must
   close its socket in `AssemblyReloadEvents.beforeAssemblyReload` and rebind the same port after
   the reload. It must never leak a socket or fight itself for the port. Durable state (approvals,
   push history) lives in `UserSettings/` or `ProjectSettings/`, never in statics.
4. **Asset writes are batched and in place.** One push = one
   `AssetDatabase.StartAssetEditing()`/`StopAssetEditing()` block (in `try/finally`). Forge-owned
   assets are overwritten **in place**, so their GUIDs never change. Never delete and recreate an
   asset that already exists. Only Forge-owned assets are rewritten; user-owned work (prefab
   variants, scene overrides, reassigned slot materials, Animator transitions) is never touched
   (CONTEXT.md › *Forge-owned*).
5. **glTFast parses; this package writes.** Use glTFast only to read the glb in memory. Never leave
   a `.glb` in `Assets/`, and never depend on glTFast shaders at runtime. Materials use the active
   pipeline's stock shader (URP Lit / HDRP Lit / Built-in Standard).
6. **Security invariants are not configurable:** bind loopback only (no bind-address setting);
   refuse the handshake unless `Host` is loopback and `Origin` is an allowed Forge origin; no
   message may carry code; a payload over the cap is refused, never buffered.
7. **Nothing fails silently.** Every refusal or failure is an error `result` to the tab, a line in
   the Console (prefix `[Forge]`), and a row in Window ▸ Forge.

## Units and coordinates

Forge is meters, Y-up, right-handed (glTF). Unity is meters, Y-up, left-handed. glTFast's
conversion (it mirrors X) is the one conversion. The package never rescales or re-axes on top of
it. Auto Rig characters face +Z in Forge and should face +Z in Unity; if a test shows otherwise,
investigate rather than patching with a rotation.

## Documentation rules

1. **Update the relevant README with every change.** The package README is the root:
   description, install, supported versions, and a **map** linking one `README.md` per
   functionality folder (`Server`, `Protocol`, `Import`, `UI`), the protocol spec in the Forge
   repo, and `Documentation~/adr/`. Each folder README says what its files do and links back up.
   Code and its README move together.
2. **A wire change goes to the Forge repo's `Plugins/PROTOCOL.md` in the same change**, with the
   protocol version bumped there: minor for new optional fields, major for anything a 1.x client
   would misread. The Forge repo's commit is part of the change, not a follow-up.
3. **ADRs** (`Documentation~/adr/NNNN-slug.md`) only for hard-to-reverse choices with a real
   trade-off; one per choice.
4. Keep `CHANGELOG.md` and the `package.json` version in step with each release.

## Build & verify

Editors live at `E:\Unity\<version>\Editor\Unity.exe`. Installed: **6000.0.40f1** (the floor),
6000.3.25f1, **6000.4.6f1** (the newest).

> **Until package version 1.0, test only the floor (6000.0) and the newest (6000.4)**, for every
> change and every release.

The host project is pinned to 6000.0. **Never open the host in 6000.4**: that rewrites
`ProjectVersion.txt`, `Packages/packages-lock.json` and settings. Test the newest version in a
scratch probe project outside the repo (`E:\b\fu\probe-6000.4`) whose `Packages/manifest.json`
references the package with
`"com.technicallyartist.forge-bridge": "file:E:/Gamedev/ProceduralMeshDCC-UnityPlugin/Packages/com.technicallyartist.forge-bridge"`
and lists it in `"testables"`.

EditMode tests, headless. Do **not** pass `-quit` together with `-runTests`. The exit code is the
result: 0 = all passed, 2 = failures, 3 = run error. The XML has the per-test detail.

```powershell
& 'E:\Unity\6000.0.40f1\Editor\Unity.exe' -batchmode -nographics -projectPath 'E:\Gamedev\ProceduralMeshDCC-UnityPlugin' -runTests -testPlatform EditMode -testFilter 'ForgeBridge' -testResults 'E:\Gamedev\ProceduralMeshDCC-UnityPlugin\TestResults\editmode-6000.0.xml' -logFile 'E:\Gamedev\ProceduralMeshDCC-UnityPlugin\TestResults\editmode-6000.0.log'; $LASTEXITCODE
```

Same against the probe for 6000.4: swap the editor path to `E:\Unity\6000.4.6f1\Editor\Unity.exe`
and `-projectPath` to the probe.

Run the cheapest check that proves the change, once:

- EditMode tests on 6000.0 for any code change.
- 6000.0 **and** 6000.4 before calling a change done.
- The live smoke for anything user-visible: a Forge tab pushes, the assets appear under
  `Assets/Forge/`, and a second push refreshes them in place with unchanged GUIDs.

**No editor may have the project open** during a batchmode run: Unity refuses a second instance
on the same project. A live smoke needs the editor open, so close it before the headless run.
