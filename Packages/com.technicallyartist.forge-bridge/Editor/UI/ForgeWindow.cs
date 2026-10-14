using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ForgeBridge.Editor
{
    public sealed class ForgeWindow : EditorWindow
    {
        private Vector2 scroll;

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
                    EditorGUILayout.LabelField(session.Project + " · connected " + Math.Max(0, (int)(DateTime.UtcNow - session.ConnectedAt).TotalSeconds) + " s ago", EditorStyles.wordWrappedMiniLabel);
                }
            }
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
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Recent events", EditorStyles.boldLabel);
            foreach (var entry in BridgeUserState.instance.Events)
            {
                var at = DateTime.TryParse(entry.At, out var time) ? time.ToLocalTime().ToString("HH:mm:ss") : entry.At;
                EditorGUILayout.HelpBox(at + "  " + entry.Message, entry.Error ? MessageType.Warning : MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
