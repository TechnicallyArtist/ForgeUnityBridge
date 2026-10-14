# Editor UI and durable settings

[`ForgeWindow.cs`](ForgeWindow.cs) provides **Window ▸ Forge**: listener state, connected sessions,
the Forge project each names, elapsed connection time, approved origins with **Revoke**, and recent
events. Revoke immediately closes that origin's sessions. The window links to settings and can
restart the listener after a port conflict.

[`BridgeSettings.cs`](BridgeSettings.cs) provides **Project Settings ▸ Forge**. The Forge folder
(default `Assets/Forge`), first/last listener port (7042–7049), and payload cap (1–256 MiB) are saved
in `ProjectSettings/ForgeBridgeSettings.asset`. **Apply and restart listener** validates settings
and applies them to new connections. The bind address and known-origin list cannot be widened.

The first known, unapproved origin raises **Allow Forge to connect?** with its normalized origin.
Allow is persisted in `UserSettings/ForgeBridgeUserState.asset`; Deny remembers nothing. Batchmode
denies unapproved origins because no dialog can be shown. The same user settings file stores the
latest 100 event rows across reloads and restarts. This file must never be committed.

[Package overview](../../README.md) · [Server](../Server/README.md)
