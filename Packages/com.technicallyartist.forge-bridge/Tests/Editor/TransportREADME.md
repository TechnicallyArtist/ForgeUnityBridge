# Transport tests

`ForgeTransportTests` covers protocol validation and the real loopback listener with local TCP
clients. The handshake matrix checks refusal ordering and eligible origins, the RFC 6455 accept
example, masked frame parsing, fragmented/oversized control rejection, version negotiation,
early header/payload caps, ambiguous JSON rejection and destination fields.

Socket tests cover first-free-port selection, simultaneous listeners, port release/rebind,
exhausted ranges, origin refusal before consent, repeated Deny attempts, fragmented hello with an
interleaved ping, push/result, close echo, revoke, and ping responsiveness while an import awaits.
Oversized errors/warning lists must still produce a bounded result with an explicit truncation
notice and leave the session ready for another push. Unencodable response metadata must close
with a reason rather than silently abandoning the reply.
These clients inject consent callbacks and never display dialogs or persist origin approvals.

Run `ForgeBridge` EditMode tests on Unity 6000.0.40f1 and the separate 6000.4.6f1 probe as described
in the [package README](../../README.md). Live browser approval, durable settings across editor
restarts, and real domain reload remain editor smoke checks.
