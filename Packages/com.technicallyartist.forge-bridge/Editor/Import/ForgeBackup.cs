using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ForgeBridge.Editor.Import
{
    /// <summary>
    /// The project backup a 1.4 push carries: the Forge project's `.ta` archive (graph and every source it needs) in an
    /// unreferenced glb bufferView. It is kept beside the imported output in a `~` folder, which Unity never imports, so the
    /// output folder can be committed and the project rebuilt in Forge from it.
    /// </summary>
    public static class ForgeBackup
    {
        public const string FolderName = "Backup~";

        /// <summary>The archive bytes, or null when the push carries no backup. <paramref name="json"/> is the glb's
        /// JSON chunk, already validated by <see cref="ForgeGlb.Validate"/>.</summary>
        public static byte[] Extract(JObject header, JObject json, byte[] glb)
        {
            var descriptor = header["backup"];
            if (descriptor == null || descriptor.Type == JTokenType.Null) return null;
            if (!(descriptor is JObject backup) || ForgeProtocol.String(backup, "format") != "ta")
                throw new InvalidDataException("The push's backup must be { format: \"ta\", bufferView, bytes }.");
            if (backup["bufferView"]?.Type != JTokenType.Integer || backup["bytes"]?.Type != JTokenType.Integer)
                throw new InvalidDataException("The push's backup needs integer bufferView and bytes.");
            long viewIndex = (long)backup["bufferView"];
            long bytes = (long)backup["bytes"];
            var views = json["bufferViews"] as JArray;
            if (views == null || viewIndex < 0 || viewIndex >= views.Count || bytes <= 0)
                throw new InvalidDataException("The push's backup names a bufferView the glb does not have.");
            var view = (JObject)views[(int)viewIndex];
            long offset = view["byteOffset"] != null ? (long)view["byteOffset"] : 0;
            if ((long?)view["buffer"] != 0 || (long?)view["byteLength"] != bytes)
                throw new InvalidDataException("The push's backup bufferView must lie in buffer 0 and hold exactly backup.bytes.");
            // ForgeGlb.Validate has checked that buffer 0 is the BIN chunk and that the view lies inside it.
            int jsonLength = BitConverter.ToInt32(glb, 12);
            int binaryStart = 20 + jsonLength + 8;
            var archive = new byte[bytes];
            Buffer.BlockCopy(glb, checked(binaryStart + (int)offset), archive, 0, (int)bytes);
            return archive;
        }

        /// <summary>Writes <paramref name="archive"/> to `&lt;folder&gt;/Backup~/&lt;baseName&gt;.ta` and returns its path.
        /// Identical bytes leave the file untouched, so re-pushing unchanged output changes nothing on disk.</summary>
        public static string Write(string folder, string baseName, byte[] archive)
        {
            string directory = folder.TrimEnd('/') + "/" + FolderName;
            string path = directory + "/" + baseName + ".ta";
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(archive)) return path;
            Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, archive);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return path;
        }
    }
}
