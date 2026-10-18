using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeBridge.Editor
{
    /// <summary>
    /// Editor code's way to ask a connected Forge for its projects and for a push (protocol 1.4 project sync).
    /// Call from the main thread; results and progress arrive on the main thread. The push itself is imported like
    /// any other, so it also raises <see cref="Import.ForgeImporter.ImportCompleted"/> before its outcome is reported.
    /// </summary>
    public static class ForgeSync
    {
        public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

        /// <summary>True while a Forge tab that supports project sync is connected.</summary>
        public static bool Connected => BridgeService.Sessions.Any(s => s.ProjectSync);

        /// <summary>Lists the projects of the most recently connected Forge tab, with their Engine Output nodes.</summary>
        public static async Task<ForgeProjectList> ListProjectsAsync(CancellationToken cancellation = default)
        {
            var server = BridgeService.Server;
            if (server == null) return new ForgeProjectList { Kind = ForgeSyncOutcomeKind.NoForge, Error = ForgeServer.NoForgeMessage };
            ForgeProjectList list;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(ListTimeout);
                try { list = await server.ListProjectsAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    list = new ForgeProjectList { Kind = ForgeSyncOutcomeKind.Failed, Error = $"Forge did not finish listing its projects within {ListTimeout.TotalSeconds:0} s." };
                }
            }
            if (list.Kind == ForgeSyncOutcomeKind.Failed) BridgeService.RecordEvent("project list failed: " + list.Error, true);
            return list;
        }

        /// <summary>
        /// Asks Forge to cook project <paramref name="projectId"/>'s Engine Output <paramref name="nodeId"/> and push it here.
        /// <paramref name="progress"/> receives preparing / waiting / cooking / sending; `waiting` means Forge shows a dialog
        /// asking for source file access and the push resumes when the user allows it there. No overall timeout applies,
        /// because waiting lasts as long as the user takes; cancel through <paramref name="cancellation"/>.
        /// </summary>
        public static async Task<ForgePushOutcome> RequestPushAsync(string projectId, string nodeId,
            Action<ForgePushProgress> progress = null, CancellationToken cancellation = default)
        {
            if (string.IsNullOrEmpty(projectId)) throw new ArgumentException("A Forge project id is required.", nameof(projectId));
            if (string.IsNullOrEmpty(nodeId)) throw new ArgumentException("An Engine Output node id is required.", nameof(nodeId));
            var server = BridgeService.Server;
            if (server == null) return new ForgePushOutcome { Kind = ForgeSyncOutcomeKind.NoForge, Error = ForgeServer.NoForgeMessage };
            BridgeService.RecordEvent("requested a push of project " + projectId + " output " + nodeId);
            var finished = false;
            var outcome = await server.PushProjectAsync(projectId, nodeId, step => BridgeService.Post(() =>
            {
                // A status posted just before the outcome may drain after it; it is stale by then.
                if (finished) return;
                if (step.WaitingForForge) BridgeService.RecordEvent("waiting for Forge: allow access to " + (step.Source ?? "a source file") + " in the Forge tab");
                progress?.Invoke(step);
            }), cancellation);
            finished = true;
            if (outcome.Kind == ForgeSyncOutcomeKind.Succeeded) BridgeService.RecordEvent("requested push finished: " + outcome.AssetPath);
            else BridgeService.RecordEvent("requested push failed: " + outcome.Error, true);
            return outcome;
        }
    }
}
