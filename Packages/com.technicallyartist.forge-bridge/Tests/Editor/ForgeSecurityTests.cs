using System;
using System.IO;
using System.Text;
using ForgeBridge.Editor.Import;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ForgeBridge.Editor.Tests
{
    public class ForgeSecurityTests
    {
        [Test]
        public void Glb_RejectsArrayFieldsWithObjectShape()
        {
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(root => root["images"] = new JObject())));
        }

        [Test]
        public void Glb_RejectsOverflowingBufferViewRange()
        {
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(root =>
            {
                root["bufferViews"][0]["byteOffset"] = long.MaxValue;
                root["bufferViews"][0]["byteLength"] = long.MaxValue;
            })));
        }

        [Test]
        public void Glb_RejectsAccessorAllocationWithoutBackingData()
        {
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(root =>
            {
                ((JObject)root["accessors"][0]).Remove("bufferView");
                root["accessors"][0]["count"] = 536870912;
            })));
        }

        [Test]
        public void Glb_RejectsSparseIndicesOutsideBufferView()
        {
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(root => root["accessors"][0]["sparse"] = new JObject
            {
                ["count"] = 3,
                ["indices"] = new JObject { ["bufferView"] = 1, ["byteOffset"] = 4, ["componentType"] = 5123 },
                ["values"] = new JObject { ["bufferView"] = 0 }
            })));
        }

        [Test]
        public void Glb_RejectsBufferBeyondDecodedDataUri()
        {
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(root =>
                root["buffers"][0]["uri"] = "data:application/octet-stream;base64,AA==")));
        }

        [Test]
        public void Glb_RejectsExcessiveDecodedImageDimensions()
        {
            var png = new byte[] { 137,80,78,71,13,10,26,10, 0,0,0,13, 73,72,68,82, 0,1,0,0, 0,1,0,0 };
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(root => root["images"] = new JArray(new JObject
            {
                ["uri"] = "data:image/png;base64," + Convert.ToBase64String(png)
            }))));
        }

        [Test]
        public void Glb_RejectsTriangleIndexOutsideVertexAttributes()
        {
            Assert.Throws<InvalidDataException>(() => ForgeGlb.Validate(Mutate(_ => { }, binary => binary[36] = 3)));
        }

        [TestCase("Assets/Forge/CON/new")]
        [TestCase("Assets/Forge/a./new")]
        [TestCase("Assets/Forge/a /new")]
        [TestCase("Assets/Forge/../outside")]
        [TestCase("Packages/Forge/Mesh.asset")]
        [TestCase("Assets/Forge/mesh.asset:stream")]
        [TestCase("Assets/Forge/a\n/new")]
        public void AssetPath_RejectsUnusableOrUncontainedPaths(string path)
        {
            Assert.Throws<ArgumentException>(() => ForgeIndex.ValidateAssetPath(path));
        }

        private static byte[] Mutate(Action<JObject> edit, Action<byte[]> editBinary = null)
        {
            var original = ForgeImportTests.Triangle();
            int jsonLength = BitConverter.ToInt32(original, 12);
            var root = JObject.Parse(Encoding.UTF8.GetString(original, 20, jsonLength));
            edit(root);
            int binaryLength = BitConverter.ToInt32(original, 20 + jsonLength);
            var binary = new byte[binaryLength];
            Buffer.BlockCopy(original, 28 + jsonLength, binary, 0, binaryLength);
            editBinary?.Invoke(binary);
            var json = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            int paddedLength = (json.Length + 3) & ~3;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546c67); writer.Write(2); writer.Write(28 + paddedLength + binaryLength);
                writer.Write(paddedLength); writer.Write(0x4e4f534a); writer.Write(json);
                for (var i = json.Length; i < paddedLength; i++) writer.Write((byte)32);
                writer.Write(binaryLength); writer.Write(0x004e4942); writer.Write(binary);
                return stream.ToArray();
            }
        }
    }
}
