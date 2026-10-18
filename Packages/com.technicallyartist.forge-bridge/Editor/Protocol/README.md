# Protocol

[`ForgeProtocol.cs`](ForgeProtocol.cs) implements protocol **1.1**: a little-endian u32 header
length, a UTF-8 JSON object (maximum 64 KiB), then an optional payload (maximum 256 MiB, configurable
lower). The incremental message buffer checks announced frame totals and the header prefix before
accepting further body bytes. Invalid JSON, duplicate properties and invalid UTF-8 are refused.

The version is checked before other header fields. Major 1, minor 0 or 1 are accepted. The first
message must be `hello`; a second hello is denied. Every push receives exactly one `result`, including
envelope validation and importer failures. `PushResult` carries the imported path, creation flag,
warnings and an optional error.

Unity `welcome` preserves `engine.version` and `project.name` and adds `engine.name: "unity"`,
`projectId`, `projectName` and `editorVersion`. Project identity uses `PlayerSettings.productGUID`,
falling back to a stable SHA-256 of the normalized absolute project path for an empty GUID.

The Forge repository owns the canonical specification at `Plugins/PROTOCOL.md`. Server framing
and refusal behavior mirror the Unreal Forge Bridge implementation.

[Package overview](../../README.md) · [Server](../Server/README.md)

Protocol 1.3 accepts earlier 1.x headers. Welcome advertises plugin.selfUpdate only for an official Git install; update requires its own 1.3 header and no payload. update-started and update-failed correlate with ref. A reconnect welcome confirms successful installation. See the canonical Forge Plugins/PROTOCOL.md.

## Project sync (1.4)

[`ForgeProjectSync.cs`](ForgeProjectSync.cs) builds the Unity → tab requests `list-projects` and
`push-project` and reads the replies: `project` (one per project), `projects-complete`, and
`project-status` (`preparing`, `waiting`, `cooking`, `sending`, then `complete` or `failed`). It defines
the public result types `ForgeProject`, `ForgeProjectList`, `ForgePushProgress` and `ForgePushOutcome`.
A tab advertises `capabilities: ["project-sync"]` in its hello; requests go only to such a tab. The
requested push itself is an ordinary `push` carrying `ref`, answered by the usual `result`.
