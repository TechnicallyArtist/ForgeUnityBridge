using System;
using System.IO;
using ForgeBridge.Editor.Import;
using GLTFast.Schema;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Material = UnityEngine.Material;
using Object = UnityEngine.Object;

namespace ForgeBridge.Editor.Tests
{
    public sealed class ForgeMaterialsTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            var name = "ForgeMaterialTests_" + Guid.NewGuid().ToString("N");
            folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(folder);

        [TestCase(ForgeRenderPipeline.Universal)]
        [TestCase(ForgeRenderPipeline.BuiltIn)]
        public void MetallicMap_UsesBlueMetallicAndInvertedGreenRoughness(ForgeRenderPipeline pipeline)
        {
            var packed = ForgeMaterials.PackMetallicRoughness(new[] { new Color32(19, 64, 192, 3), new Color32(9, 255, 0, 18) },
                null, 1, 1, 1, pipeline);
            Assert.That(packed[0], Is.EqualTo(new Color32(192, 0, 0, 191)));
            Assert.That(packed[1], Is.EqualTo(new Color32(0, 0, 0, 0)));
        }

        [Test]
        public void MaskMap_PacksOcclusionAndLeavesDetailMaskUnused()
        {
            var packed = ForgeMaterials.PackMetallicRoughness(new[] { new Color32(3, 120, 80, 9) },
                new[] { new Color32(44, 254, 1, 255) }, 1, 1, 1, ForgeRenderPipeline.HighDefinition);
            Assert.That(packed[0], Is.EqualTo(new Color32(80, 44, 0, 135)));
        }

        [Test]
        public void MaskMap_WithoutOcclusionUsesWhite()
        {
            var packed = ForgeMaterials.PackMetallicRoughness(new[] { new Color32(1, 0, 255, 1) },
                null, 1, 1, 1, ForgeRenderPipeline.HighDefinition);
            Assert.That(packed[0], Is.EqualTo(new Color32(255, 255, 0, 255)));
        }

        [Test]
        public void PackedFactors_ApplyBeforeInvertingRoughness()
        {
            var packed = ForgeMaterials.PackMetallicRoughness(new[] { new Color32(1, 128, 200, 1) },
                new[] { new Color32(0, 255, 255, 255) }, .5f, .5f, .25f, ForgeRenderPipeline.HighDefinition);
            Assert.That(packed[0], Is.EqualTo(new Color32(100, 191, 0, 191)));
        }

        [Test]
        public void MaskMap_OnlyOcclusionRetainsScalarMetallicAndRoughness()
        {
            var packed = ForgeMaterials.PackMetallicRoughness(null, new[] { new Color32(64, 0, 0, 255) },
                .2f, .8f, 1, ForgeRenderPipeline.HighDefinition);
            Assert.That(packed[0], Is.EqualTo(new Color32(51, 64, 0, 51)));
        }

        [TestCase(null, ForgeRenderPipeline.BuiltIn)]
        [TestCase("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset", ForgeRenderPipeline.Universal)]
        [TestCase("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset", ForgeRenderPipeline.HighDefinition)]
        public void DetectPipeline_RecognizesOnlySupportedAssets(string typeName, ForgeRenderPipeline expected)
        {
            Assert.That(ForgeMaterials.DetectPipeline(typeName), Is.EqualTo(expected));
        }

        [Test]
        public void DetectPipeline_UnknownSrpNamesTheRejectedType()
        {
            var error = Assert.Throws<NotSupportedException>(() => ForgeMaterials.DetectPipeline("Project.CustomPipelineAsset"));
            StringAssert.Contains("Project.CustomPipelineAsset", error.Message);
            Assert.That(Directory.GetFiles(folder), Is.Empty);
        }

        [Test]
        public void GltfGenerator_KeepsDistinctSlotsWithoutGltfShaderDependencies()
        {
            using (var generator = new ForgeGltfMaterialGenerator(ForgeRenderPipeline.BuiltIn))
            {
                var first = generator.GenerateMaterial(new GLTFast.Schema.Material { name = "Duplicate" }, null);
                var second = generator.GenerateMaterial(new GLTFast.Schema.Material { name = "Duplicate" }, null);
                try
                {
                    Assert.That(first, Is.Not.SameAs(second));
                    Assert.That(first.shader.name, Is.EqualTo("Standard"));
                    Assert.That(generator.GetDefaultMaterial(), Is.SameAs(generator.GetDefaultMaterial()));
                }
                finally { Object.DestroyImmediate(first); Object.DestroyImmediate(second); }
            }
        }

