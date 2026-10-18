using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ForgeBridge.Editor
{
    /// <summary>A Forge project as `list-projects` reports it: what a game can bind to and request a push of.</summary>
    public sealed class ForgeProject
    {
        public string Id;
        public string Name;
        /// <summary>Folder names joined by " / ", or "" at the root of the Forge project list.</summary>
        public string Folder;
        public List<ForgeProjectOutput> Outputs = new List<ForgeProjectOutput>();
    }

    /// <summary>An Engine Output node. Its id is the instance path joined by "/", including subnets.</summary>
    public sealed class ForgeProjectOutput
    {
        public string Id;
        public string Name;
    }

    public enum ForgeSyncOutcomeKind { Succeeded, Failed, NoForge }

    /// <summary>The answer to `list-projects`: the projects, or why there are none.</summary>
    public sealed class ForgeProjectList
    {
        public ForgeSyncOutcomeKind Kind;
        public List<ForgeProject> Projects = new List<ForgeProject>();
        public string Error;
    }

    /// <summary>A progress step of a requested push: preparing, waiting, cooking or sending. While
    /// `waiting`, Forge shows a dialog asking for access to <see cref="Source"/> and the push resumes on Allow.</summary>
    public sealed class ForgePushProgress
    {
        public string Status;
        public string Source;
        public string Message;
        public bool WaitingForForge => Status == "waiting";
    }

    /// <summary>How a requested push ended. A push that succeeded has been imported by the time this is reported.</summary>
    public sealed class ForgePushOutcome
    {
        public ForgeSyncOutcomeKind Kind;
        public string AssetPath = "";
        public bool Created;
        public List<string> Warnings = new List<string>();
        public string Error;
    }

    /// <summary>The 1.4 project-sync messages: requests Unity sends and the replies Forge answers them with.</summary>
    public static class ForgeProjectSync
    {
        public const string Capability = "project-sync";
        public static readonly string[] ReplyTypes = { "project", "projects-complete", "project-status" };

        public static JObject ListProjects() => ForgeProtocol.Header("list-projects");

        public static JObject PushProject(string projectId, string nodeId)
        {
            var header = ForgeProtocol.Header("push-project");
            header["projectId"] = projectId;
            header["nodeId"] = nodeId;
            return header;
        }

        public static bool AdvertisesSync(JObject hello) =>
            hello["capabilities"] is JArray capabilities && capabilities.Any(c => c.Type == JTokenType.String && (string)c == Capability);

        public static ForgeProject Project(JObject reply)
        {
            var project = reply["project"] as JObject;
            var id = project != null ? ForgeProtocol.String(project, "id") : null;
            if (string.IsNullOrEmpty(id)) throw new BridgeProtocolException(1008, "project reply is missing project.id");
            var result = new ForgeProject { Id = id, Name = ForgeProtocol.String(project, "name") ?? id, Folder = ForgeProtocol.String(project, "folder") ?? "" };
            foreach (var output in (project["outputs"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var outputId = ForgeProtocol.String(output, "id");
                if (!string.IsNullOrEmpty(outputId)) result.Outputs.Add(new ForgeProjectOutput { Id = outputId, Name = ForgeProtocol.String(output, "name") ?? outputId });
            }
            return result;
        }

        public static ForgePushProgress Progress(JObject status) => new ForgePushProgress
        {
            Status = ForgeProtocol.String(status, "status"), Source = ForgeProtocol.String(status, "source"), Message = ForgeProtocol.String(status, "message")
        };

        public static bool IsTerminal(JObject status)
        {
            var value = ForgeProtocol.String(status, "status");
            return value == "complete" || value == "failed";
        }

        public static ForgePushOutcome Outcome(JObject status)
        {
            var ok = status["ok"];
            var complete = ForgeProtocol.String(status, "status") == "complete" && !(ok?.Type == JTokenType.Boolean && !(bool)ok);
            var outcome = new ForgePushOutcome
            {
                Kind = complete ? ForgeSyncOutcomeKind.Succeeded : ForgeSyncOutcomeKind.Failed,
                AssetPath = ForgeProtocol.String(status, "assetPath") ?? "",
                Created = status["created"]?.Type == JTokenType.Boolean && (bool)status["created"],
                Warnings = (status["warnings"] as JArray ?? new JArray()).Where(w => w.Type == JTokenType.String).Select(w => (string)w).ToList(),
            };
            if (!complete) outcome.Error = ForgeProtocol.String(status, "error") ?? ForgeProtocol.String(status, "message") ?? "Forge reported a failed push without a reason.";
            return outcome;
        }
    }
}
