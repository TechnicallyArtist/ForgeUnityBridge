using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ForgeBridge.Editor
{
    [InitializeOnLoad]
    public static class BridgeService
    {
        /// <summary>Invoked on the editor main thread. Async continuations retain Unity's synchronization context.</summary>
        public static Func<JObject, byte[], Task<PushResult>> ImportAsync;

        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
        private static readonly Dictionary<string, BridgeSession> SessionRows = new Dictionary<string, BridgeSession>();
        private static readonly SemaphoreSlim ImportGate = new SemaphoreSlim(1, 1);
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        private const string DeniedOriginsKey = "ForgeBridge.DeniedOrigins";
        private static ForgeServer server;
        private static int generation;
        public static int ListenerPort { get; private set; }
        public static string ListenerReason { get; private set; } = "starting";
        public static IEnumerable<BridgeSession> Sessions => SessionRows.Values;
        public static event Action Changed;
        public const string PluginVersion = "0.3.0";
        /// <summary>The running listener, for ForgeSync's requests; null while stopped.</summary>
        internal static ForgeServer Server => server;
        /// <summary>Origins denied since the editor started. SessionState survives domain reloads, not a restart.</summary>
        public static IEnumerable<string> DeniedOrigins => SessionState.GetString(DeniedOriginsKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        public static string ListenerState => ListenerPort > 0 ? "listening on 127.0.0.1:" + ListenerPort : "not listening: " + ListenerReason;

        static BridgeService()
        {
            EditorApplication.update += DrainMainThread;
            EditorApplication.delayCall += Restart;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        public static void Restart()
        {
            Stop();
            SessionRows.Clear();
            var settings = BridgeSettings.instance;
            var problem = settings.ValidationProblem();
            if (problem != null)
            {
                ListenerReason = problem;
                RecordEvent(problem, true);
                return;
            }
            ListenerReason = "starting";
            var current = generation;
            var info = ForgeProtocol.Welcome(null, ProjectIdentity(PlayerSettings.productGUID.ToString(), Path.GetFullPath(".")),
                PlayerSettings.productName, Application.unityVersion, PluginVersion, BridgeUpdater.InstalledFromForge());
            server = new ForgeServer(settings.FirstPort, settings.LastPort, settings.PayloadCapMiB * 1024 * 1024, info,
                AskConsentAsync, ImportOnMainThreadAsync,
                (message, error) => EnqueueCurrent(current, () => RecordEvent(message, error)),
                (port, reason) => EnqueueCurrent(current, () =>
                {
                    ListenerPort = port;
                    ListenerReason = reason;
                    if (port > 0) SessionState.SetInt("ForgeBridge.ListenerPort", port);
                    Changed?.Invoke();
                }),
                session => EnqueueCurrent(current, () =>
                {
                    if (session.Connected) SessionRows[session.Id] = session;
                    else SessionRows.Remove(session.Id);
                    Changed?.Invoke();
                }), UpdateOnMainThreadAsync);
            _ = server.StartAsync(SessionState.GetInt("ForgeBridge.ListenerPort", 0));
            Changed?.Invoke();
        }

        public static void Stop()
        {
            generation++;
            server?.Dispose();
            server = null;
            ListenerPort = 0;
            ListenerReason = "stopped";
        }

        public static string ProjectIdentity(string productGuid, string projectPath)
        {
            if (Guid.TryParse(productGuid, out var guid) && guid != Guid.Empty) return guid.ToString("N");
            // Normalize Windows paths so case and separators cannot change a fallback identity.
            var path = Path.GetFullPath(projectPath).TrimEnd('/', '\\').Replace('\\', '/');
            if (Path.DirectorySeparatorChar == '\\') path = path.ToLowerInvariant();
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        public static void Revoke(string origin)
        {
            BridgeUserState.instance.Revoke(origin);
            server?.Revoke(origin);
            RecordEvent("revoked " + origin, false);
        }

        public static void RememberDenied(string origin)
        {
            if (!DeniedOrigins.Contains(origin)) SessionState.SetString(DeniedOriginsKey, string.Join("\n", DeniedOrigins.Append(origin)));
            RecordEvent("denied " + origin + " until Unity restarts or it is cleared in Window > Forge", true);
        }

        public static void ClearDenied(string origin)
        {
            SessionState.SetString(DeniedOriginsKey, string.Join("\n", DeniedOrigins.Where(item => item != origin)));
            RecordEvent("cleared denial of " + origin + "; it is asked again on its next connection");
        }

        public static void RecordEvent(string message, bool error = false)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId) { MainThreadQueue.Enqueue(() => RecordEvent(message, error)); return; }
            BridgeUserState.instance.Record(message, error);
            if (error) Debug.LogWarning("[Forge] " + message);
            else Debug.Log("[Forge] " + message);
            Changed?.Invoke();
        }

        /// <summary>True for an approved origin, false for one denied this editor session (answered without a
        /// dialog, so a tab that keeps retrying cannot nag), null when the user must be asked.</summary>
        public static bool? RememberedConsent(string origin)
        {
            if (BridgeUserState.instance.IsAllowed(origin)) return true;
            if (DeniedOrigins.Contains(origin)) return false;
            return null;
        }

        private static Task<bool> AskConsentAsync(string origin, CancellationToken cancellation)
        {
            return OnMainThreadAsync(() =>
            {
                var remembered = RememberedConsent(origin);
                if (remembered.HasValue) return Task.FromResult(remembered.Value);
                if (Application.isBatchMode) return Task.FromResult(false);
                var allowed = EditorUtility.DisplayDialog("Allow Forge to connect?",
                    origin + "\n\nAllow this Forge origin to push assets into this Unity project?", "Allow", "Deny");
                if (allowed)
                {
                    BridgeUserState.instance.Allow(origin);
                    RecordEvent("allowed " + origin);
                }
                else RememberDenied(origin);
                return Task.FromResult(allowed);
            }, cancellation);
        }

        private static Task<bool> UpdateOnMainThreadAsync(JObject header, int payloadBytes, Func<JObject, Task> reply, CancellationToken cancellation)
        {
            return OnMainThreadAsync(async () =>
            {
                var acquired = ImportGate.Wait(0);
                try
                {
                    var problem = BridgeUpdater.Problem(header, payloadBytes, PluginVersion, BridgeUpdater.InstalledFromForge(),
                        EditorApplication.isPlayingOrWillChangePlaymode, EditorApplication.isCompiling, EditorApplication.isUpdating || !acquired);
                    await BridgeUpdater.RunAsync(header, problem, reply, BridgeUpdater.AddPackageAsync, RecordEvent);
                    return true;
                }
                finally { if (acquired) ImportGate.Release(); }
            }, cancellation);
        }

        private static async Task<PushResult> ImportOnMainThreadAsync(JObject header, byte[] payload, CancellationToken cancellation)
        {
            // Serialize AssetDatabase work across sessions while each socket can still process pings and closes.
            await ImportGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await OnMainThreadAsync(() => ImportAsync != null ? ImportAsync(header, payload) :
                    Task.FromResult(PushResult.Failure("this Forge Bridge for Unity cannot import yet")), cancellation).ConfigureAwait(false);
            }
            finally { ImportGate.Release(); }
        }

        private static async Task<T> OnMainThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            using (cancellation.Register(() =>
            {
                // An import already writing assets must finish before another session enters ImportGate.
                // Cancellation can remove queued work, but cannot release an in-progress Unity operation.
                if (Interlocked.CompareExchange(ref started, -1, 0) == 0) completion.TrySetCanceled();
            }))
            {
                MainThreadQueue.Enqueue(async () =>
                {
                    if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
                    try { completion.TrySetResult(await work()); }
                    catch (Exception error) { completion.TrySetException(error); }
                });
                return await completion.Task.ConfigureAwait(false);
            }
        }

        /// <summary>Runs <paramref name="action"/> on the editor main thread.</summary>
        internal static void Post(Action action) => MainThreadQueue.Enqueue(action);

        private static void EnqueueCurrent(int current, Action action) => MainThreadQueue.Enqueue(() => { if (generation == current) action(); });

        private static void DrainMainThread()
        {
            for (var i = 0; i < 64 && MainThreadQueue.TryDequeue(out var action); i++)
            {
                try { action(); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }
    }
}
