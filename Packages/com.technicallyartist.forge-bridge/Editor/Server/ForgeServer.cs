using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ForgeBridge.Editor
{
    public sealed class BridgeSession
    {
        public string Id;
        public string Origin;
        public string Project = "waiting for hello";
        public string AppVersion;
        public DateTime ConnectedAt = DateTime.UtcNow;
        public bool Connected = true;
        public string CloseReason;
        /// <summary>The tab advertised `project-sync` in its hello, so Unity may list its projects and request pushes.</summary>
        public bool ProjectSync;
    }

    /// <summary>Owns socket workers only; all callbacks are dispatched by BridgeService.</summary>
    public sealed class ForgeServer : IDisposable
    {
        private readonly int firstPort;
        private readonly int lastPort;
        private readonly int payloadCap;
        private readonly JObject welcome;
        private readonly Func<string, CancellationToken, Task<bool>> consent;
        private readonly Func<JObject, byte[], CancellationToken, Task<PushResult>> import;
        private readonly Func<JObject, int, Func<JObject, Task>, CancellationToken, Task<bool>> update;
        private readonly Action<string, bool> log;
        private readonly Action<int, string> listenerState;
        private readonly Action<BridgeSession> sessionChanged;
        private readonly ConcurrentDictionary<string, Connection> connections = new ConcurrentDictionary<string, Connection>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private TcpListener listener;
        private readonly object listenerLock = new object();
        private bool disposed;

        public ForgeServer(int firstPort, int lastPort, int payloadCap, JObject welcome,
            Func<string, CancellationToken, Task<bool>> consent,
            Func<JObject, byte[], CancellationToken, Task<PushResult>> import,
            Action<string, bool> log, Action<int, string> listenerState, Action<BridgeSession> sessionChanged,
            Func<JObject, int, Func<JObject, Task>, CancellationToken, Task<bool>> update = null)
        {
            if (firstPort < 1 || lastPort > 65535 || firstPort > lastPort) throw new ArgumentOutOfRangeException(nameof(firstPort));
            if (payloadCap < 1 || payloadCap > ForgeProtocol.MaximumPayload) throw new ArgumentOutOfRangeException(nameof(payloadCap));
            this.firstPort = firstPort;
            this.lastPort = lastPort;
            this.payloadCap = payloadCap;
            this.welcome = welcome;
            this.consent = consent;
            this.import = import;
            this.update = update;
            this.log = log;
            this.listenerState = listenerState;
            this.sessionChanged = sessionChanged;
        }

        public Task StartAsync(int preferredPort = 0) => Task.Run(() => ListenAsync(preferredPort));

        private async Task ListenAsync(int preferredPort)
        {
            var boundPort = 0;
            string lastError = null;
            var candidateCount = lastPort - firstPort + 1;
            for (var i = -1; i < candidateCount; i++)
            {
                var port = i < 0 ? preferredPort : firstPort + i;
                if (port < firstPort || port > lastPort || i >= 0 && port == preferredPort) continue;
                lock (listenerLock)
                {
                    if (disposed) return;
                    try
                    {
                        var candidate = new TcpListener(IPAddress.Loopback, port);
                        candidate.Server.ExclusiveAddressUse = true;
                        try { candidate.Start(); }
                        catch { candidate.Stop(); throw; }
                        listener = candidate;
                        boundPort = port;
                    }
                    catch (SocketException error) { lastError = error.Message; }
                }
                if (boundPort != 0) break;
            }
            if (boundPort == 0)
            {
                var reason = $"no free port in {firstPort}–{lastPort}" + (lastError == null ? "" : ": " + lastError);
                listenerState(0, reason);
                log(reason, true);
                return;
            }
            listenerState(boundPort, null);
            log("listening on 127.0.0.1:" + boundPort, false);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    var connection = new Connection(this, client, boundPort, cancellation.Token);
                    connections[connection.Id] = connection;
                    // Dispose and accept may race; never let the accepted socket outlive the server.
                    if (cancellation.IsCancellationRequested) connection.Dispose();
                    _ = connection.RunAsync();
                }
            }
            catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    listenerState(0, error.Message);
                    log("listener failed: " + error.Message, true);
                }
            }
        }

        public const string NoForgeMessage = "No Forge tab that supports project sync is connected. Open Forge in a browser and connect it to this Unity project.";

        /// <summary>Lists the projects of the most recently connected tab that supports project sync.</summary>
        public Task<ForgeProjectList> ListProjectsAsync(CancellationToken cancellation)
        {
            var connection = SyncConnection();
            if (connection == null) return Task.FromResult(new ForgeProjectList { Kind = ForgeSyncOutcomeKind.NoForge, Error = NoForgeMessage });
            return connection.ListProjectsAsync(cancellation);
        }

        /// <summary>Asks the most recently connected tab that supports project sync to cook and push one Engine Output.
        /// <paramref name="progress"/> runs on a socket worker for each non-terminal status.</summary>
        public Task<ForgePushOutcome> PushProjectAsync(string projectId, string nodeId, Action<ForgePushProgress> progress, CancellationToken cancellation)
        {
            var connection = SyncConnection();
            if (connection == null) return Task.FromResult(new ForgePushOutcome { Kind = ForgeSyncOutcomeKind.NoForge, Error = NoForgeMessage });
            return connection.PushProjectAsync(projectId, nodeId, progress, cancellation);
        }

        /// <summary>True while a tab that advertised project sync is connected and welcomed.</summary>
        public bool HasSyncSession => SyncConnection() != null;

        private Connection SyncConnection() =>
            connections.Values.Where(c => c.ProjectSync).OrderByDescending(c => c.ConnectedAt).FirstOrDefault();

        public const string OriginDeniedMessage = "Denied in Unity. To be asked again, clear it under Denied origins in Window > Forge.";

        public void Revoke(string origin)
        {
            foreach (var connection in connections.Values)
                if (connection.Origin == origin) _ = connection.DenyAsync("origin", "the user revoked this origin");
        }

        public void Dispose()
        {
            lock (listenerLock)
            {
                if (disposed) return;
                disposed = true;
                cancellation.Cancel();
                listener?.Stop();
            }
            foreach (var connection in connections.Values) connection.Dispose();
            connections.Clear();
        }

        private sealed class Connection : IDisposable
        {
            private readonly ForgeServer owner;
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            private readonly int port;
            private readonly CancellationTokenSource lifetime;
            private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
            private readonly BridgeSession session;
            private volatile bool upgraded;
            private bool helloSeen;
            private int closing;
            private int unansweredPings;
            private int importing;
            private int updating;
            private readonly ConcurrentDictionary<string, PendingRequest> requests = new ConcurrentDictionary<string, PendingRequest>();
            public string Id { get; } = Guid.NewGuid().ToString("D");
            public string Origin { get; private set; }
            public volatile bool ProjectSync;
            public DateTime ConnectedAt => session.ConnectedAt;

            public Connection(ForgeServer owner, TcpClient client, int port, CancellationToken cancellation)
            {
                this.owner = owner;
                this.client = client;
                this.port = port;
                stream = client.GetStream();
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                session = new BridgeSession { Id = Id };
            }

            public async Task RunAsync()
            {
                Task heartbeat = null;
                try
                {
                    if (!await UpgradeAsync().ConfigureAwait(false)) return;
                    heartbeat = HeartbeatAsync();
                    await ReceiveAsync().ConfigureAwait(false);
                }
                catch (BridgeProtocolException error)
                {
                    owner.log("connection refused: " + error.Message, true);
                    await CloseAsync(error.CloseCode, error.Message).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
                {
                    if (!lifetime.IsCancellationRequested && !(error is EndOfStreamException)) owner.log("connection failed: " + error.Message, true);
                    session.CloseReason = session.CloseReason ?? "connection closed";
                }
                catch (Exception error)
                {
                    owner.log("connection failed: " + error.Message, true);
                    await CloseAsync(1011, "internal bridge error").ConfigureAwait(false);
                }
                finally
                {
                    Dispose();
                    if (heartbeat != null) await heartbeat.ConfigureAwait(false);
                    owner.connections.TryRemove(Id, out _);
                    FailRequests("Forge disconnected: " + (session.CloseReason ?? "connection closed"));
                    if (upgraded)
                    {
                        session.Connected = false;
                        NotifySession();
                        owner.log("disconnected " + Origin + ": " + (session.CloseReason ?? "connection closed"), false);
                    }
                }
            }

            private async Task<bool> UpgradeAsync()
            {
                using (var head = new MemoryStream())
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    // Closing the socket also cancels ReadAsync on Unity runtimes that ignore its token.
                    using (timeout.Token.Register(() => { if (!upgraded) client.Close(); }))
                    {
                        var buffer = new byte[1024];
                        var end = -1;
                        while (end < 0)
                        {
                            var count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                            if (count == 0) return false;
                            var previous = (int)head.Length;
                            head.Write(buffer, 0, count);
                            var data = head.GetBuffer();
                            for (var i = Math.Max(0, previous - 3); i <= head.Length - 4; i++)
                                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) { end = i + 4; break; }
                            if (head.Length > ForgeHandshake.MaximumHeadBytes || end >= 0 && end != head.Length)
                                return await RefuseAsync(400, "malformed request or data before the upgrade").ConfigureAwait(false);
                        }
                        timeout.CancelAfter(Timeout.Infinite);
                        var decision = ForgeHandshake.Evaluate(Encoding.ASCII.GetString(head.GetBuffer(), 0, end), port);
                        if (!decision.Accepted) return await RefuseAsync(decision.Status, decision.Reason).ConfigureAwait(false);
                        Origin = decision.Origin;
                        var approval = owner.consent(Origin, lifetime.Token);
                        while (!approval.IsCompleted)
                        {
                            if (client.Available > 0) return await RefuseAsync(400, "data before the upgrade completed").ConfigureAwait(false);
                            if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0) return false;
                            await Task.WhenAny(approval, Task.Delay(50, lifetime.Token)).ConfigureAwait(false);
                            lifetime.Token.ThrowIfCancellationRequested();
                        }
                        if (client.Available > 0) return await RefuseAsync(400, "data before the upgrade completed").ConfigureAwait(false);
                        if (!await approval.ConfigureAwait(false)) return await DenyOriginAsync(decision).ConfigureAwait(false);
                        await WriteAsync(ForgeHandshake.Response(decision)).ConfigureAwait(false);
                        upgraded = true;
                        session.Origin = Origin;
                        NotifySession();
                        owner.log("connected " + Origin, false);
                        return true;
                    }
                }
            }

            /// <summary>A browser cannot read an HTTP 403, which looks exactly like the editor going away. Completing
            /// the upgrade only to send `denied` `origin` lets the tab tell a Deny from an editor that is not ready.</summary>
            private async Task<bool> DenyOriginAsync(HandshakeDecision decision)
            {
                await WriteAsync(ForgeHandshake.Response(decision)).ConfigureAwait(false);
                upgraded = true;
                await DenyAsync("origin", OriginDeniedMessage).ConfigureAwait(false);
                return false;
            }

            private async Task<bool> RefuseAsync(int status, string reason)
            {
                owner.log($"HTTP {status}: {reason}", true);
                await WriteAsync(ForgeHandshake.Response(ForgeHandshake.Refuse(status, reason))).ConfigureAwait(false);
                return false;
            }

            private async Task ReceiveAsync()
            {
                ForgeMessageBuffer message = null;
                var frameBytes = new byte[14];
                var chunk = new byte[16 * 1024];
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        await ReadExactAsync(frameBytes, 0, 2).ConfigureAwait(false);
                        WebSocketCodec.ValidatePrefix(frameBytes[0], frameBytes[1]);
                        await ReadExactAsync(frameBytes, 2, WebSocketCodec.HeaderBytes(frameBytes[1]) - 2).ConfigureAwait(false);
                        var frame = WebSocketCodec.DecodeHeader(frameBytes);
                        if (!frame.Control)
                        {
                            if (frame.Opcode == 1) throw new BridgeProtocolException(1003, "send binary protocol messages, not text");
                            if (frame.Opcode == 0 && message == null) throw new BridgeProtocolException(1002, "continuation without a message");
                            if (frame.Opcode == 2 && message != null) throw new BridgeProtocolException(1002, "new message before fragmented message completed");
                            if (frame.Opcode == 2) message = new ForgeMessageBuffer(owner.payloadCap);
                            message.AnnounceFrame(frame.Length);
                        }
                        ulong received = 0;
                        var control = frame.Control ? new byte[(int)frame.Length] : null;
                        while (received < frame.Length)
                        {
                            var count = (int)Math.Min((ulong)chunk.Length, frame.Length - received);
                            // Read the length prefix first so an excessive header/payload is rejected before its body.
                            if (!frame.Control && message.BufferedBytes < 4) count = Math.Min(count, 4 - (int)message.BufferedBytes);
                            await ReadExactAsync(chunk, 0, count).ConfigureAwait(false);
                            WebSocketCodec.Unmask(chunk, count, frame.Mask, received);
                            if (frame.Control) Buffer.BlockCopy(chunk, 0, control, (int)received, count);
                            else message.Append(chunk, 0, count);
                            received += (ulong)count;
                        }
                        if (frame.Control)
                        {
                            if (frame.Opcode == 9) await WriteAsync(WebSocketCodec.Encode(10, control)).ConfigureAwait(false);
                            else if (frame.Opcode == 10) Interlocked.Exchange(ref unansweredPings, 0);
                            else
                            {
                                WebSocketCodec.ValidateClose(control);
                                await WriteAsync(WebSocketCodec.Encode(8, control)).ConfigureAwait(false);
                                session.CloseReason = "closed by the tab";
                                return;
                            }
                        }
                        else if (frame.Final)
                        {
                            var complete = message.Complete();
                            message.Dispose();
                            message = null;
                            await HandleMessageAsync(complete).ConfigureAwait(false);
                        }
                    }
                }
                finally { message?.Dispose(); }
            }

            private async Task HandleMessageAsync(ForgeMessage message)
            {
                var header = message.Header;
                var reference = ForgeProtocol.String(header, "id");
                if (!ForgeProtocol.AcceptsVersion(ForgeProtocol.String(header, "protocol")))
                {
                    await DenyAsync("protocol", "unsupported protocol; this bridge speaks " + ForgeProtocol.Version, reference).ConfigureAwait(false);
                    return;
                }
                var type = ForgeProtocol.String(header, "type");
                if (reference == null || type == null) throw new BridgeProtocolException(1008, "header must contain string id and type");
                if (type == "hello")
                {
                    var problem = helloSeen ? "hello was already sent on this socket" : ForgeProtocol.HelloProblem(header, message.Payload.Length);
                    if (problem != null) { await DenyAsync("hello", problem, reference).ConfigureAwait(false); return; }
                    helloSeen = true;
                    session.Project = ForgeProtocol.String(header, "project.name");
                    session.ProjectSync = ForgeProjectSync.AdvertisesSync(header);
                    session.AppVersion = ForgeProtocol.String(header, "app.version");
                    NotifySession();
                    var response = (JObject)owner.welcome.DeepClone();
                    response["id"] = Guid.NewGuid().ToString("D");
                    response["ref"] = reference;
                    await SendAsync(response).ConfigureAwait(false);
                    ProjectSync = session.ProjectSync;
                    return;
                }
                if (!helloSeen) { await DenyAsync("hello", "hello must be the first message", reference).ConfigureAwait(false); return; }
                if (type == "update")
                {
                    if (Interlocked.CompareExchange(ref updating, 1, 0) != 0)
                    {
                        var failed = ForgeProtocol.Header("update-failed", reference);
                        failed["message"] = "A bridge update is already running; wait for Unity to finish";
                        await SendAsync(failed).ConfigureAwait(false);
                    }
                    else _ = UpdateAsync(message);
                    return;
                }
                if (Array.IndexOf(ForgeProjectSync.ReplyTypes, type) >= 0)
                {
                    RouteReply(header);
                    return;
                }
                if (type != "push") throw new BridgeProtocolException(1008, "unexpected message type " + type);
                var envelopeProblem = ForgeProtocol.PushProblem(header, message.Payload.Length);
                if (envelopeProblem != null)
                {
                    await ReportResultAsync(reference, PushResult.Failure(envelopeProblem)).ConfigureAwait(false);
                    return;
                }
                if (Interlocked.CompareExchange(ref importing, 1, 0) != 0)
                {
                    await ReportResultAsync(reference, PushResult.Failure("a push from this session is still importing; wait for its result")).ConfigureAwait(false);
                    return;
                }
                // Continue reading control frames while Unity imports. One outstanding import per session bounds the queue.
                _ = ImportAsync(message);
            }

            public Task<ForgeProjectList> ListProjectsAsync(CancellationToken cancellation)
            {
                var projects = new List<ForgeProject>();
                return RequestAsync(ForgeProjectSync.ListProjects(), reply =>
                {
                    var type = ForgeProtocol.String(reply, "type");
                    if (type == "project") projects.Add(ForgeProjectSync.Project(reply));
                    else if (type == "projects-complete") return new ForgeProjectList { Kind = ForgeSyncOutcomeKind.Succeeded, Projects = projects };
                    else if (type == "project-status" && ForgeProjectSync.IsTerminal(reply))
                        return new ForgeProjectList { Kind = ForgeSyncOutcomeKind.Failed, Error = ForgeProjectSync.Outcome(reply).Error ?? "Forge ended the project list without projects-complete." };
                    return null;
                }, reason => new ForgeProjectList { Kind = ForgeSyncOutcomeKind.Failed, Error = reason }, cancellation);
            }

            public Task<ForgePushOutcome> PushProjectAsync(string projectId, string nodeId, Action<ForgePushProgress> progress, CancellationToken cancellation)
            {
                return RequestAsync(ForgeProjectSync.PushProject(projectId, nodeId), reply =>
                {
                    if (ForgeProtocol.String(reply, "type") != "project-status") return null;
                    if (ForgeProjectSync.IsTerminal(reply)) return ForgeProjectSync.Outcome(reply);
                    progress?.Invoke(ForgeProjectSync.Progress(reply));
                    return null;
                }, reason => new ForgePushOutcome { Kind = ForgeSyncOutcomeKind.Failed, Error = reason }, cancellation);
            }

            /// <summary>Sends a request and completes with the first reply <paramref name="reply"/> turns into a result;
            /// replies it answers with null are progress. A disconnect completes it through <paramref name="failure"/>.</summary>
            private async Task<T> RequestAsync<T>(JObject request, Func<JObject, T> reply, Func<string, T> failure, CancellationToken cancellation) where T : class
            {
                var id = ForgeProtocol.String(request, "id");
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                requests[id] = new PendingRequest
                {
                    Reply = header =>
                    {
                        var result = reply(header);
                        if (result == null) return false;
                        completion.TrySetResult(result);
                        return true;
                    },
                    Fail = reason => completion.TrySetResult(failure(reason)),
                };
                using (cancellation.Register(() => { requests.TryRemove(id, out _); completion.TrySetCanceled(); }))
                {
                    // A disconnect before this point has already failed every request it knew of.
                    if (lifetime.IsCancellationRequested && requests.TryRemove(id, out _)) return failure("Forge disconnected.");
                    try { await SendAsync(request).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
                    {
                        requests.TryRemove(id, out _);
                        cancellation.ThrowIfCancellationRequested();
                        return failure("Forge disconnected before the request was sent.");
                    }
                    return await completion.Task.ConfigureAwait(false);
                }
            }

            private void RouteReply(JObject header)
            {
                var reference = ForgeProtocol.String(header, "ref");
                if (reference == null || !requests.TryGetValue(reference, out var pending))
                {
                    owner.log("ignored " + ForgeProtocol.String(header, "type") + " for a request that is no longer waiting", false);
                    return;
                }
                if (pending.Reply(header)) requests.TryRemove(reference, out _);
            }

            private void FailRequests(string reason)
            {
                foreach (var id in requests.Keys.ToArray())
                    if (requests.TryRemove(id, out var pending)) pending.Fail(reason);
            }

            private sealed class PendingRequest
            {
                /// <summary>Returns true when the reply ends the request.</summary>
                public Func<JObject, bool> Reply;
                public Action<string> Fail;
            }

            private async Task UpdateAsync(ForgeMessage message)
            {
                try
                {
                    if (owner.update != null)
                        await owner.update(message.Header, message.Payload.Length, SendAsync, lifetime.Token).ConfigureAwait(false);
                    else
                    {
                        var failed = ForgeProtocol.Header("update-failed", ForgeProtocol.String(message.Header, "id"));
                        failed["message"] = "This bridge cannot update itself";
                        await SendAsync(failed).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        owner.log("bridge update failed: " + error.Message, true);
                        await CloseAsync(1011, "bridge update failed").ConfigureAwait(false);
                    }
                }
                finally { Interlocked.Exchange(ref updating, 0); }
            }

            private async Task ImportAsync(ForgeMessage message)
            {
                try
                {
                    PushResult result;
                    try { result = await owner.import(message.Header, message.Payload, lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                    catch (Exception error) { result = PushResult.Failure(error.Message); }
                    await ReportResultAsync(ForgeProtocol.String(message.Header, "id"), result ?? PushResult.Failure("import returned no result")).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        owner.log("could not send push result: " + error.Message, true);
                        await CloseAsync(error is BridgeProtocolException protocol ? protocol.CloseCode : (ushort)1011,
                            "could not send push result").ConfigureAwait(false);
                    }
                }
                finally { Interlocked.Exchange(ref importing, 0); }
            }

            private async Task ReportResultAsync(string reference, PushResult result)
            {
                var failed = !string.IsNullOrEmpty(result.Error);
                owner.log(failed ? "push failed: " + result.Error : (result.Created ? "created " : "refreshed ") + result.AssetPath, failed);
                if (result.Warnings != null) foreach (var warning in result.Warnings) owner.log("push warning: " + warning, false);
                await SendAsync(ForgeProtocol.Result(reference, result)).ConfigureAwait(false);
            }

            public async Task DenyAsync(string reason, string message, string reference = null)
            {
                owner.log("denied " + reason + ": " + message, true);
                try
                {
                    if (upgraded) await SendAsync(ForgeProtocol.Denied(reason, message, reference)).ConfigureAwait(false);
                    else await RefuseAsync(403, message).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException) { }
                await CloseAsync(1008, message).ConfigureAwait(false);
            }

            private async Task HeartbeatAsync()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
                        if (Interlocked.CompareExchange(ref unansweredPings, 0, 0) >= 2)
                        {
                            owner.log("connection closed: no pong to 2 pings", true);
                            await CloseAsync(1001, "no pong to 2 pings").ConfigureAwait(false);
                            return;
                        }
                        Interlocked.Increment(ref unansweredPings);
                        await WriteAsync(WebSocketCodec.Encode(9, Array.Empty<byte>())).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
                {
                    Dispose();
                }
            }

            private async Task ReadExactAsync(byte[] bytes, int offset, int count)
            {
                while (count > 0)
                {
                    var read = await stream.ReadAsync(bytes, offset, count, lifetime.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                    count -= read;
                }
            }

            private Task SendAsync(JObject header) => WriteAsync(WebSocketCodec.Encode(2, ForgeProtocol.Encode(header)));

            private async Task WriteAsync(byte[] bytes)
            {
                await writeLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try { await stream.WriteAsync(bytes, 0, bytes.Length, lifetime.Token).ConfigureAwait(false); }
                finally { writeLock.Release(); }
            }

            private async Task CloseAsync(ushort code, string reason)
            {
                if (Interlocked.Exchange(ref closing, 1) != 0) return;
                session.CloseReason = reason;
                try { if (upgraded) await WriteAsync(WebSocketCodec.Close(code, reason)).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException) { }
                finally { Dispose(); }
            }

            private void NotifySession()
            {
                owner.sessionChanged(new BridgeSession
                {
                    Id = session.Id, Origin = session.Origin, Project = session.Project, AppVersion = session.AppVersion,
                    ConnectedAt = session.ConnectedAt, Connected = session.Connected, CloseReason = session.CloseReason,
                    ProjectSync = session.ProjectSync
                });
            }

            public void Dispose()
            {
                lifetime.Cancel();
                client.Close();
            }
        }
    }
}
