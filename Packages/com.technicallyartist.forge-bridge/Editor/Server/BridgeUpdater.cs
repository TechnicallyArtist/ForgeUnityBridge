using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;

namespace ForgeBridge.Editor
{
    /// <summary>Main-thread package update policy and Package Manager adapter.</summary>
    public static class BridgeUpdater
    {
        public const string GitBase = "https://github.com/TechnicallyArtist/ForgeUnityBridge.git?path=Packages/com.technicallyartist.forge-bridge";

        public static bool CanSelfUpdate(PackageSource source, string packageId)
        {
            const string prefix = "com.technicallyartist.forge-bridge@";
            if (source != PackageSource.Git || packageId == null || !packageId.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var url = packageId.Substring(prefix.Length).Split('#')[0];
            return string.Equals(url, GitBase, StringComparison.OrdinalIgnoreCase);
        }

        public static bool InstalledFromForge()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeUpdater).Assembly);
            return package != null && CanSelfUpdate(package.source, package.packageId);
        }

        public static string Problem(JObject header, int payloadBytes, string current, bool capable, bool playing, bool compiling, bool importing)
        {
            if (ForgeProtocol.String(header, "protocol") != "1.3") return "Bridge updates require protocol 1.3";
            if (payloadBytes != 0) return "An update must not carry a payload";
            if (!capable) return "This bridge is a local, embedded, or fork install; update it manually";
            var version = ForgeProtocol.String(header, "version");
            const string pattern = @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z";
            if (version == null || !Regex.IsMatch(version, pattern) || !Version.TryParse(version, out var requested) ||
                !Version.TryParse(current, out var installed) || requested <= installed)
                return "Choose a strict x.y.z version newer than " + current;
            if (playing) return "Exit Play Mode, then click Update again";
            if (compiling) return "Unity is compiling; try again in a moment";
            if (importing) return "Unity is importing; try again in a moment";
            return null;
        }

        public static async Task RunAsync(JObject header, string problem, Func<JObject, Task> reply,
            Func<string, Task<string>> add, Action<string, bool> log)
        {
            var reference = ForgeProtocol.String(header, "id");
            var version = ForgeProtocol.String(header, "version");
            if (problem == null)
            {
                log("bridge update requested: " + version, false);
                await reply(ForgeProtocol.Header("update-started", reference));
                try { problem = await add(GitBase + "#" + version); }
                catch (Exception error) { problem = error.Message; }
            }
            if (problem == null) return; // Package resolution reloads the domain; welcome confirms the new version.
            log("bridge update failed: " + problem, true);
            var failed = ForgeProtocol.Header("update-failed", reference);
            failed["message"] = problem;
            await reply(failed);
        }

        public static Task<string> AddPackageAsync(string url)
        {
            var request = Client.Add(url);
            var completion = new TaskCompletionSource<string>();
            void Poll()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Poll;
                completion.TrySetResult(request.Status == StatusCode.Success ? null : request.Error?.message ?? "Package Manager failed");
            }
            EditorApplication.update += Poll;
            return completion.Task;
        }
    }
}
