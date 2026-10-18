using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ForgeBridge.Editor
{
    public sealed class ForgeWindow : EditorWindow
    {
        private Vector2 scroll;
        private List<ForgeProject> projects = new List<ForgeProject>();
        private string syncStatus;
        private bool syncError;
        private bool syncBusy;

        [MenuItem("Window/Forge")]
        public static void Open() => GetWindow<ForgeWindow>("Forge");

        private void OnEnable()
        {
            minSize = new Vector2(380, 300);
            BridgeService.Changed += Repaint;
        }

        private void OnDisable() => BridgeService.Changed -= Repaint;
        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Forge Bridge for Unity", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(BridgeService.ListenerState, BridgeService.ListenerPort > 0 ? MessageType.Info : MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Project settings")) SettingsService.OpenProjectSettings("Project/Forge");
                if (GUILayout.Button("Restart listener")) BridgeService.Restart();
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sessions", EditorStyles.boldLabel);
            var sessions = BridgeService.Sessions.ToArray();
            if (sessions.Length == 0) EditorGUILayout.LabelField("No Forge tab is connected.", EditorStyles.wordWrappedLabel);
            foreach (var session in sessions)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(session.Origin, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(session.Project + " · connected " + Math.Max(0, (int)(DateTime.UtcNow - session.ConnectedAt).TotalSeconds) + " s ago" +
                        (session.ProjectSync ? " · project sync" : ""), EditorStyles.wordWrappedMiniLabel);
                }
            }
            DrawProjectSync();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Allowed origins", EditorStyles.boldLabel);
            var origins = BridgeUserState.instance.AllowedOrigins.ToArray();
            if (origins.Length == 0) EditorGUILayout.LabelField("The Allow dialog asks on first contact.", EditorStyles.wordWrappedLabel);
            foreach (var origin in origins)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(origin, EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("Revoke", GUILayout.Width(65))) BridgeService.Revoke(origin);
                }
            }
            var denied = BridgeService.DeniedOrigins.ToArray();
            if (denied.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Denied origins", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Refused without asking until Unity restarts. Clear one to be asked again.", EditorStyles.wordWrappedMiniLabel);
                foreach (var origin in denied)
                {
                    using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField(origin, EditorStyles.wordWrappedLabel);
                        if (GUILayout.Button("Clear", GUILayout.Width(65))) BridgeService.ClearDenied(origin);
                    }
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Recent events", EditorStyles.boldLabel);
            foreach (var entry in BridgeUserState.instance.Events)
            {
                var at = DateTime.TryParse(entry.At, out var time) ? time.ToLocalTime().ToString("HH:mm:ss") : entry.At;
                EditorGUILayout.HelpBox(at + "  " + entry.Message, entry.Error ? MessageType.Warning : MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Lists the connected Forge's projects and pushes one Engine Output on request (protocol 1.4).</summary>
        private void DrawProjectSync()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Project sync", EditorStyles.boldLabel);
            if (!ForgeSync.Connected) EditorGUILayout.LabelField("No connected Forge tab supports project sync.", EditorStyles.wordWrappedLabel);
            using (new EditorGUI.DisabledScope(syncBusy || !ForgeSync.Connected))
            {
                if (GUILayout.Button("List Forge projects")) ListProjects();
                foreach (var project in projects)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField(string.IsNullOrEmpty(project.Folder) ? project.Name : project.Folder + " / " + project.Name, EditorStyles.boldLabel);
                        if (project.Outputs.Count == 0) EditorGUILayout.LabelField("No Engine Output node.", EditorStyles.miniLabel);
                        foreach (var output in project.Outputs)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.LabelField(output.Name, EditorStyles.wordWrappedLabel);
                                if (GUILayout.Button("Push", GUILayout.Width(65))) RequestPush(project, output);
                            }
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(syncStatus)) EditorGUILayout.HelpBox(syncStatus, syncError ? MessageType.Warning : MessageType.Info);
        }

        private async void ListProjects()
        {
            SetSyncStatus("Listing Forge projects…", false, true);
            var list = await ForgeSync.ListProjectsAsync();
            projects = list.Projects;
            if (list.Kind == ForgeSyncOutcomeKind.Succeeded) SetSyncStatus(list.Projects.Count + " Forge projects.", false, false);
            else SetSyncStatus(list.Error, true, false);
        }

        private async void RequestPush(ForgeProject project, ForgeProjectOutput output)
        {
            string name = project.Name + " / " + output.Name;
            SetSyncStatus("Asking Forge to push " + name + "…", false, true);
            var outcome = await ForgeSync.RequestPushAsync(project.Id, output.Id, step => SetSyncStatus(step.WaitingForForge
                ? "Waiting for Forge: allow access to " + (step.Source ?? "a source file") + " in the Forge tab."
                : name + ": " + step.Status + "…", false, true));
            if (outcome.Kind == ForgeSyncOutcomeKind.Succeeded) SetSyncStatus("Pushed " + name + " to " + outcome.AssetPath + ".", false, false);
            else SetSyncStatus(outcome.Error, true, false);
        }

        private void SetSyncStatus(string status, bool error, bool busy)
        {
            syncStatus = status;
            syncError = error;
            syncBusy = busy;
            Repaint();
        }
    }
}
