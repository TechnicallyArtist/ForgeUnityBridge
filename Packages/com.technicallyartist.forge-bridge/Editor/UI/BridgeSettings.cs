using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ForgeBridge.Editor
{
    [FilePath("ProjectSettings/ForgeBridgeSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class BridgeSettings : ScriptableSingleton<BridgeSettings>
    {
        [SerializeField] private string forgeFolder = "Assets/Forge";
        [SerializeField] private int firstPort = 7042;
        [SerializeField] private int lastPort = 7049;
        [SerializeField] private int payloadCapMiB = 256;
        public string ForgeFolder { get => forgeFolder; set => forgeFolder = value; }
        public int FirstPort { get => firstPort; set => firstPort = value; }
        public int LastPort { get => lastPort; set => lastPort = value; }
        public int PayloadCapMiB { get => payloadCapMiB; set => payloadCapMiB = value; }

        public string ValidationProblem()
        {
            if (string.IsNullOrWhiteSpace(forgeFolder) || forgeFolder != "Assets" && !forgeFolder.StartsWith("Assets/", StringComparison.Ordinal))
                return "The Forge folder must be Assets or a folder under Assets, using forward slashes.";
            foreach (var segment in forgeFolder.Split('/'))
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains("\\"))
                    return "The Forge folder contains an invalid path segment.";
            if (firstPort < 1 || lastPort > 65535 || firstPort > lastPort) return "Choose a port range between 1 and 65535, with the first port no higher than the last.";
            if (payloadCapMiB < 1 || payloadCapMiB > 256) return "The payload cap must be between 1 and 256 MiB.";
            return null;
        }

        public void SaveSettings()
        {
            var problem = ValidationProblem();
            if (problem != null) throw new InvalidOperationException(problem);
            Save(true);
        }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Forge", SettingsScope.Project)
            {
                label = "Forge",
                keywords = new HashSet<string> { "Forge", "bridge", "port", "payload", "folder" },
                guiHandler = _ => DrawSettings()
            };
        }

        private static void DrawSettings()
        {
            var settings = instance;
            EditorGUILayout.LabelField("Forge Bridge for Unity", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            settings.ForgeFolder = EditorGUILayout.TextField("Forge folder", settings.ForgeFolder);
            settings.FirstPort = EditorGUILayout.IntField("First port", settings.FirstPort);
            settings.LastPort = EditorGUILayout.IntField("Last port", settings.LastPort);
            settings.PayloadCapMiB = EditorGUILayout.IntField("Payload cap (MiB)", settings.PayloadCapMiB);
            EditorGUILayout.HelpBox("The listener binds only to 127.0.0.1. Applying settings restarts the listener and closes existing sessions.", MessageType.Info);
            var problem = settings.ValidationProblem();
            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
            using (new EditorGUI.DisabledScope(problem != null))
            {
                if (GUILayout.Button("Apply and restart listener", GUILayout.Width(220)))
                {
                    settings.SaveSettings();
                    BridgeService.Restart();
                }
            }
        }
    }

    [Serializable]
    public sealed class BridgeEvent
    {
        public string At;
        public string Message;
        public bool Error;
    }

    [FilePath("UserSettings/ForgeBridgeUserState.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class BridgeUserState : ScriptableSingleton<BridgeUserState>
    {
        [SerializeField] private List<string> allowedOrigins = new List<string>();
        [SerializeField] private List<BridgeEvent> events = new List<BridgeEvent>();
        public IReadOnlyList<string> AllowedOrigins => allowedOrigins;
        public IReadOnlyList<BridgeEvent> Events => events;

        public bool IsAllowed(string origin) => allowedOrigins.Contains(origin);

        public void Allow(string origin)
        {
            if (!ForgeHandshake.TryNormalizeOrigin(origin, out var normalized)) throw new ArgumentException("Not a known Forge origin", nameof(origin));
            if (!allowedOrigins.Contains(normalized)) { allowedOrigins.Add(normalized); Save(true); }
        }

        public void Revoke(string origin)
        {
            if (allowedOrigins.Remove(origin)) Save(true);
        }

        public void Record(string message, bool error)
        {
            events.Insert(0, new BridgeEvent { At = DateTime.UtcNow.ToString("O"), Message = message, Error = error });
            if (events.Count > 100) events.RemoveRange(100, events.Count - 100);
            Save(true);
        }
    }
}