        [Test]
        public void Prepare_RejectsUnrepresentableUvBeforeWriting()
        {
            var source = new GLTFast.Schema.Material
            {
                pbrMetallicRoughness = new PbrMetallicRoughness { baseColorTexture = new TextureInfo { index = 0, texCoord = 1 } }
            };
            Assert.Throws<NotSupportedException>(() => ForgeMaterials.Prepare(source, _ => null, ForgeRenderPipeline.BuiltIn));
            Assert.That(Directory.GetFiles(folder), Is.Empty);
        }

        [Test]
        public void SaveAndRefresh_KeepMaterialAndTextureGuidsAndRepackPixels()
        {
            var texture = FixtureTexture(new Color32(19, 64, 192, 255));
            try
            {
                var source = new GLTFast.Schema.Material
                {
                    pbrMetallicRoughness = new PbrMetallicRoughness
                    {
                        baseColorTexture = new TextureInfo { index = 0 },
                        metallicRoughnessTexture = new TextureInfo { index = 0 }
                    },
                    normalTexture = new NormalTextureInfo { index = 0 },
                    occlusionTexture = new OcclusionTextureInfo { index = 0 },
                    emissiveTexture = new TextureInfo { index = 0 },
                    Emissive = new Color(.1f, .2f, .3f)
                };
                var materialPath = folder + "/Prop_Slot.mat";
                Func<string, string> mapPath = role => folder + "/Prop_Slot_" + role + ".png";
                Save(source, texture, ForgeRenderPipeline.BuiltIn, materialPath, mapPath);
                var materialGuid = AssetDatabase.AssetPathToGUID(materialPath);
                var textureGuid = AssetDatabase.AssetPathToGUID(mapPath("MetallicRoughness"));
                Assert.That(ReadPixel(mapPath("MetallicRoughness")), Is.EqualTo(new Color32(192, 0, 0, 191)));
                Assert.That(ReadPixel(mapPath("Occlusion")).g, Is.EqualTo(19));
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                Assert.That(material.shader.name, Is.EqualTo("Standard"));
                Assert.That(material.IsKeywordEnabled("_METALLICGLOSSMAP"), Is.True);
                Assert.That(material.IsKeywordEnabled("_NORMALMAP"), Is.True);
                Assert.That(material.IsKeywordEnabled("_EMISSION"), Is.True);
                Assert.That(material.GetColor("_EmissionColor").r, Is.EqualTo(.1f).Within(.0001));
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(mapPath("Normal"))).textureType, Is.EqualTo(TextureImporterType.NormalMap));
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(mapPath("MetallicRoughness"))).sRGBTexture, Is.False);
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(mapPath("BaseColor"))).sRGBTexture, Is.True);
                texture.SetPixels32(new[] { new Color32(0, 200, 64, 255) });
                texture.Apply();
                Save(source, texture, ForgeRenderPipeline.BuiltIn, materialPath, mapPath);
                Assert.That(AssetDatabase.AssetPathToGUID(materialPath), Is.EqualTo(materialGuid));
                Assert.That(AssetDatabase.AssetPathToGUID(mapPath("MetallicRoughness")), Is.EqualTo(textureGuid));
                Assert.That(ReadPixel(mapPath("MetallicRoughness")), Is.EqualTo(new Color32(64, 0, 0, 55)));
                Assert.That(AssetDatabase.GetAssetPath(material.GetTexture("_MetallicGlossMap")), Is.EqualTo(mapPath("MetallicRoughness")));
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [TestCase(ForgeRenderPipeline.Universal)]
        [TestCase(ForgeRenderPipeline.HighDefinition)]
        public void PipelineSwitch_ToBuiltInPreservesMaterialAndPackedTextureGuid(ForgeRenderPipeline originalPipeline)
        {
            if (Shader.Find(ForgeMaterials.ShaderName(originalPipeline)) == null)
                Assert.Ignore("This host does not have the optional " + originalPipeline + " pipeline installed.");
            var texture = FixtureTexture(new Color32(45, 96, 160, 255));
            try
            {
                var source = new GLTFast.Schema.Material
                {
                    pbrMetallicRoughness = new PbrMetallicRoughness { metallicRoughnessTexture = new TextureInfo { index = 0 } }
                };
                var materialPath = folder + "/Prop.mat";
                Func<string, string> mapPath = role => folder + "/" + role + ".png";
                Save(source, texture, originalPipeline, materialPath, mapPath);
                var materialGuid = AssetDatabase.AssetPathToGUID(materialPath);
                var mapGuid = AssetDatabase.AssetPathToGUID(mapPath("MetallicRoughness"));
                Save(source, texture, ForgeRenderPipeline.BuiltIn, materialPath, mapPath);
                Assert.That(AssetDatabase.AssetPathToGUID(materialPath), Is.EqualTo(materialGuid));
                Assert.That(AssetDatabase.AssetPathToGUID(mapPath("MetallicRoughness")), Is.EqualTo(mapGuid));
                Assert.That(AssetDatabase.LoadAssetAtPath<Material>(materialPath).shader.name, Is.EqualTo("Standard"));
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void Refresh_WhenEmissionIsRemovedDisablesEmissionKeyword()
        {
            var source = new GLTFast.Schema.Material { Emissive = new Color(.3f, .2f, .1f) };
            var path = folder + "/Prop.mat";
            Save(source, null, ForgeRenderPipeline.BuiltIn, path, _ => throw new InvalidOperationException());
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path).IsKeywordEnabled("_EMISSION"), Is.True);
            source.Emissive = Color.black;
            Save(source, null, ForgeRenderPipeline.BuiltIn, path, _ => throw new InvalidOperationException());
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path).IsKeywordEnabled("_EMISSION"), Is.False);
        }

        [Test]
        public void FailedWrite_RestoresDirtyMaterialBeforeAnotherSave()
        {
            var path = folder + "/Restore.mat";
            var material = new Material(Shader.Find("Standard"));
            material.SetColor("_Color", Color.red);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            var guid = AssetDatabase.AssetPathToGUID(path);
            material.SetColor("_Color", Color.green);
            EditorUtility.SetDirty(material);
            using (CreateTransaction(path))
            {
                material.SetColor("_Color", Color.blue);
                EditorUtility.SetDirty(material);
                // Simulate a later texture import failure before SaveAssets.
            }
            var restored = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.That(restored.GetColor("_Color"), Is.EqualTo(Color.green));
            Assert.That(EditorUtility.IsDirty(restored), Is.True);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path).GetColor("_Color"), Is.EqualTo(Color.green));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
        }

        [Test]
        public void FailedWrite_RestoresUnsavedControllerSubassetEdits()
        {
            var path = folder + "/Restore.controller";
            var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var walk = machine.AddState("Walk");
            AssetDatabase.SaveAssets();
            var transition = idle.AddTransition(walk);
            transition.duration = .27f;
            EditorUtility.SetDirty(transition);
            EditorUtility.SetDirty(controller);
            using (CreateTransaction(path))
            {
                transition.duration = .99f;
                machine.AddState("FailedState");
                EditorUtility.SetDirty(transition);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssetIfDirty(controller);
            }
            var restored = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path);
            Assert.That(restored.layers[0].stateMachine.states.Length, Is.EqualTo(2));
            var restoredIdle = System.Array.Find(restored.layers[0].stateMachine.states, state => state.state.name == "Idle").state;
            Assert.That(restoredIdle.transitions.Length, Is.EqualTo(1));
            Assert.That(restoredIdle.transitions[0].duration, Is.EqualTo(.27f).Within(.0001));
        }

        private static IDisposable CreateTransaction(string path)
        {
            var type = typeof(ForgeAssetWriter).Assembly.GetType("ForgeBridge.Editor.Import.ForgeWriteTransaction", true);
            return (IDisposable)Activator.CreateInstance(type, new object[] { new[] { path } });
        }

        private static void Save(MaterialBase source, Texture2D texture, ForgeRenderPipeline pipeline, string path, Func<string, string> mapPath)
        {
            using (var prepared = ForgeMaterials.Prepare(source, _ => texture, pipeline))
            {
                AssetDatabase.StartAssetEditing();
                try { prepared.Save(path, mapPath); }
                finally { AssetDatabase.StopAssetEditing(); }
                prepared.Finish();
                AssetDatabase.SaveAssets();
            }
        }

        private static Texture2D FixtureTexture(Color32 pixel)
        {
            var result = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            result.SetPixels32(new[] { pixel });
            result.Apply();
            return result;
        }

        private static Color32 ReadPixel(string path)
        {
            var result = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                Assert.That(result.LoadImage(File.ReadAllBytes(path)), Is.True);
                return result.GetPixels32()[0];
            }
            finally { Object.DestroyImmediate(result); }
        }
    }
}
