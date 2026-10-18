using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ForgeBridge.Editor.Tests
{
    public class ForgeTransportTests
    {
        private static string Request(int port, string origin = "http://localhost:3000") =>
            $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            (origin == null ? "" : "Origin: " + origin + "\r\n") + "\r\n";

        [Test]
        public void Handshake_Rfc6455AcceptExample()
        {
            Assert.That(ForgeHandshake.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ=="), Is.EqualTo("s3pPLMBiTxaQ9kYGzzhZRbK+xOo="));
        }

        [TestCase("valid", 101)]
        [TestCase("wrongHost", 400)]
        [TestCase("duplicateHost", 400)]
        [TestCase("missingOrigin", 403)]
        [TestCase("nullOrigin", 403)]
        [TestCase("unknownOrigin", 403)]
        [TestCase("duplicateOrigin", 403)]
        [TestCase("http", 426)]
        [TestCase("version", 426)]
        [TestCase("key", 400)]
        [TestCase("fold", 400)]
        [TestCase("earlyData", 400)]
        [TestCase("hostBeforeOrigin", 400)]
        [TestCase("upgradeBeforeOrigin", 426)]
        public void Handshake_EnforcesMatrixBeforeConsent(string variation, int status)
        {
            var request = Request(7042);
            switch (variation)
            {
                case "wrongHost": request = request.Replace("127.0.0.1:7042", "forge.attacker.example:7042"); break;
                case "duplicateHost": request = request.Replace("Host:", "Host: localhost:7042\r\nHost:"); break;
                case "missingOrigin": request = Request(7042, null); break;
                case "nullOrigin": request = Request(7042, "null"); break;
                case "unknownOrigin": request = Request(7042, "https://attacker.example"); break;
                case "duplicateOrigin": request = request.Replace("Origin:", "Origin: http://localhost:3000\r\nOrigin:"); break;
                case "http": request = request.Replace("Upgrade: websocket\r\n", ""); break;
                case "version": request = request.Replace("Version: 13", "Version: 12"); break;
                case "key": request = request.Replace("dGhlIHNhbXBsZSBub25jZQ==", "bad"); break;
                case "fold": request = request.Replace("Origin:", " Origin:"); break;
                case "earlyData": request += "x"; break;
                case "hostBeforeOrigin": request = Request(7042, null).Replace("127.0.0.1:7042", "evil.test:7042"); break;
                case "upgradeBeforeOrigin": request = Request(7042, null).Replace("Upgrade: websocket\r\n", ""); break;
            }
            Assert.That(ForgeHandshake.Evaluate(request, 7042).Status, Is.EqualTo(status));
        }

        [TestCase("https://forge.technicallyartist.com", true)]
        [TestCase("https://forge.technicallyartist.com:443", true)]
        [TestCase("http://localhost:5173", true)]
        [TestCase("http://127.0.0.1:1", true)]
        [TestCase("https://forge.technicallyartist.com:444", false)]
        [TestCase("https://forge.technicallyartist.com.evil.test", false)]
        [TestCase("https://forge.technicallyartist.com/", false)]
        [TestCase("https://forge.technicallyartist.com@evil.test", false)]
        [TestCase("http://127.0.0.2:3000", false)]
        [TestCase("http://localhost:65536", false)]
        [TestCase("file:///index.html", false)]
        public void Origin_UsesExactKnownOrigins(string origin, bool allowed)
        {
            Assert.That(ForgeHandshake.TryNormalizeOrigin(origin, out _), Is.EqualTo(allowed));
        }

        [TestCase("1.0", true)]
        [TestCase("1.1", true)]
        [TestCase("1.2", true)]
        [TestCase("1.3", true)]
        [TestCase("1.4", true)]
        [TestCase("1.5", false)]
        [TestCase("2.0", false)]
        [TestCase("1", false)]
        [TestCase("1.0.0", false)]
        [TestCase("1.-1", false)]
        [TestCase("1.+1", false)]
        [TestCase("1. 1", false)]
        [TestCase(null, false)]
        public void Protocol_AcceptsOnlySupportedWholeNumberVersions(string version, bool accepted)
        {
            Assert.That(ForgeProtocol.AcceptsVersion(version), Is.EqualTo(accepted));
        }

        [Test]
        public void Welcome_PreservesLegacyFieldsAndAddsUnityDestinationIdentity()
        {
            var welcome = ForgeProtocol.Welcome("hello-id", "project-id", "Bridge Test", "6000.0.40f1", "0.1.0");
            Assert.That((string)welcome["engine"]["version"], Is.EqualTo("6000.0.40f1"));
            Assert.That((string)welcome["engine"]["name"], Is.EqualTo("unity"));
            Assert.That((string)welcome["project"]["name"], Is.EqualTo("Bridge Test"));
            Assert.That((string)welcome["projectId"], Is.EqualTo("project-id"));
            Assert.That((string)welcome["projectName"], Is.EqualTo("Bridge Test"));
            Assert.That((string)welcome["editorVersion"], Is.EqualTo("6000.0.40f1"));
            Assert.That((string)welcome["ref"], Is.EqualTo("hello-id"));
        }

        [Test]
        public void ProjectIdentity_UsesProductGuidAndStablePathFallback()
        {
            Assert.That(BridgeService.ProjectIdentity("071205390ac44147a858c85fba599468", "."), Is.EqualTo("071205390ac44147a858c85fba599468"));
            Assert.That(BridgeService.ProjectIdentity("00000000000000000000000000000000", "."), Is.EqualTo(BridgeService.ProjectIdentity("", Path.GetFullPath("."))));
            Assert.That(BridgeService.ProjectIdentity("", "."), Is.Not.EqualTo(BridgeService.ProjectIdentity("", "another-project")));
        }

        [Test]
        public void Frame_UnmasksIncrementalPayloadWithOffset()
        {
            var encoded = ClientFrame(2, Encoding.UTF8.GetBytes("Hello"));
            var header = WebSocketCodec.DecodeHeader(encoded);
            Assert.That(header.Length, Is.EqualTo(5));
            var first = encoded.Skip(6).Take(2).ToArray();
            var second = encoded.Skip(8).ToArray();
            WebSocketCodec.Unmask(first, first.Length, header.Mask, 0);
            WebSocketCodec.Unmask(second, second.Length, header.Mask, 2);
            Assert.That(Encoding.UTF8.GetString(first.Concat(second).ToArray()), Is.EqualTo("Hello"));
        }

        [TestCase(0x09, 0x80, 1002)]
        [TestCase(0x89, 0xfe, 1002)]
        [TestCase(0xc2, 0x80, 1002)]
        [TestCase(0x82, 0x00, 1002)]
        [TestCase(0x83, 0x80, 1002)]
        public void Frame_RejectsMalformedFrames(int first, int second, int code)
        {
            var bytes = new byte[14]; bytes[0] = (byte)first; bytes[1] = (byte)second;
            Assert.That(Assert.Throws<BridgeProtocolException>(() => WebSocketCodec.DecodeHeader(bytes)).CloseCode, Is.EqualTo(code));
        }

        [Test]
        public void Message_RejectsAnnouncedCapWithoutBufferingBody()
        {
            using (var message = new ForgeMessageBuffer(16))
            {
                Assert.That(Assert.Throws<BridgeProtocolException>(() => message.AnnounceFrame((ulong)(4 + ForgeProtocol.HeaderLimit + 17))).CloseCode, Is.EqualTo(1009));
                Assert.That(message.BufferedBytes, Is.Zero);
            }
        }

        [Test]
        public void Message_RejectsHeaderCapFromFourBytesAlone()
        {
            using (var message = new ForgeMessageBuffer(16))
            {
                message.AnnounceFrame(4);
                Assert.That(Assert.Throws<BridgeProtocolException>(() => message.Append(new byte[] { 1, 0, 1, 0 }, 0, 4)).CloseCode, Is.EqualTo(1009));
                Assert.That(message.BufferedBytes, Is.EqualTo(4));
            }
        }

        [Test]
        public void Message_RejectsExactPayloadCapFromPrefixBeforeReadingBody()
        {
            using (var message = new ForgeMessageBuffer(16))
            {
                message.AnnounceFrame(23); // 4-byte prefix + 2-byte header + 17-byte payload.
                Assert.That(Assert.Throws<BridgeProtocolException>(() => message.Append(new byte[] { 2, 0, 0, 0 }, 0, 4)).CloseCode, Is.EqualTo(1009));
                Assert.That(message.BufferedBytes, Is.EqualTo(4));
            }
        }

        [Test]
        public void Message_FragmentTotalEnforcesCap()
        {
            using (var message = new ForgeMessageBuffer(16))
            {
                message.AnnounceFrame(4);
                message.Append(new byte[] { 2, 0, 0, 0 }, 0, 4);
                message.AnnounceFrame(18);
                Assert.That(Assert.Throws<BridgeProtocolException>(() => message.AnnounceFrame(1)).CloseCode, Is.EqualTo(1009));
            }
        }

        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{\"a\":1,\"a\":2}")]
        [TestCase("{}{}")]
        public void Message_RejectsMalformedOrAmbiguousHeader(string header)
        {
            var bytes = Encoding.UTF8.GetBytes(header);
            var encoded = new byte[bytes.Length + 4]; encoded[0] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, encoded, 4, bytes.Length);
            using (var message = new ForgeMessageBuffer(16))
            {
                message.AnnounceFrame((ulong)encoded.Length);
                message.Append(encoded, 0, encoded.Length);
                Assert.That(Assert.Throws<BridgeProtocolException>(() => message.Complete()).CloseCode, Is.EqualTo(1008));
            }
        }

        [Test]
        public async Task Listener_UsesNextFreePortAndReleasesItOnDispose()
        {
            var ports = ReservePorts(3);
            var first = ((IPEndPoint)ports[0].LocalEndpoint).Port;
            ports[1].Stop(); ports[2].Stop();
            try
            {
                using (var firstServer = new TestServer(first, first + 2))
                using (var secondServer = new TestServer(first, first + 2))
                {
                    Assert.That(await firstServer.Start(), Is.EqualTo(first + 1));
                    Assert.That(await secondServer.Start(), Is.EqualTo(first + 2));
                    firstServer.Dispose();
                    using (var rebound = new TestServer(first, first + 2)) Assert.That(await rebound.Start(first + 1), Is.EqualTo(first + 1));
                }
            }
            finally { foreach (var listener in ports) listener.Stop(); }
        }

        [Test]
        public async Task Listener_AllPortsTakenReportsReason()
        {
            var ports = ReservePorts(1);
            var port = ((IPEndPoint)ports[0].LocalEndpoint).Port;
            try
            {
                using (var server = new TestServer(port, port))
                {
                    Assert.That(await server.Start(), Is.Zero);
                    Assert.That(server.Reason, Does.Contain("no free port"));
                }
            }
            finally { ports[0].Stop(); }
        }

        [Test]
        public async Task Listener_UnknownOriginNeverAsksConsentAndDenyAsksAgain()
        {
            using (var server = new TestServer())
            {
                server.Allow = false;
                var port = await server.Start();
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    await Write(client, Encoding.ASCII.GetBytes(Request(port, "https://evil.test")));
                    Assert.That(await ReadHttp(client), Does.StartWith("HTTP/1.1 403"));
                    Assert.That(server.ConsentCalls, Is.Zero);
                }
                for (var i = 0; i < 2; i++)
                {
                    using (var client = new TcpClient())
                    {
                        await client.ConnectAsync(IPAddress.Loopback, port);
                        await Write(client, Encoding.ASCII.GetBytes(Request(port)));
                        // A Deny completes the upgrade only to say so: a browser cannot read a 403.
                        Assert.That(await ReadHttp(client), Does.StartWith("HTTP/1.1 101"));
                        var denied = await ReadMessage(client);
                        Assert.That((string)denied["reason"], Is.EqualTo("origin"));
                        Assert.That((string)denied["message"], Is.EqualTo(ForgeServer.OriginDeniedMessage));
                        var close = await ReadFrame(client);
                        Assert.That(close.Item1, Is.EqualTo(8));
                        Assert.That(close.Item2[0] << 8 | close.Item2[1], Is.EqualTo(1008));
                    }
                }
                Assert.That(server.ConsentCalls, Is.EqualTo(2));
            }
        }

        [Test]
        public void Consent_RemembersDenyForTheEditorSessionUntilCleared()
        {
            const string origin = "http://localhost:4999";
            Assume.That(BridgeUserState.instance.IsAllowed(origin), Is.False);
            try
            {
                Assert.That(BridgeService.RememberedConsent(origin), Is.Null);
                BridgeService.RememberDenied(origin);
                BridgeService.RememberDenied(origin);
                Assert.That(BridgeService.RememberedConsent(origin), Is.False);
                Assert.That(BridgeService.DeniedOrigins.Count(item => item == origin), Is.EqualTo(1));
                BridgeService.ClearDenied(origin);
                Assert.That(BridgeService.RememberedConsent(origin), Is.Null);
            }
            finally { BridgeService.ClearDenied(origin); }
        }

        [Test]
        public async Task Session_FragmentedHelloPingPushAndCloseRoundTrip()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                var hello = ForgeProtocol.Encode(Hello());
                await Write(client, ClientFrame(2, hello.Take(3).ToArray(), false));
                await Write(client, ClientFrame(9, new byte[] { 4, 5 }));
                var pong = await ReadFrame(client);
                Assert.That(pong.Item1, Is.EqualTo(10));
                Assert.That(pong.Item2, Is.EqualTo(new byte[] { 4, 5 }));
                await Write(client, ClientFrame(0, hello.Skip(3).ToArray()));
                var welcome = await ReadMessage(client);
                Assert.That((string)welcome["type"], Is.EqualTo("welcome"));
                Assert.That((string)welcome["projectId"], Is.EqualTo("unity-project"));
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Push(), new byte[] { 1, 2, 3 })));
                var result = await ReadMessage(client);
                Assert.That((string)result["ref"], Is.EqualTo("push-id"));
                Assert.That((bool)result["ok"], Is.True);
                Assert.That((string)result["assetPath"], Is.EqualTo("Assets/Forge/Test.prefab"));
                await Write(client, ClientFrame(8, new byte[] { 3, 232 }));
                Assert.That((await ReadFrame(client)).Item1, Is.EqualTo(8));
            }
        }

        [Test]
        public async Task Session_ProcessesPingsWhileImportAwaitsMainThread()
        {
            var finish = new TaskCompletionSource<PushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = new TestServer { Import = (_, __) => finish.Task })
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Hello())));
                await ReadMessage(client);
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Push(), new byte[] { 1, 2, 3 })));
                await Write(client, ClientFrame(9, new byte[] { 42 }));
                Assert.That((await ReadFrame(client)).Item1, Is.EqualTo(10));
                finish.SetResult(PushResult.Failure("specific import failure"));
                var result = await ReadMessage(client);
                Assert.That((bool)result["ok"], Is.False);
                Assert.That((string)result["error"], Is.EqualTo("specific import failure"));
            }
        }

        [Test]
        public async Task Session_OversizedDiagnosticsReturnBoundedResultAndNextPushStillWorks()
        {
            int pushes = 0;
            using (var server = new TestServer
            {
                Import = (_, __) => Task.FromResult(Interlocked.Increment(ref pushes) == 1
                    ? new PushResult
                    {
                        Error = "Import failure: " + new string('\u4e2d', 100000),
                        Warnings = Enumerable.Repeat("Detailed warning " + new string('x', 200), 1000).ToList()
                    }
                    : new PushResult { AssetPath = "Assets/Forge/Recovered.prefab", Created = true })
            })
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Hello())));
                await ReadMessage(client);
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Push(), new byte[] { 1, 2, 3 })));
                var result = await ReadMessage(client);
                Assert.That((string)result["ref"], Is.EqualTo("push-id"));
                Assert.That((bool)result["ok"], Is.False);
                Assert.That((string)result["error"], Does.StartWith("Import failure:"));
                Assert.That(((JArray)result["warnings"]).Any(warning => ((string)warning).Contains("truncated")), Is.True);
                Assert.That(ForgeProtocol.Encode(result).Length, Is.LessThanOrEqualTo(ForgeProtocol.HeaderLimit + 4));
                var second = Push(); second["id"] = "push-again";
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(second, new byte[] { 1, 2, 3 })));
                var recovered = await ReadMessage(client);
                Assert.That((string)recovered["ref"], Is.EqualTo("push-again"));
                Assert.That((bool)recovered["ok"], Is.True);
            }
        }

        [Test]
        public async Task Session_UnencodableResultClosesInsteadOfSilentlyLosingReply()
        {
            using (var server = new TestServer
            {
                Import = (_, __) => Task.FromResult(new PushResult { AssetPath = new string('x', ForgeProtocol.HeaderLimit + 1) })
            })
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Hello())));
                await ReadMessage(client);
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Push(), new byte[] { 1, 2, 3 })));
                var close = await ReadFrame(client);
                Assert.That(close.Item1, Is.EqualTo(8));
                Assert.That((close.Item2[0] << 8) | close.Item2[1], Is.EqualTo(1009));
            }
        }

        [Test]
        public async Task Session_ProtocolVersionIsCheckedBeforeOtherHeaderFields()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(new JObject { ["protocol"] = "2.0" })));
                var denied = await ReadMessage(client);
                Assert.That((string)denied["reason"], Is.EqualTo("protocol"));
                Assert.That((await ReadFrame(client)).Item1, Is.EqualTo(8));
            }
        }

        [Test]
        public async Task Session_RevokeSendsDeniedThenCloses()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Hello())));
                await ReadMessage(client);
                server.Server.Revoke("http://localhost:3000");
                Assert.That((string)(await ReadMessage(client))["reason"], Is.EqualTo("origin"));
                Assert.That((await ReadFrame(client)).Item1, Is.EqualTo(8));
            }
        }

        [TestCase("1.2", "update-failed")]
        [TestCase("1.3", "update-started")]
        public async Task Session_UpdateDispatchesAfterHelloAndChecksMessageVersion(string protocol, string expected)
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Hello())));
                await ReadMessage(client);
                var update = new JObject { ["protocol"] = protocol, ["type"] = "update", ["id"] = "update-id", ["version"] = "0.2.2" };
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(update)));
                var result = await ReadMessage(client);
                Assert.AreEqual(expected, (string)result["type"]);
                Assert.AreEqual("update-id", (string)result["ref"]);
                if (expected == "update-failed") Assert.That((string)result["message"], Does.Contain("1.3"));
                await Write(client, ClientFrame(9, new byte[] { 42 }));
                Assert.AreEqual(10, (await ReadFrame(client)).Item1);
            }
        }

        [Test]
        public async Task Session_UpdateBeforeHelloIsDenied()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(new JObject { ["protocol"] = "1.3", ["type"] = "update", ["id"] = "u", ["version"] = "0.2.2" })));
                Assert.AreEqual("hello", (string)(await ReadMessage(client))["reason"]);
            }
        }

        [Test]
        public async Task ProjectSync_ListsProjectsOfACapableTab()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await SyncHello(client, server);
                var listing = server.Server.ListProjectsAsync(CancellationToken.None);
                var request = await ReadMessage(client);
                Assert.That((string)request["type"], Is.EqualTo("list-projects"));
                Assert.That((string)request["protocol"], Is.EqualTo("1.4"));
                var id = (string)request["id"];
                await Reply(client, id, new JObject { ["type"] = "project", ["project"] = new JObject
                {
                    ["id"] = "elf-boy", ["name"] = "Elf Boy", ["folder"] = "Characters / Elves",
                    ["outputs"] = new JArray(new JObject { ["id"] = "rig/out", ["name"] = "Unity" })
                } });
                await Reply(client, id, new JObject { ["type"] = "project", ["project"] = new JObject { ["id"] = "rock", ["name"] = "Rock", ["folder"] = "", ["outputs"] = new JArray() } });
                await Reply(client, id, new JObject { ["type"] = "projects-complete", ["count"] = 2 });
                var list = await listing;
                Assert.That(list.Kind, Is.EqualTo(ForgeSyncOutcomeKind.Succeeded));
                Assert.That(list.Projects.Select(p => p.Name), Is.EqualTo(new[] { "Elf Boy", "Rock" }));
                Assert.That(list.Projects[0].Folder, Is.EqualTo("Characters / Elves"));
                Assert.That(list.Projects[0].Outputs.Single().Id, Is.EqualTo("rig/out"));
                Assert.That(list.Projects[1].Outputs, Is.Empty);
            }
        }

        [Test]
        public async Task ProjectSync_PushReportsProgressImportsThePushThenTheOutcome()
        {
            var imported = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = new TestServer { Import = (header, _) => { imported.TrySetResult(header); return Task.FromResult(new PushResult { AssetPath = "Assets/Forge/Elf.prefab" }); } })
            using (var client = await Connect(server))
            {
                await SyncHello(client, server);
                var progress = new ConcurrentQueue<ForgePushProgress>();
                var pushing = server.Server.PushProjectAsync("elf-boy", "rig/out", progress.Enqueue, CancellationToken.None);
                var request = await ReadMessage(client);
                Assert.That((string)request["type"], Is.EqualTo("push-project"));
                Assert.That((string)request["projectId"], Is.EqualTo("elf-boy"));
                Assert.That((string)request["nodeId"], Is.EqualTo("rig/out"));
                var id = (string)request["id"];
                await Reply(client, id, new JObject { ["type"] = "project-status", ["status"] = "preparing" });
                await Reply(client, id, new JObject { ["type"] = "project-status", ["status"] = "waiting", ["source"] = "elf.fbx", ["message"] = "Allow source file access" });
                await Reply(client, id, new JObject { ["type"] = "project-status", ["status"] = "sending" });
                var push = Push();
                push["ref"] = id;
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(push, new byte[] { 1, 2, 3 })));
                Assert.That((string)(await imported.Task)["ref"], Is.EqualTo(id));
                Assert.That((bool)(await ReadMessage(client))["ok"], Is.True);
                Assert.That(pushing.IsCompleted, Is.False, "The push-project outcome waits for Forge's terminal status.");
                await Reply(client, id, new JObject { ["type"] = "project-status", ["status"] = "complete", ["ok"] = true,
                    ["assetPath"] = "Assets/Forge/Elf.prefab", ["created"] = false, ["warnings"] = new JArray("cook note") });
                var outcome = await pushing;
                Assert.That(outcome.Kind, Is.EqualTo(ForgeSyncOutcomeKind.Succeeded));
                Assert.That(outcome.AssetPath, Is.EqualTo("Assets/Forge/Elf.prefab"));
                Assert.That(outcome.Warnings, Is.EqualTo(new[] { "cook note" }));
                Assert.That(progress.Select(p => p.Status), Is.EqualTo(new[] { "preparing", "waiting", "sending" }));
                Assert.That(progress.Single(p => p.WaitingForForge).Source, Is.EqualTo("elf.fbx"));
            }
        }

        [Test]
        public async Task ProjectSync_FailedStatusEndsTheRequestWithForgesError()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await SyncHello(client, server);
                var pushing = server.Server.PushProjectAsync("elf-boy", "missing", null, CancellationToken.None);
                var id = (string)(await ReadMessage(client))["id"];
                await Reply(client, id, new JObject { ["type"] = "project-status", ["status"] = "failed", ["error"] = "Unknown Engine Output node: missing." });
                var outcome = await pushing;
                Assert.That(outcome.Kind, Is.EqualTo(ForgeSyncOutcomeKind.Failed));
                Assert.That(outcome.Error, Is.EqualTo("Unknown Engine Output node: missing."));
                await Reply(client, id, new JObject { ["type"] = "project-status", ["status"] = "failed", ["error"] = "late" });
                await Write(client, ClientFrame(9, new byte[] { 1 }));
                Assert.That((await ReadFrame(client)).Item1, Is.EqualTo(10), "A reply nobody waits for is ignored, not a protocol error.");
            }
        }

        [Test]
        public async Task ProjectSync_ATabWithoutTheCapabilityIsNoForge()
        {
            using (var server = new TestServer())
            using (var client = await Connect(server))
            {
                await Write(client, ClientFrame(2, ForgeProtocol.Encode(Hello())));
                await ReadMessage(client);
                var list = await server.Server.ListProjectsAsync(CancellationToken.None);
                Assert.That(list.Kind, Is.EqualTo(ForgeSyncOutcomeKind.NoForge));
                var outcome = await server.Server.PushProjectAsync("p", "n", null, CancellationToken.None);
                Assert.That(outcome.Kind, Is.EqualTo(ForgeSyncOutcomeKind.NoForge));
                Assert.That(outcome.Error, Is.EqualTo(ForgeServer.NoForgeMessage));
            }
        }

        [Test]
        public async Task ProjectSync_DisconnectFailsTheWaitingRequest()
        {
            using (var server = new TestServer())
            {
                var client = await Connect(server);
                await SyncHello(client, server);
                var pushing = server.Server.PushProjectAsync("elf-boy", "rig/out", null, CancellationToken.None);
                await ReadMessage(client);
                client.Close();
                var outcome = await pushing;
                Assert.That(outcome.Kind, Is.EqualTo(ForgeSyncOutcomeKind.Failed));
                Assert.That(outcome.Error, Does.StartWith("Forge disconnected"));
            }
        }

        /// <summary>Hello advertising project sync; returns once the server would send a request to this tab.</summary>
        private static async Task SyncHello(TcpClient client, TestServer server)
        {
            var hello = Hello();
            hello["protocol"] = "1.2";
            hello["capabilities"] = new JArray("project-sync", "project-backup");
            await Write(client, ClientFrame(2, ForgeProtocol.Encode(hello)));
            Assert.That((string)(await ReadMessage(client))["type"], Is.EqualTo("welcome"));
            // The connection is marked sync-capable just after its welcome is written.
            for (var i = 0; i < 100 && !server.Server.HasSyncSession; i++) await Task.Delay(10);
            Assert.That(server.Server.HasSyncSession, Is.True);
        }

        private static Task Reply(TcpClient client, string reference, JObject reply)
        {
            reply["protocol"] = "1.4";
            reply["id"] = Guid.NewGuid().ToString("D");
            reply["ref"] = reference;
            return Write(client, ClientFrame(2, ForgeProtocol.Encode(reply)));
        }

        private static JObject Hello() => new JObject
        {
            ["protocol"] = "1.1", ["type"] = "hello", ["id"] = "hello-id", ["session"] = "tab-id",
            ["project"] = new JObject { ["id"] = "forge-project", ["name"] = "Test" }, ["app"] = new JObject { ["version"] = "test" }
        };

        private static JObject Push() => new JObject
        {
            ["protocol"] = "1.1", ["type"] = "push", ["id"] = "push-id", ["kind"] = "mesh",
            ["payload"] = new JObject { ["format"] = "glb", ["bytes"] = 3 }
        };

        private static byte[] ClientFrame(byte opcode, byte[] payload, bool final = true)
        {
            var unmasked = WebSocketCodec.Encode(opcode, payload);
            var head = unmasked.Length - payload.Length;
            var result = new byte[unmasked.Length + 4];
            Buffer.BlockCopy(unmasked, 0, result, 0, head);
            result[0] = (byte)((final ? 128 : 0) | opcode);
            result[1] |= 128;
            var mask = new byte[] { 0x37, 0xfa, 0x21, 0x3d };
            Buffer.BlockCopy(mask, 0, result, head, 4);
            for (var i = 0; i < payload.Length; i++) result[head + 4 + i] = (byte)(payload[i] ^ mask[i & 3]);
            return result;
        }

        private static async Task<TcpClient> Connect(TestServer server)
        {
            var port = await server.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await Write(client, Encoding.ASCII.GetBytes(Request(port)));
            Assert.That(await ReadHttp(client), Does.StartWith("HTTP/1.1 101"));
            return client;
        }

        private static Task Write(TcpClient client, byte[] bytes) => client.GetStream().WriteAsync(bytes, 0, bytes.Length);

        private static async Task<string> ReadHttp(TcpClient client)
        {
            var text = new StringBuilder();
            while (text.Length < 16384)
            {
                var next = await Read(client, 1);
                text.Append((char)next[0]);
                if (text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return text.ToString();
            }
            throw new Exception("HTTP response too large");
        }

        private static async Task<Tuple<byte, byte[]>> ReadFrame(TcpClient client)
        {
            var header = await Read(client, 2);
            var length = header[1] & 127;
            if (length == 126)
            {
                var extended = await Read(client, 2);
                length = extended[0] << 8 | extended[1];
            }
            else if (length == 127)
            {
                var extended = await Read(client, 8);
                ulong wideLength = 0;
                foreach (var value in extended) wideLength = (wideLength << 8) | value;
                Assert.That(wideLength, Is.LessThanOrEqualTo((ulong)ForgeProtocol.HeaderLimit + 4));
                length = (int)wideLength;
            }
            Assert.That(length, Is.LessThanOrEqualTo(ForgeProtocol.HeaderLimit + 4));
            return Tuple.Create((byte)(header[0] & 15), await Read(client, length));
        }

        private static async Task<JObject> ReadMessage(TcpClient client)
        {
            var frame = await ReadFrame(client);
            Assert.That(frame.Item1, Is.EqualTo(2));
            using (var buffer = new ForgeMessageBuffer(1024 * 1024))
            {
                buffer.AnnounceFrame((ulong)frame.Item2.Length);
                buffer.Append(frame.Item2, 0, frame.Item2.Length);
                return buffer.Complete().Header;
            }
        }

        private static async Task<byte[]> Read(TcpClient client, int count)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var pending = client.GetStream().ReadAsync(bytes, offset, count - offset);
                if (await Task.WhenAny(pending, Task.Delay(5000)) != pending) throw new TimeoutException("Timed out reading bridge response");
                var received = await pending;
                if (received == 0) throw new EndOfStreamException();
                offset += received;
            }
            return bytes;
        }

        private static TcpListener[] ReservePorts(int count)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var listeners = new TcpListener[count];
                try
                {
                    listeners[0] = new TcpListener(IPAddress.Loopback, 0); listeners[0].Start();
                    var port = ((IPEndPoint)listeners[0].LocalEndpoint).Port;
                    if (port + count > 65535) { listeners[0].Stop(); continue; }
                    for (var i = 1; i < count; i++) { listeners[i] = new TcpListener(IPAddress.Loopback, port + i); listeners[i].Start(); }
                    return listeners;
                }
                catch (SocketException) { }
                foreach (var listener in listeners) listener?.Stop();
            }
            throw new Exception("Could not reserve test ports");
        }

        private sealed class TestServer : IDisposable
        {
            public ForgeServer Server;
            public bool Allow = true;
            public int ConsentCalls;
            public string Reason;
            public Func<JObject, byte[], Task<PushResult>> Import = (_, __) => Task.FromResult(new PushResult { AssetPath = "Assets/Forge/Test.prefab", Created = true });
            private readonly int first;
            private readonly int last;
            private readonly TaskCompletionSource<int> bound = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TestServer(int first = 0, int last = 0)
            {
                if (first == 0)
                {
                    var reservation = ReservePorts(1);
                    first = ((IPEndPoint)reservation[0].LocalEndpoint).Port;
                    reservation[0].Stop();
                    last = first;
                }
                this.first = first;
                this.last = last;
            }

            public async Task<int> Start(int preferred = 0)
            {
                Server = new ForgeServer(first, last, 1024 * 1024,
                    ForgeProtocol.Welcome(null, "unity-project", "Unity", "6000.0.40f1", "0.1.0"),
                    (_, __) => { Interlocked.Increment(ref ConsentCalls); return Task.FromResult(Allow); },
                    (header, payload, _) => Import(header, payload), (_, __) => { },
                    (port, reason) => { Reason = reason; bound.TrySetResult(port); }, _ => { }, async (header, bytes, reply, token) =>
                    {
                        var problem = BridgeUpdater.Problem(header, bytes, "0.2.1", true, false, false, false);
                        await BridgeUpdater.RunAsync(header, problem, reply, _ => Task.FromResult<string>(null), (_, __) => {});
                        return true;
                    });
                _ = Server.StartAsync(preferred);
                if (await Task.WhenAny(bound.Task, Task.Delay(5000)) != bound.Task) throw new TimeoutException("Listener did not bind");
                return await bound.Task;
            }

            public void Dispose() => Server?.Dispose();
        }
    }
}
