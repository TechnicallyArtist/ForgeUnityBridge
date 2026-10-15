# Loopback server

[`ForgeServer.cs`](ForgeServer.cs) owns a `TcpListener` and its WebSocket sessions. It binds only
`127.0.0.1`, tries the first free configured port (7042–7049 by default), and prefers this editor's
previous port after a domain reload. Disposal closes the listener and every client synchronously.
Socket reads, writes, handshake waits and heartbeats run on worker tasks.

[`ForgeHandshake.cs`](ForgeHandshake.cs) validates Host, upgrade and Origin in that order. Only
the public Forge HTTPS origin and HTTP localhost/127.0.0.1 origins are eligible for consent.
Invalid requests receive HTTP 400/403/426 before a dialog. Requests are limited to 16 KiB;
incomplete handshakes time out after 15 seconds. Clients cannot send data before HTTP 101.

[`WebSocketCodec.cs`](WebSocketCodec.cs) handles masked client frames, canonical length encodings,
control frame limits and valid UTF-8 close reasons. The receiver supports fragmented binary
messages with interleaved control frames. It checks announced message lengths before reading
bodies and checks the four-byte protocol length before reading headers. It never preallocates
an announced message body. Pings run every 15 seconds; two unanswered pings close the session.

[`BridgeService.cs`](BridgeService.cs) captures Unity project metadata on the main thread and
drains callbacks through `EditorApplication.update`. Imports enter through
`BridgeService.ImportAsync: Func<JObject, byte[], Task<PushResult>>` on the main thread, serialized
across sessions. One import per session can be outstanding; another push receives an explicit
failed result, while ping, close and revoke remain responsive. The service closes sockets before
assembly reload and editor exit. Missing import handlers return an explicit failed result.

Approvals and the latest 100 events persist per project/user under `UserSettings/`. Approval is
never inferred in batchmode. A Deny (fresh, or remembered for this editor session) completes the
upgrade only to send `denied` `origin` and close `1008`: a browser cannot read an HTTP 403, which
looks the same as an editor that is still importing or reloading scripts. Revoking an origin forgets it and sends `denied` / close to its sessions.
All refusals and push outcomes reach the Console with `[Forge]` and the Forge window's history.
Result diagnostics are bounded to the 64 KiB protocol header limit. Long errors or warning lists
carry an explicit truncation notice; the complete messages remain in the Unity Console. A response
that cannot fit even after diagnostic truncation closes the session with a logged reason instead
of leaving the tab waiting for a result.

[Package overview](../../README.md) · [Protocol](../Protocol/README.md) · [Editor UI](../UI/README.md)
