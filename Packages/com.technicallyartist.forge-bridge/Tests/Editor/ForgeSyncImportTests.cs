using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ForgeBridge.Editor.Import;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ForgeBridge.Editor.Tests
{
    /// <summary>The import half of project sync: the kept `.ta` backup and the import-completed event.</summary>
    public class ForgeSyncImportTests
    {
        private const string Root = "Assets/ForgeSyncImportTests";
        private const string BackupPath = Root + "/Test/" + ForgeBackup.FolderName + "/Triangle.ta";
        private string previousFolder;
        private readonly List<ForgeImportCompleted> completed = new List<ForgeImportCompleted>();

        [SetUp] public void SetUp()
        {
            previousFolder = BridgeSettings.instance.ForgeFolder;
            BridgeSettings.instance.ForgeFolder = Root;
            Clean();
            completed.Clear();
            ForgeImporter.ImportCompleted += Record;
        }

        [TearDown] public void TearDown()
        {
            ForgeImporter.ImportCompleted -= Record;
            ForgeImporter.ImportCompleted -= Throw;
            BridgeSettings.instance.ForgeFolder = previousFolder;
            Clean();
        }

        [UnityTest] public IEnumerator Push_KeepsBackupBesideOutputAndLeavesAnIdenticalOneUntouched()
        {
            byte[] archive = Archive("first");
            byte[] glb = WithBackup(ForgeImportTests.Triangle(), archive, out JObject backup);
            var first = ForgeImporter.ImportAsync(Header(glb, backup), glb); yield return ForgeImportTests.Await(first);
            Assert.That(first.Result.Error, Is.Null);
            Assert.That(File.ReadAllBytes(BackupPath), Is.EqualTo(archive));
            Assert.That(AssetDatabase.AssetPathToGUID(BackupPath), Is.Empty, "A Backup~ folder is never imported.");
            Assert.That(completed[0].BackupPath, Is.EqualTo(BackupPath));
            var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(BackupPath, written);

            var same = ForgeImporter.ImportAsync(Header(glb, backup), glb); yield return ForgeImportTests.Await(same);
            Assert.That(same.Result.Error, Is.Null);
            Assert.That(File.GetLastWriteTimeUtc(BackupPath), Is.EqualTo(written));

            byte[] changed = Archive("second");
            glb = WithBackup(ForgeImportTests.Triangle(), changed, out backup);
            var refresh = ForgeImporter.ImportAsync(Header(glb, backup), glb); yield return ForgeImportTests.Await(refresh);
            Assert.That(refresh.Result.Error, Is.Null);
            Assert.That(File.ReadAllBytes(BackupPath), Is.EqualTo(changed));
            Assert.That(Directory.GetFiles(Root + "/Test/" + ForgeBackup.FolderName), Has.Length.EqualTo(1));
        }

        [UnityTest] public IEnumerator Push_WithAMismatchedBackupFailsBeforeAnyWrite()
        {
            byte[] glb = WithBackup(ForgeImportTests.Triangle(), Archive("x"), out JObject backup);
            backup["bytes"] = (long)backup["bytes"] + 1;
            var push = ForgeImporter.ImportAsync(Header(glb, backup), glb); yield return ForgeImportTests.Await(push);
            Assert.That(push.Result.Error, Does.Contain("backup"));
            Assert.That(Directory.Exists(Root), Is.False);
            Assert.That(completed, Is.Empty);
        }

        [UnityTest] public IEnumerator Push_RaisesImportCompletedOncePerPushWithItsIdentityAndAssets()
        {
            byte[] glb = ForgeImportTests.Triangle();
            var sent = ForgeImporter.ImportAsync(ForgeImportTests.Header(glb), glb); yield return ForgeImportTests.Await(sent);
            Assert.That(sent.Result.Error, Is.Null);
            var header = ForgeImportTests.Header(glb);
            header["ref"] = "push-project-1";
            var requested = ForgeImporter.ImportAsync(header, glb); yield return ForgeImportTests.Await(requested);
            Assert.That(requested.Result.Error, Is.Null);

            Assert.That(completed, Has.Count.EqualTo(2));
            Assert.That(completed[0].Requested, Is.False);
            Assert.That(completed[0].Created, Is.True);
            Assert.That(completed[1].RequestId, Is.EqualTo("push-project-1"));
            Assert.That(completed[1].Created, Is.False);
            foreach (var item in completed)
            {
                Assert.That(item.ProjectId, Is.EqualTo("test-project"));
                Assert.That(item.NodeId, Is.EqualTo("test-node"));
                Assert.That(item.AssetPath, Is.EqualTo(Root + "/Test/Triangle.prefab"));
                Assert.That(item.Prefab, Is.EqualTo(AssetDatabase.LoadAssetAtPath<GameObject>(item.AssetPath)));
                Assert.That(item.Meshes, Has.Count.EqualTo(1));
                Assert.That(AssetDatabase.GetAssetPath(item.Meshes[0]), Is.EqualTo(Root + "/Test/Meshes/Triangle.asset"));
                Assert.That(item.Clips, Is.Empty);
                Assert.That(item.Controller, Is.Null);
                Assert.That(item.BackupPath, Is.Null);
            }
        }

        [UnityTest] public IEnumerator ImportCompletedHandlerFailure_IsAWarningAndKeepsTheImport()
        {
            ForgeImporter.ImportCompleted += Throw;
            byte[] glb = ForgeImportTests.Triangle();
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: game post-process failed");
            var push = ForgeImporter.ImportAsync(ForgeImportTests.Header(glb), glb); yield return ForgeImportTests.Await(push);
            Assert.That(push.Result.Error, Is.Null);
            Assert.That(push.Result.Warnings, Has.Some.Contains("game post-process failed"));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(push.Result.AssetPath), Is.Not.Null);
            Assert.That(completed, Has.Count.EqualTo(1), "Other handlers still run.");
        }

        private void Record(ForgeImportCompleted item) => completed.Add(item);
        private static void Throw(ForgeImportCompleted _) => throw new InvalidOperationException("game post-process failed");

        private static JObject Header(byte[] glb, JObject backup)
        {
            var header = ForgeImportTests.Header(glb);
            header["backup"] = backup;
            return header;
        }

        /// <summary>A stand-in archive: the importer keeps the bytes and never opens them.</summary>
        private static byte[] Archive(string content) => Encoding.UTF8.GetBytes("PK-test-archive-" + content);

        /// <summary>Embeds <paramref name="archive"/> the way Forge's projectBackup.ts does: an unreferenced bufferView
        /// after the existing binary data, 4-byte aligned, recorded in the document's extras too.</summary>
        internal static byte[] WithBackup(byte[] glb, byte[] archive, out JObject backup)
        {
            int jsonLength = BitConverter.ToInt32(glb, 12);
            var root = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
            int binaryLength = BitConverter.ToInt32(glb, 20 + jsonLength);
            var binary = new byte[binaryLength];
            Buffer.BlockCopy(glb, 28 + jsonLength, binary, 0, binaryLength);
            int offset = (binaryLength + 3) & ~3;
            var views = (JArray)root["bufferViews"];
            backup = new JObject { ["format"] = "ta", ["bufferView"] = views.Count, ["bytes"] = archive.Length };
            views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = archive.Length });
            root["buffers"] = new JArray(new JObject { ["byteLength"] = offset + archive.Length });
            root["extras"] = new JObject { ["forgeProjectBackup"] = backup.DeepClone() };
            var json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
            int paddedJson = (json.Length + 3) & ~3;
            int paddedBinary = (offset + archive.Length + 3) & ~3;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546c67); writer.Write(2); writer.Write(28 + paddedJson + paddedBinary);
                writer.Write(paddedJson); writer.Write(0x4e4f534a); writer.Write(json);
                for (int i = json.Length; i < paddedJson; i++) writer.Write((byte)32);
                writer.Write(paddedBinary); writer.Write(0x004e4942);
                writer.Write(binary);
                for (int i = binary.Length; i < offset; i++) writer.Write((byte)0);
                writer.Write(archive);
                for (int i = offset + archive.Length; i < paddedBinary; i++) writer.Write((byte)0);
                return stream.ToArray();
            }
        }

        private static void Clean()
        {
            AssetDatabase.DeleteAsset(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            if (File.Exists(Root + ".meta")) File.Delete(Root + ".meta");
        }
    }
}
