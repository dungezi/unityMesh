using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace StylizedMesh
{
    public sealed class MeshSession : ScriptableObject
    {
        public Mesh original;
        public Mesh working;
        public bool[] selected;
        public bool[] protectedVertices;
        public bool changed;
        [NonSerialized] public MeshBinding binding;
        [NonSerialized] public Renderer previewRenderer;
        [NonSerialized] public MeshFilter previewFilter;

        public static MeshSession Create(MeshBinding binding)
        {
            MeshOperations.Validate(binding.Mesh);
            var result = CreateInstance<MeshSession>();
            result.hideFlags = HideFlags.HideAndDontSave;
            result.original = binding.Mesh;
            result.working = MeshOperations.Clone(binding.Mesh);
            result.binding = binding;
            result.selected = new bool[binding.Mesh.vertexCount];
            result.protectedVertices = new bool[binding.Mesh.vertexCount];
            return result;
        }

        public void Refresh()
        {
            if (previewRenderer is SkinnedMeshRenderer)
            {
                var skinned = (SkinnedMeshRenderer)previewRenderer;
                // Reassigning the same mesh on every expression update needlessly rebuilds skinning data.
                if (skinned.sharedMesh != working) skinned.sharedMesh = working;
                var bounds = skinned.localBounds; bounds.Encapsulate(working.bounds); skinned.localBounds = bounds;
            }
            else if (previewFilter != null && previewFilter.sharedMesh != working) previewFilter.sharedMesh = working;
        }

        public void Replace(Mesh mesh, bool[] mask)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("修改网格拓扑");
            Undo.RegisterCompleteObjectUndo(this, "修改网格拓扑");
            working = mesh;
            protectedVertices = mask;
            selected = new bool[mesh.vertexCount];
            changed = true;
            Refresh();
            EditorUtility.SetDirty(this);
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
        }
    }

    public sealed class MeshBinding
    {
        public Renderer renderer;
        public MeshFilter filter;
        public int[] path;
        public int componentIndex;
        public string label;
        public Mesh Mesh { get { return renderer is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)renderer).sharedMesh : filter.sharedMesh; } }

        public static List<MeshBinding> Find(GameObject root)
        {
            var result = new List<MeshBinding>();
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer.sharedMesh != null) result.Add(Create(root, renderer, null));
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh != null && renderer != null) result.Add(Create(root, renderer, filter));
            }
            return result;
        }

        private static MeshBinding Create(GameObject root, Renderer renderer, MeshFilter filter)
        {
            var indices = new List<int>();
            var transform = renderer.transform;
            while (transform != root.transform) { indices.Add(transform.GetSiblingIndex()); transform = transform.parent; }
            indices.Reverse();
            int index = Array.IndexOf(renderer.GetComponents(renderer.GetType()), renderer);
            return new MeshBinding { renderer = renderer, filter = filter, path = indices.ToArray(), componentIndex = index,
                label = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform) + " / " + renderer.GetType().Name };
        }

        public Transform ResolveTransform(GameObject root)
        {
            Transform target = root.transform;
            foreach (int index in path) target = target.GetChild(index);
            return target;
        }

        public Renderer ResolveRenderer(GameObject root)
        {
            var transform = ResolveTransform(root);
            return (Renderer)transform.GetComponents(renderer.GetType())[componentIndex];
        }
    }

    public static class MeshPreview
    {
        // Copy rendering and transforms only: no avatar scripts, Animator, PhysBones, audio or colliders execute.
        public static GameObject Create(GameObject source, List<MeshSession> sessions, Vector3 offset)
        {
            var transforms = new Dictionary<Transform, Transform>();
            var root = CopyTransforms(source.transform, null, transforms).gameObject;
            root.name = source.name + " · 网格预览";
            root.transform.position += offset;
            try
            {
            foreach (var sourceRenderer in source.GetComponentsInChildren<Renderer>(true))
            {
                if (!(sourceRenderer is SkinnedMeshRenderer) && !(sourceRenderer is MeshRenderer)) continue;
                var target = transforms[sourceRenderer.transform].gameObject;
                Renderer renderer;
                if (sourceRenderer is SkinnedMeshRenderer)
                {
                    var original = (SkinnedMeshRenderer)sourceRenderer;
                    var skinned = target.AddComponent<SkinnedMeshRenderer>();
                    skinned.sharedMesh = original.sharedMesh;
                    var bones = original.bones; var mapped = new Transform[bones.Length];
                    for (int i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] != null && !transforms.TryGetValue(bones[i], out mapped[i]))
                            throw new InvalidOperationException("骨骼位于所选对象外，请选择完整模型根对象。");
                    }
                    skinned.bones = mapped;
                    if (original.rootBone != null)
                    {
                        Transform rootBone;
                        if (!transforms.TryGetValue(original.rootBone, out rootBone))
                            throw new InvalidOperationException("根骨骼位于所选对象外，请选择完整模型根对象。");
                        skinned.rootBone = rootBone;
                    }
                    skinned.localBounds = original.localBounds;
                    skinned.updateWhenOffscreen = true;
                    skinned.quality = original.quality;
                    for (int i = 0; original.sharedMesh != null && i < original.sharedMesh.blendShapeCount; i++)
                        skinned.SetBlendShapeWeight(i, original.GetBlendShapeWeight(i));
                    renderer = skinned;
                }
                else
                {
                    var originalFilter = sourceRenderer.GetComponent<MeshFilter>();
                    if (originalFilter == null) continue;
                    target.AddComponent<MeshFilter>().sharedMesh = originalFilter.sharedMesh;
                    renderer = target.AddComponent<MeshRenderer>();
                }
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                renderer.enabled = sourceRenderer.enabled;
                renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                renderer.receiveShadows = sourceRenderer.receiveShadows;
            }
            foreach (var session in sessions)
            {
                session.previewRenderer = session.binding.ResolveRenderer(root);
                session.previewFilter = session.previewRenderer.GetComponent<MeshFilter>();
                session.Refresh();
            }
            return root;
            }
            catch { UnityEngine.Object.DestroyImmediate(root); throw; }
        }

        private static Transform CopyTransforms(Transform source, Transform parent, Dictionary<Transform, Transform> map)
        {
            var copy = new GameObject(source.name) { hideFlags = HideFlags.HideAndDontSave };
            var target = copy.transform;
            target.SetParent(parent, false);
            if (parent == null) { target.position = source.position; target.rotation = source.rotation; target.localScale = source.lossyScale; }
            else { target.localPosition = source.localPosition; target.localRotation = source.localRotation; target.localScale = source.localScale; }
            map[source] = target;
            for (int i = 0; i < source.childCount; i++) CopyTransforms(source.GetChild(i), target, map);
            copy.SetActive(source.gameObject.activeSelf);
            return target;
        }
    }
}
