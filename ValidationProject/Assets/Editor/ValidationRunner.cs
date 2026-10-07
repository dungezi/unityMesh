using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StylizedMesh;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ValidationRunner
{
    private static readonly List<UnityEngine.Object> transient = new List<UnityEngine.Object>();
    private static readonly List<string> results = new List<string>();

    [Serializable]
    private sealed class PackageManifest { public string version = null; }

    private static string DistributionPath()
    {
        string workspace = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
        var manifest = JsonUtility.FromJson<PackageManifest>(File.ReadAllText(Path.Combine(workspace, "Packages/com.codex.stylized-mesh/package.json")));
        if (manifest == null || string.IsNullOrEmpty(manifest.version)) throw new Exception("Missing package version.");
        return Path.Combine(workspace, "artifacts/StylizedMeshStudio-" + manifest.version + ".unitypackage");
    }

    public static void ImportAndExport()
    {
        string workspace = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
        string package = DistributionPath();
        AssetDatabase.ImportPackage(package, false);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        string source = Path.Combine(workspace, "Packages/com.codex.stylized-mesh");
        foreach (string file in Directory.GetFiles(Path.Combine(source, "Editor"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = file.Substring(source.Length + 1);
            string imported = Path.Combine(Application.dataPath, "StylizedMesh", relative);
            Check(File.Exists(imported) && File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(imported)), "Imported source mismatch: " + relative);
        }
        Check(Shader.Find("StylizedMesh/Color Blocks") != null && !ShaderUtil.ShaderHasError(Shader.Find("StylizedMesh/Color Blocks")), "Imported shader invalid.");
        File.Copy(Path.Combine(workspace, "README.md"), Path.Combine(Application.dataPath, "StylizedMesh/README.md"), true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ExportPackage("Assets/StylizedMesh", package, ExportPackageOptions.Recurse);
        File.WriteAllLines(Path.Combine(workspace, "artifacts/import-validation.txt"), new[] {
            "PASS Unity AssetDatabase.ImportPackage", "PASS imported C# sources match package sources",
            "PASS imported shader", "PASS Unity native ExportPackage", "Unity " + Application.unityVersion });
        Debug.Log("STYLIZED_MESH_IMPORT_SUCCESS");
        EditorApplication.Exit(0);
    }

    public static void Run()
    {
        string reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/validation.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
        int failures = 0;
        var cases = new Action[] { ReductionPreservesData, PaintedProtection, UVSeamsSurvive, MovingShapesProtected,
            CancellationIsNonDestructive, VertexAndUVEdits, FlatShadingPreservesExtendedSkin, ExtendedSkinReductionRejected,
            MultiFrameExpressions, PosedPreviewAndPrefab, UndoRestoresEdits, CleanupKeepsUVAndShapes, LargeMeshUsesUInt32, ZeroUVChannels, ShaderCompiles,
            StaticSceneHandleAndUndo, SkinnedSceneHandleWithActiveExpressions, PreviewVisibility,
            ExpressionPreviewDefersGeometry, MultiStepWindowUndoRedo, SceneCameraNavigationShortcuts };
        foreach (var test in cases)
        {
            try { test(); results.Add("PASS " + test.Method.Name); }
            catch (Exception e) { failures++; results.Add("FAIL " + test.Method.Name + "\n" + e); Debug.LogException(e); }
            finally
            {
                foreach (var obj in transient) if (obj != null && !EditorUtility.IsPersistent(obj)) UnityEngine.Object.DestroyImmediate(obj);
                transient.Clear();
            }
        }
        results.Add($"Unity {Application.unityVersion}; {cases.Length - failures}/{cases.Length} cases passed.");
        File.WriteAllLines(reportPath, results);
        if (failures == 0)
        {
            string output = DistributionPath();
            AssetDatabase.ExportPackage("Assets/StylizedMesh", output, ExportPackageOptions.Recurse);
            Debug.Log("STYLIZED_MESH_VALIDATION_SUCCESS\n" + string.Join("\n", results));
        }
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    private static T Track<T>(T value) where T : UnityEngine.Object { transient.Add(value); return value; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool Close(Vector3 a, Vector3 b, float tolerance = 1e-5f) { return (a - b).sqrMagnitude <= tolerance * tolerance; }

    private static ReduceSettings Unrestricted(float quality = 0.35f)
    {
        return new ReduceSettings { quality = quality, borders = false, uvSeams = false, uvFoldovers = false,
            materialBoundaries = false, movingBlendShapeVertices = false, boneBoundaries = false };
    }

    private static Mesh Grid(int side = 14, bool skin = false, bool shape = false)
    {
        int n = side * side;
        var mesh = Track(new Mesh { name = "TestGrid", indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 });
        var vertices = new Vector3[n]; var uv = new Vector2[n]; var extras = new List<Vector4>();
        var colors = new Color[n]; var weights = new BoneWeight[n];
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
        {
            int i = y * side + x; float u = (float)x / (side - 1), v = (float)y / (side - 1);
            vertices[i] = new Vector3(u, v, Mathf.Sin(u * 2f) * Mathf.Sin(v * 2f) * 0.12f);
            uv[i] = new Vector2(u, v); extras.Add(new Vector4(u, v, 0.25f, 0.75f)); colors[i] = new Color(u, v, 0.5f, 1);
            weights[i] = new BoneWeight { boneIndex0 = 0, boneIndex1 = 1, weight0 = 0.75f - u * 0.5f, weight1 = 0.25f + u * 0.5f };
        }
        mesh.vertices = vertices; mesh.uv = uv; mesh.SetUVs(1, extras); mesh.colors = colors;
        var a = new List<int>(); var b = new List<int>();
        for (int y = 0; y < side - 1; y++) for (int x = 0; x < side - 1; x++)
        {
            int i = y * side + x; var target = x < side / 2 ? a : b;
            target.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 });
        }
        mesh.subMeshCount = 2; mesh.SetTriangles(a, 0); mesh.SetTriangles(b, 1);
        mesh.RecalculateNormals(); mesh.RecalculateTangents();
        if (skin) { mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity }; mesh.boneWeights = weights; }
        if (shape)
        {
            var delta = new Vector3[n]; delta[n / 2] = new Vector3(0, 0.1f, 0);
            mesh.AddBlendShapeFrame("Blink", 50, delta, null, null);
            var larger = delta.Select(v => v * 2).ToArray(); mesh.AddBlendShapeFrame("Blink", 100, larger, null, null);
            mesh.AddBlendShapeFrame("Smile", 100, delta, null, null);
        }
        return mesh;
    }

    private static void ReductionPreservesData()
    {
        var source = Grid(18, true, true); var vertices = source.vertices; var tris = source.triangles;
        var reduced = Track(MeshOperations.Reduce(source, null, Unrestricted()));
        Check(MeshOperations.TriangleCount(reduced) < MeshOperations.TriangleCount(source), "Triangle count did not decrease.");
        Check(reduced.subMeshCount == 2, "Material slots lost.");
        Check(reduced.blendShapeCount == 2 && reduced.GetBlendShapeName(0) == "Blink" && reduced.GetBlendShapeFrameCount(0) == 2, "Expression names/frames lost.");
        Check(reduced.bindposes.Length == 2 && reduced.boneWeights.Length == reduced.vertexCount, "Skinning lost.");
        Check(reduced.colors.Length == reduced.vertexCount && reduced.uv.Length == reduced.vertexCount, "Vertex attributes lost.");
        Check(reduced.GetVertexAttributeDimension(VertexAttribute.TexCoord1) == 4, "4D UV channel lost.");
        foreach (var w in reduced.boneWeights) Check(Mathf.Abs(w.weight0 + w.weight1 + w.weight2 + w.weight3 - 1) < 1e-5, "Skin weights not normalized.");
        Check(source.vertices.SequenceEqual(vertices) && source.triangles.SequenceEqual(tris), "Source mutated.");
    }

    private static void PaintedProtection()
    {
        var source = Grid(); int locked = 7 * 14 + 7; Vector3 position = source.vertices[locked];
        var mask = new bool[source.vertexCount]; mask[locked] = true;
        var reduced = Track(MeshOperations.Reduce(source, mask, Unrestricted(0.1f)));
        Check(reduced.vertices.Any(v => Close(v, position)), "Protected vertex moved or removed.");
        Check(MeshOperations.TriangleCount(reduced) < MeshOperations.TriangleCount(source), "No reduction outside protection.");
    }

    private static void UVSeamsSurvive()
    {
        const int side = 8; var source = Track(new Mesh { name = "UVSeam" });
        var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
        for (int half = 0; half < 2; half++)
        {
            int offset = vertices.Count;
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
            {
                float u = (float)x / (side - 1), v = (float)y / (side - 1);
                vertices.Add(new Vector3(half - 1 + u, v, 0)); uv.Add(new Vector2(u * 0.4f + half * 0.6f, v));
            }
            for (int y = 0; y < side - 1; y++) for (int x = 0; x < side - 1; x++)
            { int i = offset + y * side + x; tris.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 }); }
        }
        source.SetVertices(vertices); source.SetUVs(0, uv); source.SetTriangles(tris, 0); source.RecalculateNormals();
        var options = Unrestricted(0.25f); options.uvSeams = true;
        var reduced = Track(MeshOperations.Reduce(source, null, options));
        var p = reduced.vertices; var tex = reduced.uv;
        for (int y = 1; y < side - 1; y++)
        {
            var point = new Vector3(0, (float)y / (side - 1), 0);
            Check(Enumerable.Range(0, p.Length).Any(i => Close(p[i], point) && Mathf.Abs(tex[i].x - 0.4f) < 1e-5f), "Left seam UV lost.");
            Check(Enumerable.Range(0, p.Length).Any(i => Close(p[i], point) && Mathf.Abs(tex[i].x - 0.6f) < 1e-5f), "Right seam UV lost.");
        }
    }

    private static void MovingShapesProtected()
    {
        var source = Grid(14, false, true); var settings = Unrestricted(0.1f); settings.movingBlendShapeVertices = true;
        var reduced = Track(MeshOperations.Reduce(source, null, settings)); int original = source.vertexCount / 2;
        int index = Array.FindIndex(reduced.vertices, p => Close(p, source.vertices[original]));
        Check(index >= 0, "Expression-moving vertex lost.");
        var delta = new Vector3[reduced.vertexCount]; reduced.GetBlendShapeFrameVertices(0, 1, delta, null, null);
        Check(Close(delta[index], new Vector3(0, 0.2f, 0)), "Protected expression offset changed.");
    }

    private static void CancellationIsNonDestructive()
    {
        var source = Grid(); var original = source.vertices; bool canceled = false;
        try { MeshOperations.Reduce(source, null, Unrestricted(), p => true); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && original.SequenceEqual(source.vertices), "Cancellation mutated source or did not cancel.");
    }

    private static void VertexAndUVEdits()
    {
        var source = Grid(4, true, true); var mesh = Track(MeshOperations.Clone(source));
        var selected = new bool[mesh.vertexCount]; selected[0] = true;
        var old = mesh.vertices; var oldUV = mesh.uv;
        MeshOperations.TransformVertices(mesh, selected, Matrix4x4.Translate(new Vector3(0.2f, 0, 0)), true);
        Check(Close(mesh.vertices[0], old[0] + new Vector3(0.2f, 0, 0)), "Vertex did not move.");
        Check(Close(mesh.vertices[1], old[1]) && mesh.uv.SequenceEqual(oldUV), "Unselected vertex or UV changed.");
        MeshOperations.TransformUV(mesh, 1, selected, new Vector2(0.15f, 0.2f), Vector2.one, 0);
        var extra = new List<Vector4>(); mesh.GetUVs(1, extra);
        Check(Mathf.Abs(extra[0].x - 0.15f) < 1e-5 && extra[0].z == 0.25f && extra[0].w == 0.75f, "UV transform lost extra components.");
        Check(extra[1].x == 1f / 3f, "Unselected UV changed.");
        MeshOperations.Snap(mesh, selected, 0.1f);
        Check(mesh.blendShapeCount == 2 && mesh.bindposes.Length == 2, "Vertex operation lost animation data.");
        Check(Close(source.vertices[0], old[0]), "Source changed by editing clone.");
    }

    private static Mesh FiveInfluences()
    {
        var mesh = Grid(4, false, true); mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray();
        var counts = new NativeArray<byte>(mesh.vertexCount, Allocator.Temp);
        var weights = new NativeArray<BoneWeight1>(mesh.vertexCount * 5, Allocator.Temp);
        try
        {
            for (int i = 0; i < counts.Length; i++)
            { counts[i] = 5; for (int j = 0; j < 5; j++) weights[i * 5 + j] = new BoneWeight1 { boneIndex = j, weight = 0.2f }; }
            mesh.SetBoneWeights(counts, weights);
        }
        finally { counts.Dispose(); weights.Dispose(); }
        return mesh;
    }

    private static void FlatShadingPreservesExtendedSkin()
    {
        var source = FiveInfluences(); int[] map;
        var flat = Track(MeshOperations.FlatShade(source, out map));
        Check(MeshOperations.TriangleCount(flat) == MeshOperations.TriangleCount(source) && flat.vertexCount == source.triangles.Length, "Flat shading changed triangle count.");
        Check(flat.subMeshCount == source.subMeshCount && flat.blendShapeCount == source.blendShapeCount, "Flat shading lost slots/shapes.");
        using (var counts = flat.GetBonesPerVertex()) Check(counts.All(c => c == 5), "Extended weights truncated.");
        var normals = flat.normals; var tri = flat.triangles;
        for (int i = 0; i < tri.Length; i += 3) Check(Close(normals[tri[i]], normals[tri[i + 1]]) && Close(normals[tri[i]], normals[tri[i + 2]]), "Face normals not flat.");
        var a = new List<Vector4>(); var b = new List<Vector4>(); source.GetUVs(1, a); flat.GetUVs(1, b);
        for (int i = 0; i < map.Length; i++) Check(b[i] == a[map[i]], "Remapped UV changed.");
    }

    private static void ExtendedSkinReductionRejected()
    {
        var source = FiveInfluences(); bool rejected = false;
        try { MeshOperations.Reduce(source, null, Unrestricted()); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unsupported influence count silently truncated.");
    }

    private static void MultiFrameExpressions()
    {
        var mesh = Grid(4, true, true); var selected = Enumerable.Repeat(true, mesh.vertexCount).ToArray();
        MeshOperations.TransformVertices(mesh, selected, Matrix4x4.Scale(new Vector3(1, 2, 1)), true);
        var delta = new Vector3[mesh.vertexCount]; mesh.GetBlendShapeFrameVertices(0, 1, delta, null, null);
        Check(Close(delta[mesh.vertexCount / 2], new Vector3(0, 0.4f, 0)), "Expression displacement did not scale.");
        MeshOperations.RecalculateNormals(mesh, true);
        Check(mesh.GetBlendShapeFrameCount(0) == 2 && mesh.GetBlendShapeFrameWeight(0, 0) == 50, "Frame weight lost rebuilding normals.");
    }

    private static void PosedPreviewAndPrefab()
    {
        var root = Track(new GameObject("ValidationAvatar"));
        var boneA = new GameObject("BoneA").transform; boneA.SetParent(root.transform, false);
        var boneB = new GameObject("BoneB").transform; boneB.SetParent(root.transform, false); boneB.localPosition = new Vector3(0.1f, 0, 0);
        root.AddComponent<Animator>();
        var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = Grid(4, true, true);
        renderer.bones = new[] { boneA, boneB }; renderer.rootBone = boneA; renderer.SetBlendShapeWeight(0, 35);
        var session = Track(MeshSession.Create(MeshBinding.Find(root)[0])); Track(session.working);
        var sessions = new List<MeshSession> { session };
        var preview = Track(StylizedMesh.MeshPreview.Create(root, sessions, new Vector3(2, 0, 0)));
        Check(preview.GetComponent<Animator>() == null, "Preview executes original components.");
        var previewRenderer = preview.GetComponent<SkinnedMeshRenderer>();
        Check(previewRenderer.bones[0] != boneA && previewRenderer.GetBlendShapeWeight(0) == 35, "Preview rig/shapes incorrect.");
        string prefabPath = MeshExporter.Export(root, sessions, "Assets/ValidationOutput", false);
        var saved = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        var savedRenderer = saved.GetComponent<SkinnedMeshRenderer>();
        Check(saved.GetComponent<Animator>() != null && savedRenderer.bones[0].IsChildOf(saved.transform), "Prefab lost components or internal bone references.");
        Check(savedRenderer.sharedMesh != renderer.sharedMesh && savedRenderer.sharedMesh.blendShapeCount == 2, "Prefab mesh not independently saved.");
        AssetDatabase.DeleteAsset(Path.GetDirectoryName(prefabPath).Replace('\\', '/'));
    }

    private static void UndoRestoresEdits()
    {
        var mesh = Grid(4); var old = mesh.vertices;
        Undo.RegisterCompleteObjectUndo(mesh, "Validation edit");
        var selected = Enumerable.Repeat(true, mesh.vertexCount).ToArray();
        MeshOperations.TransformVertices(mesh, selected, Matrix4x4.Translate(Vector3.one), true);
        Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
        Check(mesh.vertices.SequenceEqual(old), "Undo failed to restore vertex data.");
        Undo.ClearUndo(mesh);
    }

    private static void CleanupKeepsUVAndShapes()
    {
        var mesh = Grid(4, true, true); var uv = mesh.uv; int count = mesh.blendShapeCount;
        var tris = mesh.GetTriangles(0).ToList(); tris.AddRange(new[] { 0, 0, 1 }); mesh.SetTriangles(tris, 0);
        Check(MeshOperations.RemoveDegenerateTriangles(mesh) == 1, "Degenerate triangle not removed.");
        Check(uv.SequenceEqual(mesh.uv) && count == mesh.blendShapeCount, "Cleanup destroyed attributes.");
    }

    private static void LargeMeshUsesUInt32()
    {
        var mesh = Grid(110); int[] map; var flat = Track(MeshOperations.FlatShade(mesh, out map));
        Check(flat.vertexCount > 65535 && flat.indexFormat == IndexFormat.UInt32, "Large remap overflowed 16-bit index format.");
    }

    private static void ShaderCompiles()
    {
        var shader = Shader.Find("StylizedMesh/Color Blocks"); Check(shader != null, "Shader missing.");
        Check(!ShaderUtil.ShaderHasError(shader), "Color-block shader compile error.");
    }

    private static void ZeroUVChannels()
    {
        var source = Grid(10);
        source.SetUVs(7, Enumerable.Repeat(Vector4.zero, source.vertexCount).ToList());
        var reduced = Track(MeshOperations.Reduce(source, null, Unrestricted()));
        Check(reduced.HasVertexAttribute(VertexAttribute.TexCoord7) && reduced.GetVertexAttributeDimension(VertexAttribute.TexCoord7) == 4, "All-zero UV channel or layout lost.");
    }

    private static object Invoke(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }

    private static void SetField(object target, string name, object value)
    { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }

    private static object GetField(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }

    private static void StaticSceneHandleAndUndo() { CheckSceneHandles(false); }
    private static void SkinnedSceneHandleWithActiveExpressions() { CheckSceneHandles(true); }

    private static void SceneCameraNavigationShortcuts()
    {
        var window = Track(ScriptableObject.CreateInstance<StylizedMeshWindow>());
        SetField(window, "sceneTool", 3);
        var mouseDown = new Event { type = EventType.MouseDown, button = 1 };
        Invoke(window, "HandleSceneInput", mouseDown, 1, false);
        foreach (var key in new[] { KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Q, KeyCode.E, KeyCode.R, KeyCode.Escape })
        {
            var evt = new Event { type = EventType.KeyDown, keyCode = key };
            Check((bool)Invoke(window, "HandleSceneInput", evt, 1, false), "Right mouse navigation not detected.");
            Check(evt.type == EventType.KeyDown && (int)GetField(window, "sceneTool") == 3, "Vertex shortcut consumed camera navigation: " + key);
        }
        // Repaints in a second Scene View must not cancel navigation in the first.
        Invoke(window, "HandleSceneInput", new Event { type = EventType.Repaint }, 2, false);
        var held = new Event { type = EventType.KeyDown, keyCode = KeyCode.W };
        Invoke(window, "HandleSceneInput", held, 1, false);
        Check(held.type == EventType.KeyDown, "Second scene view cleared first view's mouse state.");
        var mouseUp = new Event { type = EventType.MouseUp, button = 1 };
        Invoke(window, "HandleSceneInput", mouseUp, 1, false);
        foreach (var pair in new[] { new KeyValuePair<KeyCode, int>(KeyCode.W, 1), new KeyValuePair<KeyCode, int>(KeyCode.E, 2), new KeyValuePair<KeyCode, int>(KeyCode.R, 3), new KeyValuePair<KeyCode, int>(KeyCode.Q, 0) })
        {
            var evt = new Event { type = EventType.KeyDown, keyCode = pair.Key };
            Invoke(window, "HandleSceneInput", evt, 1, false);
            Check(evt.type == EventType.Used && (int)GetField(window, "sceneTool") == pair.Value, "Tool shortcut failed after releasing right mouse.");
        }
        foreach (var modifiers in new[] { EventModifiers.Alt, EventModifiers.Shift, EventModifiers.Control, EventModifiers.Command })
        {
            var evt = new Event { type = EventType.KeyDown, keyCode = KeyCode.W, modifiers = modifiers };
            Invoke(window, "HandleSceneInput", evt, 1, false);
            Check(evt.type == EventType.KeyDown && (int)GetField(window, "sceneTool") == 0, "Modified shortcut was intercepted.");
        }
        var nativeNavigation = new Event { type = EventType.KeyDown, keyCode = KeyCode.W };
        Invoke(window, "HandleSceneInput", nativeNavigation, 1, true);
        Check(nativeNavigation.type == EventType.KeyDown, "Native view navigation was intercepted.");
        // Event.Use on a synthetic Event changes both type and rawType. For already consumed
        // camera events, the native navigation flag must independently keep the shortcut inactive.
        var consumed = new Event { type = EventType.MouseDown, button = 1 }; consumed.Use();
        Check((bool)Invoke(window, "HandleSceneInput", consumed, 1, true), "Consumed native camera event was not recognized.");
        var nativeKey = new Event { type = EventType.KeyDown, keyCode = KeyCode.W };
        Invoke(window, "HandleSceneInput", nativeKey, 1, true);
        Check(nativeKey.type == EventType.KeyDown, "W was intercepted after native camera consumed the mouse event.");
        bool wasEditingText = EditorGUIUtility.editingTextField;
        try
        {
            EditorGUIUtility.editingTextField = true;
            var textEvent = new Event { type = EventType.KeyDown, keyCode = KeyCode.W };
            Invoke(window, "HandleSceneInput", textEvent, 1, false);
            Check(textEvent.type == EventType.KeyDown, "Text input was intercepted.");
        }
        finally { EditorGUIUtility.editingTextField = wasEditingText; }
        Invoke(window, "HandleSceneInput", new Event { type = EventType.MouseDown, button = 1 }, 1, false);
        Invoke(window, "HandleSceneInput", new Event { type = EventType.MouseLeaveWindow }, 1, false);
        var resumed = new Event { type = EventType.KeyDown, keyCode = KeyCode.W };
        Invoke(window, "HandleSceneInput", resumed, 1, false);
        Check(resumed.type == EventType.Used, "Leaving scene left navigation state stuck.");
        UnityEngine.Object.DestroyImmediate(window);
    }

    private static void PreviewVisibility()
    {
        var root = Track(new GameObject("VisibilityValidation"));
        root.AddComponent<MeshFilter>().sharedMesh = Grid(4);
        var original = root.AddComponent<MeshRenderer>();
        var child = new GameObject("Skin"); child.transform.SetParent(root.transform, false);
        var skin = child.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = Grid(4, false, true);
        var disabled = new GameObject("Disabled"); disabled.transform.SetParent(root.transform, false);
        disabled.AddComponent<MeshFilter>().sharedMesh = Grid(4); disabled.AddComponent<MeshRenderer>().enabled = false;
        var window = Track(ScriptableObject.CreateInstance<StylizedMeshWindow>());
        SetField(window, "source", root); Invoke(window, "Load");
        var preview = (GameObject)GetField(window, "preview");
        var renderers = preview.GetComponentsInChildren<Renderer>(true);
        var enabled = renderers.Select(r => r.enabled).ToArray();
        SetField(window, "tab", 1); SetField(window, "sceneEditing", true);
        for (int step = 0; step < 3; step++)
        {
            Invoke(window, "SetPreviewVisible", false);
            Check(!preview.activeSelf && renderers.All(r => r.forceRenderingOff), "Hidden preview still permits rendering.");
            Check(!Tools.hidden, "Hiding preview left editor handles hidden.");
            Invoke(window, "RefreshGeometry");
            Check(renderers.All(r => r.forceRenderingOff), "Geometry refresh restored hidden renderer.");
            Invoke(window, "SetPreviewVisible", true);
            Check(preview.activeInHierarchy && renderers.All(r => !r.forceRenderingOff), "Showing preview did not restore rendering.");
            Check(renderers.Select(r => r.enabled).SequenceEqual(enabled), "Showing preview changed disabled parts.");
        }
        Check(root.activeSelf && original.enabled && !original.forceRenderingOff, "Visibility changed original model.");
        UnityEngine.Object.DestroyImmediate(window);
        Check(preview == null, "Closing tool left orphan preview.");
    }

    private static void ExpressionPreviewDefersGeometry()
    {
        var root = Track(new GameObject("ExpressionPreviewValidation"));
        var mesh = Grid(100, false, true);
        var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
        var window = Track(ScriptableObject.CreateInstance<StylizedMeshWindow>());
        SetField(window, "source", root); Invoke(window, "Load");
        var session = ((List<MeshSession>)GetField(window, "sessions"))[0];
        var before = (Vector3[])GetField(window, "displayedVertices");
        var indices = GetField(window, "wireTriangles");
        var working = session.working;
        var vertices = working.vertices;
        for (int i = 0; i < 100; i++) Invoke(window, "SetExpressionWeight", 0, (float)i);
        var previewRenderer = (SkinnedMeshRenderer)session.previewRenderer;
        Check(previewRenderer.GetBlendShapeWeight(0) == 99, "Expression did not update renderer immediately.");
        Check(ReferenceEquals(before, GetField(window, "displayedVertices")), "Slider baked geometry eagerly.");
        Check(ReferenceEquals(indices, GetField(window, "wireTriangles")), "Slider rebuilt topology.");
        Check((bool)GetField(window, "poseDirty"), "Slider did not invalidate cached pose.");
        var expected = Track(new Mesh()); previewRenderer.BakeMesh(expected, true);
        Check(Close((Vector3)Invoke(window, "WorldVertex", 100), previewRenderer.transform.TransformPoint(expected.vertices[100])), "Deferred pose did not follow latest expression.");
        Check(!(bool)GetField(window, "poseDirty"), "Vertex access did not synchronize pose.");
        Check(ReferenceEquals(indices, GetField(window, "wireTriangles")), "Pose update rebuilt unchanged topology.");
        Check(session.working == working && working.vertices.SequenceEqual(vertices), "Expression preview modified edited mesh.");
        Check(renderer.GetBlendShapeWeight(0) == 0, "Expression changed original renderer.");
        UnityEngine.Object.DestroyImmediate(window);
    }

    private static void MultiStepWindowUndoRedo()
    {
        var root = Track(new GameObject("HistoryValidation"));
        var original = Grid(4, false, true);
        root.AddComponent<MeshFilter>().sharedMesh = original; root.AddComponent<MeshRenderer>();
        var window = Track(ScriptableObject.CreateInstance<StylizedMeshWindow>());
        SetField(window, "source", root); Invoke(window, "Load");
        var session = ((List<MeshSession>)GetField(window, "sessions"))[0];
        var meshes = (List<Mesh>)GetField(window, "ownedMeshes");
        for (int i = 0; i < session.selected.Length; i++) session.selected[i] = true;
        var stages = new List<Mesh> { session.working };
        Action first = () => MeshOperations.TransformVertices(session.working, session.selected, Matrix4x4.Translate(new Vector3(0.1f, 0, 0)), true);
        Action second = () => MeshOperations.TransformUV(session.working, 0, session.selected, new Vector2(0.05f, 0), Vector2.one, 0);
        // Run in the SAME editor event to catch accidental merging of consecutive undo groups.
        Invoke(window, "Edit", "Move", first, false); stages.Add(session.working);
        Invoke(window, "Edit", "UV", second, false); stages.Add(session.working);
        int[] map; var flat = MeshOperations.FlatShade(session.working, out map); meshes.Add(flat);
        session.Replace(flat, new bool[flat.vertexCount]); stages.Add(session.working);
        var restored = MeshOperations.Clone(original); meshes.Add(restored);
        session.Replace(restored, new bool[restored.vertexCount]); stages.Add(session.working);
        for (int i = stages.Count - 2; i >= 0; i--)
        {
            Invoke(window, "HistoryStep", false);
            Check(session.working == stages[i], "Undo skipped intermediate state " + i);
        }
        for (int i = 1; i < stages.Count; i++)
        {
            Invoke(window, "HistoryStep", true);
            Check(session.working == stages[i], "Redo skipped intermediate state " + i);
        }
        Invoke(window, "ChangeMask", "Select one", (Action)(() => session.selected[0] = true));
        Invoke(window, "ChangeMask", "Protect one", (Action)(() => session.protectedVertices[0] = true));
        Invoke(window, "HistoryStep", false);
        Check(session.selected[0] && !session.protectedVertices[0], "Mask undo merged separate actions.");
        Invoke(window, "HistoryStep", false);
        Check(!session.selected[0], "Selection undo failed.");
        Invoke(window, "HistoryStep", true); Invoke(window, "HistoryStep", true);
        Check(session.selected[0] && session.protectedVertices[0], "Mask redo failed.");
        Invoke(window, "HistoryStep", false);
        Invoke(window, "ChangeMask", "New protection", (Action)(() => session.protectedVertices[1] = true));
        Invoke(window, "HistoryStep", true);
        Check(!session.protectedVertices[0] && session.protectedVertices[1], "New action did not discard old redo branch.");
        Check(original.vertices.SequenceEqual(stages[0].vertices), "History changed original mesh.");
        UnityEngine.Object.DestroyImmediate(window);
    }

    private static void CheckSceneHandles(bool skinned)
    {
        for (int tool = 1; tool <= 3; tool++)
        {
            var root = Track(new GameObject("HandleValidation"));
            root.transform.position = new Vector3(0.4f, 0.2f, 0.3f);
            root.transform.rotation = Quaternion.Euler(5, 20, 10);
            root.transform.localScale = new Vector3(1.4f, 0.8f, 1.1f);
            var mesh = Grid(4, skinned, true);
            if (skinned)
            {
                var a = new GameObject("BoneA").transform; a.SetParent(root.transform, false);
                var b = new GameObject("BoneB").transform; b.SetParent(root.transform, false);
                b.localPosition = new Vector3(0.15f, 0.08f, 0); b.localRotation = Quaternion.Euler(0, 0, 25);
                var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                renderer.bones = new[] { a, b }; renderer.rootBone = a;
                renderer.SetBlendShapeWeight(0, 75); renderer.SetBlendShapeWeight(1, 20);
            }
            else { root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>(); }
            var sourceVertices = mesh.vertices;
            var window = Track(ScriptableObject.CreateInstance<StylizedMeshWindow>());
            SetField(window, "source", root); Invoke(window, "Load"); SetField(window, "sceneTool", tool);
            var sessions = (List<MeshSession>)GetField(window, "sessions"); var session = sessions[0];
            for (int i = 0; i < session.selected.Length; i++) session.selected[i] = i % 2 == 0;
            var before = new Vector3[mesh.vertexCount];
            for (int i = 0; i < before.Length; i++) before[i] = (Vector3)Invoke(window, "WorldVertex", i);
            Vector3 center = (Vector3)Invoke(window, "SelectionWorldCenter");
            Vector3 pivot = center + (tool == 1 ? new Vector3(0.1f, 0.04f, -0.03f) : Vector3.zero);
            Quaternion rotation = tool == 2 ? Quaternion.Euler(15, 25, 10) : Quaternion.identity;
            Vector3 scale = tool == 3 ? new Vector3(1.2f, 0.75f, 1.1f) : Vector3.one;
            Matrix4x4 matrix = Matrix4x4.TRS(pivot, rotation, scale) * Matrix4x4.Translate(-center);
            Invoke(window, "ApplyDrag", pivot, rotation, scale);
            for (int i = 0; i < before.Length; i++)
            {
                Vector3 expected = session.selected[i] ? matrix.MultiplyPoint3x4(before[i]) : before[i];
                var actual = (Vector3)Invoke(window, "WorldVertex", i);
                Check(Close(actual, expected, 1e-4f), "Live handle pose does not follow target: tool " + tool + " vertex " + i + " expected=" + expected.ToString("F6") + " actual=" + actual.ToString("F6") + " before=" + before[i].ToString("F6"));
            }
            Invoke(window, "EndDrag");
            for (int i = 0; i < before.Length; i++)
            {
                Vector3 expected = session.selected[i] ? matrix.MultiplyPoint3x4(before[i]) : before[i];
                Check(Close((Vector3)Invoke(window, "WorldVertex", i), expected, 1e-4f), "Committed handle pose transformed expression twice: tool " + tool);
            }
            Check(mesh.vertices.SequenceEqual(sourceVertices), "Handle mutated source mesh.");
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            float undoError = session.working.vertices.Select((vertex, i) => (vertex - sourceVertices[i]).magnitude).Max();
            Check(undoError < 1e-6f, "Handle undo did not restore rest vertices: tool " + tool + " max error " + undoError);
            UnityEngine.Object.DestroyImmediate(window);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
