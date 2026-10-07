using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StylizedMesh.Simplification;
using UnityEngine;

// Runs the actual bundled simplifier with Unity's managed math types.
// No Mesh, renderer, asset or editor execution is simulated here.
public static class AlgorithmChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Close(Vector3 a, Vector3 b) { return (a - b).sqrMagnitude < 1e-10f; }
    private static MeshSimplifier Grid(int side, bool flatX = false)
    {
        var mesh = new MeshSimplifier(); var options = SimplificationOptions.Default;
        options.PreserveBorderEdges = false; options.PreserveUVSeamEdges = false; options.PreserveUVFoldoverEdges = false;
        options.VertexLinkDistance = 1e-8; mesh.SimplificationOptions = options;
        var vertices = new Vector3[side * side]; var uv = new Vector2[vertices.Length];
        var skin = new BoneWeight[vertices.Length]; var colors = new Color[vertices.Length];
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
        {
            int i = y * side + x; float u = (float)x / (side - 1), v = (float)y / (side - 1);
            vertices[i] = flatX ? new Vector3(0, u, v) : new Vector3(u, v, (float)Math.Sin(u * 2) * (float)Math.Sin(v * 2) * 0.1f);
            uv[i] = new Vector2(u, v); colors[i] = new Color(u, v, 0.5f, 1);
            skin[i] = new BoneWeight { boneIndex0 = 0, boneIndex1 = 1, weight0 = 1 - u, weight1 = u };
        }
        mesh.Vertices = vertices; mesh.UV1 = uv; mesh.Colors = colors; mesh.BoneWeights = skin;
        var tris = new List<int>();
        for (int y = 0; y < side - 1; y++) for (int x = 0; x < side - 1; x++)
        { int i = y * side + x; tris.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 }); }
        mesh.AddSubMeshTriangles(tris.ToArray()); return mesh;
    }

    private static void ReductionAndAttributes()
    {
        var mesh = Grid(20); int before = mesh.GetSubMeshTriangles(0).Length;
        mesh.SimplifyMesh(0.3f);
        Check(mesh.GetSubMeshTriangles(0).Length < before, "No reduction.");
        Check(mesh.UV1.Length == mesh.Vertices.Length && mesh.Colors.Length == mesh.Vertices.Length, "Attributes lost.");
        Check(mesh.Vertices.All(v => !float.IsNaN(v.x) && !float.IsInfinity(v.x)), "Non-finite output.");
    }

    private static void ProtectedVertices()
    {
        var mesh = Grid(20); int index = 10 * 20 + 10; Vector3 position = mesh.Vertices[index]; Vector2 uv = mesh.UV1[index];
        var mask = new bool[mesh.Vertices.Length]; mask[index] = true; mesh.SetProtectedVertices(mask);
        mesh.SimplifyMesh(0.05f);
        int output = Array.FindIndex(mesh.Vertices, p => Close(p, position));
        Check(output >= 0 && mesh.UV1[output] == uv, "Protected position or UV changed.");
    }

    private static void WeightInterpolation()
    {
        var mesh = Grid(18); mesh.SimplifyMesh(0.15f);
        var skin = mesh.BoneWeights; var vertices = mesh.Vertices;
        for (int i = 0; i < skin.Length; i++)
        {
            var w = skin[i]; float sum = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            Check(Math.Abs(sum - 1) < 1e-5, "Weights not normalized.");
            Check(w.weight0 >= 0 && w.weight1 >= 0 && w.weight2 >= 0 && w.weight3 >= 0, "Negative weight.");
            float second = (w.boneIndex0 == 1 ? w.weight0 : 0) + (w.boneIndex1 == 1 ? w.weight1 : 0) +
                (w.boneIndex2 == 1 ? w.weight2 : 0) + (w.boneIndex3 == 1 ? w.weight3 : 0);
            Check(Math.Abs(second - vertices[i].x) < 0.03f, "Weights detached from geometry: vertex=" + i + ", x=" + vertices[i].x + ", weight=" + second);
        }
    }

    private static void ShapeFrames()
    {
        var mesh = Grid(16); var dv = mesh.Vertices.Select(v => new Vector3(0, v.x * 0.1f, 0)).ToArray();
        var zero = new Vector3[dv.Length];
        mesh.AddBlendShape(new BlendShape("Smile", new[] { new BlendShapeFrame(50, dv, zero, zero), new BlendShapeFrame(100, dv.Select(v => v * 2).ToArray(), zero, zero) }));
        mesh.SimplifyMesh(0.3f); var shapes = mesh.GetAllBlendShapes();
        Check(shapes.Length == 1 && shapes[0].ShapeName == "Smile" && shapes[0].Frames.Length == 2, "Shape metadata lost.");
        Check(shapes[0].Frames[0].FrameWeight == 50 && shapes[0].Frames[1].DeltaVertices.Length == mesh.Vertices.Length, "Frame weight or remap invalid.");
        for (int i = 0; i < mesh.Vertices.Length; i++)
            Check(Math.Abs(shapes[0].Frames[1].DeltaVertices[i].y - mesh.Vertices[i].x * 0.2f) < 0.005f, "Expression interpolation invalid.");
    }

    private static void UVSeams()
    {
        const int side = 10; var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
        for (int half = 0; half < 2; half++)
        {
            int offset = vertices.Count;
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
            { float u = (float)x / (side - 1), v = (float)y / (side - 1); vertices.Add(new Vector3(half - 1 + u, v, 0)); uv.Add(new Vector2(u * 0.4f + half * 0.6f, v)); }
            for (int y = 0; y < side - 1; y++) for (int x = 0; x < side - 1; x++)
            { int i = offset + y * side + x; tris.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 }); }
        }
        var mesh = new MeshSimplifier(); var options = SimplificationOptions.Default;
        options.PreserveUVSeamEdges = true; options.VertexLinkDistance = 1e-8; mesh.SimplificationOptions = options;
        mesh.Vertices = vertices.ToArray(); mesh.UV1 = uv.ToArray(); mesh.AddSubMeshTriangles(tris.ToArray()); mesh.SimplifyMesh(0.2f);
        var output = mesh.Vertices; var tex = mesh.UV1;
        for (int y = 1; y < side - 1; y++)
        {
            Vector3 p = new Vector3(0, (float)y / (side - 1), 0);
            Check(Enumerable.Range(0, output.Length).Any(i => Close(output[i], p) && Math.Abs(tex[i].x - 0.4f) < 1e-5), "Left seam lost.");
            Check(Enumerable.Range(0, output.Length).Any(i => Close(output[i], p) && Math.Abs(tex[i].x - 0.6f) < 1e-5), "Right seam lost.");
        }
    }

    private static void Cancellation()
    {
        var mesh = Grid(10); mesh.CancellationRequested = p => true; bool canceled = false;
        try { mesh.SimplifyMesh(0.1f); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Cancel callback ignored.");
    }

    private static void ZeroWidthBounds()
    {
        var mesh = Grid(12, true); mesh.SimplifyMesh(0.25f);
        Check(mesh.Vertices.All(v => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.z)), "Zero-width smart link produced invalid coordinates.");
        Check(mesh.GetSubMeshTriangles(0).Length > 0, "Whole mesh disappeared.");
    }

    public static int Main(string[] args)
    {
        var results = new List<string>(); int failures = 0;
        foreach (var test in new Action[] { ReductionAndAttributes, ProtectedVertices, WeightInterpolation, ShapeFrames, UVSeams, Cancellation, ZeroWidthBounds })
        {
            try { test(); results.Add("PASS " + test.Method.Name); }
            catch (Exception e) { failures++; results.Add("FAIL " + test.Method.Name + ": " + e); }
        }
        results.Add("Managed algorithm tests: " + (7 - failures) + "/7 passed. Unity engine/editor integration tests were not run.");
        File.WriteAllLines(args[0], results); Console.WriteLine(string.Join("\n", results));
        return failures == 0 ? 0 : 1;
    }
}
