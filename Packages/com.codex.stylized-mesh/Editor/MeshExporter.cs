using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace StylizedMesh
{
    public static class MeshExporter
    {
        public static string Export(GameObject source, List<MeshSession> sessions, string parentFolder, bool colorBlocks)
        {
            if (source == null || sessions.Count == 0) throw new InvalidOperationException("没有载入模型。");
            parentFolder = parentFolder.Replace('\\', '/').TrimEnd('/');
            if (!(parentFolder == "Assets" || parentFolder.StartsWith("Assets/", StringComparison.Ordinal)) || parentFolder.Contains(".."))
                throw new ArgumentException("输出目录必须位于项目 Assets 内。");
            EnsureFolder(parentFolder);
            string name = SafeName(source.name) + "_Stylized";
            string folder = AssetDatabase.GenerateUniqueAssetPath(parentFolder + "/" + name);
            AssetDatabase.CreateFolder(parentFolder, Path.GetFileName(folder));
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source);
                clone.name = name;
                var materials = new Dictionary<Material, Material>();
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    MeshOperations.Validate(session.working);
                    var mesh = UnityEngine.Object.Instantiate(session.working);
                    mesh.hideFlags = HideFlags.None;
                    mesh.name = SafeName(session.original.name) + "_" + i;
                    AssetDatabase.CreateAsset(mesh, folder + "/" + mesh.name + ".asset");
                    Renderer renderer = session.binding.ResolveRenderer(clone);
                    if (renderer is SkinnedMeshRenderer)
                    {
                        var skinned = (SkinnedMeshRenderer)renderer;
                        skinned.sharedMesh = mesh;
                        var bounds = skinned.localBounds; bounds.Encapsulate(mesh.bounds); skinned.localBounds = bounds;
                    }
                    else renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
                    foreach (var collider in renderer.GetComponents<MeshCollider>())
                        if (collider.sharedMesh == session.original) collider.sharedMesh = mesh;
                    if (colorBlocks)
                    {
                        var slots = renderer.sharedMaterials;
                        for (int m = 0; m < slots.Length; m++)
                        {
                            if (slots[m] == null) continue;
                            Material material;
                            if (!materials.TryGetValue(slots[m], out material))
                            {
                                var shader = Shader.Find("StylizedMesh/Color Blocks");
                                if (shader == null) throw new InvalidOperationException("找不到色块材质 Shader，请重新导入插件。");
                                material = new Material(shader) { name = SafeName(slots[m].name) + "_Blocks" };
                                string textureProperty = slots[m].HasProperty("_MainTex") ? "_MainTex" : "_BaseMap";
                                if (slots[m].HasProperty(textureProperty))
                                {
                                    material.mainTexture = slots[m].GetTexture(textureProperty);
                                    material.mainTextureScale = slots[m].GetTextureScale(textureProperty);
                                    material.mainTextureOffset = slots[m].GetTextureOffset(textureProperty);
                                }
                                if (slots[m].HasProperty("_Color")) material.color = slots[m].GetColor("_Color");
                                else if (slots[m].HasProperty("_BaseColor")) material.color = slots[m].GetColor("_BaseColor");
                                AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + material.name + ".mat"));
                                materials.Add(slots[m], material);
                            }
                            slots[m] = material;
                        }
                        renderer.sharedMaterials = slots;
                    }
                }
                // A derivative should not inherit a published VRChat blueprint ID.
                foreach (var component in clone.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component.GetType().FullName != "VRC.Core.PipelineManager") continue;
                    var serialized = new SerializedObject(component);
                    var id = serialized.FindProperty("blueprintId");
                    if (id != null && id.propertyType == SerializedPropertyType.String) { id.stringValue = ""; serialized.ApplyModifiedPropertiesWithoutUndo(); }
                }
                string prefabPath = folder + "/" + name + ".prefab";
                bool success;
                PrefabUtility.SaveAsPrefabAsset(clone, prefabPath, out success);
                if (!success) throw new InvalidOperationException("Prefab 保存失败。");
                AssetDatabase.SaveAssets();
                return prefabPath;
            }
            catch { AssetDatabase.DeleteAsset(folder); throw; }
            finally { if (clone != null) UnityEngine.Object.DestroyImmediate(clone); }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            string parent = folder.Substring(0, slash); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(slash + 1));
        }

        private static string SafeName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "Mesh" : value;
        }
    }
}
