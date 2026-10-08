using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using StylizedMesh.Simplification;

namespace StylizedMesh
{
    [Serializable]
    public sealed class ReduceSettings
    {
        public float quality = 0.5f;
        public bool borders = true;
        public bool uvSeams = true;
        public bool uvFoldovers = true;
        public bool materialBoundaries = true;
        public bool movingBlendShapeVertices;
        public bool boneBoundaries = true;
        public bool curvature;

        public static ReduceSettings Conservative()
        { return new ReduceSettings { quality = 0.65f, curvature = true, movingBlendShapeVertices = true }; }

        public static ReduceSettings Strong()
        { return new ReduceSettings { quality = 0.2f, borders = false, uvSeams = false, uvFoldovers = false }; }
    }

    public sealed class ReductionReport
    {
        public int originalTriangles, reducedTriangles, vertexCount, lockedVertexCount;
        public bool FullyProtected { get { return vertexCount > 0 && lockedVertexCount == vertexCount; } }
        public bool Reduced { get { return reducedTriangles < originalTriangles; } }
    }

    public static class MeshOperations
    {
        public static int TriangleCount(Mesh mesh)
        {
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) count += (int)mesh.GetIndexCount(s) / 3;
            return count;
        }

        public static void Validate(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount == 0) throw new InvalidOperationException("网格为空。");
            if (!mesh.isReadable) throw new InvalidOperationException("请在模型导入设置中开启 Read/Write，再载入模型。");
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                    throw new InvalidOperationException("目前仅支持三角形网格。");
            foreach (var vertex in mesh.vertices)
                if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z))
                    throw new InvalidOperationException("网格包含非有限坐标。");
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        public static Mesh Clone(Mesh source)
        {
            var result = UnityEngine.Object.Instantiate(source);
            result.name = source.name + "_Edited";
            result.hideFlags = HideFlags.HideAndDontSave;
            return result;
        }

        public static int[][] Neighbors(Mesh mesh)
        {
            var sets = new HashSet<int>[mesh.vertexCount];
            for (int i = 0; i < sets.Length; i++) sets[i] = new HashSet<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    sets[a].Add(b); sets[a].Add(c); sets[b].Add(a);
                    sets[b].Add(c); sets[c].Add(a); sets[c].Add(b);
                }
            }
            var result = new int[sets.Length][];
            for (int i = 0; i < sets.Length; i++) { result[i] = new int[sets[i].Count]; sets[i].CopyTo(result[i]); }
            return result;
        }

        // Equal-position duplicates are selected together without welding their UVs/normals/weights.
        public static void ExpandCoincident(Vector3[] vertices, bool[] mask, bool mirror = false)
        {
            var positions = new HashSet<Vector3>();
            for (int i = 0; i < mask.Length; i++) if (mask[i])
            {
                positions.Add(vertices[i]);
                if (mirror) positions.Add(new Vector3(-vertices[i].x, vertices[i].y, vertices[i].z));
            }
            for (int i = 0; i < mask.Length; i++) if (positions.Contains(vertices[i])) mask[i] = true;
        }

        public static bool[] ReductionMask(Mesh mesh, bool[] userMask, ReduceSettings settings)
        {
            int count = mesh.vertexCount;
            var mask = userMask == null ? new bool[count] : (bool[])userMask.Clone();
            if (mask.Length != count) throw new ArgumentException("保护范围与网格顶点数不一致。");
            if (settings.materialBoundaries)
            {
                var owner = new int[count];
                for (int i = 0; i < count; i++) owner[i] = -1;
                var positionOwners = new Dictionary<Vector3, int>();
                var vertices = mesh.vertices;
                for (int s = 0; s < mesh.subMeshCount; s++) foreach (int v in mesh.GetTriangles(s))
                {
                    if (owner[v] >= 0 && owner[v] != s) mask[v] = true;
                    owner[v] = s;
                    int previous;
                    if (positionOwners.TryGetValue(vertices[v], out previous) && previous != s)
                        positionOwners[vertices[v]] = -2;
                    else if (!positionOwners.ContainsKey(vertices[v])) positionOwners[vertices[v]] = s;
                }
                for (int i = 0; i < count; i++)
                    if (positionOwners.ContainsKey(vertices[i]) && positionOwners[vertices[i]] == -2) mask[i] = true;
            }
            if (settings.movingBlendShapeVertices)
            {
                var delta = new Vector3[count];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(s); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(s, frame, delta, null, null);
                        for (int i = 0; i < count; i++) if (delta[i].sqrMagnitude > 1e-14f) mask[i] = true;
                    }
            }
            var adjacency = Neighbors(mesh);
            if (settings.boneBoundaries)
            {
                var weights = mesh.boneWeights;
                if (weights.Length == count)
                    for (int i = 0; i < count; i++) foreach (int j in adjacency[i])
                        if (weights[i].weight0 > 0 && weights[j].weight0 > 0 && DominantBone(weights[i]) != DominantBone(weights[j]))
                        { mask[i] = true; mask[j] = true; }
            }
            // Lock a one-ring buffer so simplification cannot collapse triangles containing a protected vertex.
            var buffered = (bool[])mask.Clone();
            for (int i = 0; i < count; i++) if (mask[i]) foreach (int j in adjacency[i]) buffered[j] = true;
            ExpandCoincident(mesh.vertices, buffered);
            return buffered;
        }

        private static int DominantBone(BoneWeight w)
        {
            int index = w.boneIndex0; float weight = w.weight0;
            if (w.weight1 > weight) { index = w.boneIndex1; weight = w.weight1; }
            if (w.weight2 > weight) { index = w.boneIndex2; weight = w.weight2; }
            if (w.weight3 > weight) index = w.boneIndex3;
            return index;
        }

        public static Mesh Reduce(Mesh source, bool[] protection, ReduceSettings settings, Func<float, bool> cancel = null)
        {
            ReductionReport report;
            return Reduce(source, protection, settings, out report, cancel);
        }

        public static Mesh Reduce(Mesh source, bool[] protection, ReduceSettings settings, out ReductionReport report, Func<float, bool> cancel = null)
        {
            report = new ReductionReport();
            Validate(source);
            report.originalTriangles = report.reducedTriangles = TriangleCount(source);
            report.vertexCount = source.vertexCount;
            using (var counts = source.GetBonesPerVertex())
                foreach (byte count in counts)
                    if (count > 4) throw new InvalidOperationException("该网格有超过 4 个骨骼影响的顶点。减面暂不支持；顶点编辑与平面着色仍会保留全部权重。");
            if (settings.quality >= 0.99999f) return Clone(source);
            if (cancel != null && cancel(0)) throw new OperationCanceledException();
            var mask = ReductionMask(source, protection, settings);
            foreach (bool locked in mask) if (locked) report.lockedVertexCount++;
            // Do not load/interpolate hundreds of expression frames for an impossible collapse.
            if (report.FullyProtected) return Clone(source);
            var simplifier = new MeshSimplifier();
            var options = SimplificationOptions.Default;
            options.PreserveBorderEdges = settings.borders;
            options.PreserveUVSeamEdges = settings.uvSeams;
            options.PreserveUVFoldoverEdges = settings.uvFoldovers;
            options.PreserveSurfaceCurvature = settings.curvature;
            options.EnableSmartLink = true;
            options.VertexLinkDistance = 1e-8;
            simplifier.SimplificationOptions = options;
            simplifier.Initialize(source);
            // Upstream auto-detection omits all-zero channels and shrinks dimensions with unused Z/W.
            // Use the actual vertex layout instead, including custom shader data in UV2–UV7.
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!source.HasVertexAttribute(attribute)) continue;
                var uv = new List<Vector4>(); source.GetUVs(channel, uv);
                simplifier.SetUVs(channel, uv, source.GetVertexAttributeDimension(attribute));
            }
            simplifier.SetProtectedVertices(mask);
            simplifier.CancellationRequested = cancel;
            simplifier.SimplifyMesh(Mathf.Clamp(settings.quality, 0.01f, 1f));
            Mesh result = simplifier.ToMesh();
            result.hideFlags = HideFlags.HideAndDontSave;
            result.name = source.name;
            KeepBounds(result, source.bounds);
            report.reducedTriangles = TriangleCount(result);
            return result;
        }

        public static void KeepBounds(Mesh mesh, Bounds previous)
        {
            mesh.RecalculateBounds();
            previous.Encapsulate(mesh.bounds);
            mesh.bounds = previous;
        }

        public static bool[] RemapProtection(Mesh oldMesh, bool[] oldMask, Mesh newMesh)
        {
            var positions = new HashSet<Vector3>();
            var vertices = oldMesh.vertices;
            for (int i = 0; i < oldMask.Length; i++) if (oldMask[i]) positions.Add(vertices[i]);
            var newVertices = newMesh.vertices;
            var mask = new bool[newVertices.Length];
            for (int i = 0; i < mask.Length; i++) mask[i] = positions.Contains(newVertices[i]);
            return mask;
        }

        public static void TransformVertices(Mesh mesh, bool[] selection, Matrix4x4 transform, bool transformExpressions)
        {
            var vertices = mesh.vertices;
            var bounds = mesh.bounds;
            for (int i = 0; i < vertices.Length; i++) if (selection[i]) vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
            if (transformExpressions)
                RewriteShapes(mesh, (dv, dn, dt) =>
                {
                    for (int i = 0; i < dv.Length; i++) if (selection[i])
                    {
                        dv[i] = transform.MultiplyVector(dv[i]);
                        dn[i] = transform.inverse.transpose.MultiplyVector(dn[i]);
                        dt[i] = transform.MultiplyVector(dt[i]);
                    }
                });
            mesh.vertices = vertices;
            KeepBounds(mesh, bounds);
        }

        public static void Snap(Mesh mesh, bool[] selection, float step)
        {
            if (!Finite(step) || step <= 0) throw new ArgumentException("吸附间距必须大于 0。");
            var vertices = mesh.vertices;
            var bounds = mesh.bounds;
            for (int i = 0; i < vertices.Length; i++) if (selection[i])
                vertices[i] = new Vector3(Mathf.Round(vertices[i].x / step), Mathf.Round(vertices[i].y / step), Mathf.Round(vertices[i].z / step)) * step;
            // Preserve expression offsets; snapping animated frame positions would quantize lip sync.
            mesh.vertices = vertices;
            KeepBounds(mesh, bounds);
        }

        public static void Inflate(Mesh mesh, bool[] selection, float amount)
        {
            var normals = mesh.normals;
            if (normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
            normals = mesh.normals;
            var vertices = mesh.vertices;
            var bounds = mesh.bounds;
            for (int i = 0; i < vertices.Length; i++) if (selection[i]) vertices[i] += normals[i] * amount;
            mesh.vertices = vertices;
            KeepBounds(mesh, bounds);
        }

        public static void Smooth(Mesh mesh, bool[] selection, float strength, int iterations, bool preserveBorders)
        {
            var adjacent = Neighbors(mesh);
            var borders = preserveBorders ? BoundaryVertices(mesh) : new bool[mesh.vertexCount];
            var vertices = mesh.vertices;
            var bounds = mesh.bounds;
            for (int iteration = 0; iteration < Mathf.Clamp(iterations, 1, 30); iteration++)
                vertices = SmoothArray(vertices, selection, borders, adjacent, Mathf.Clamp01(strength));
            // Apply the same linear smoothing operator to expression displacement fields.
            RewriteShapes(mesh, (dv, dn, dt) =>
            {
                for (int iteration = 0; iteration < Mathf.Clamp(iterations, 1, 30); iteration++)
                {
                    var next = SmoothArray(dv, selection, borders, adjacent, Mathf.Clamp01(strength));
                    Array.Copy(next, dv, dv.Length);
                }
            });
            mesh.vertices = vertices;
            KeepBounds(mesh, bounds);
        }

        private static Vector3[] SmoothArray(Vector3[] values, bool[] selected, bool[] borders, int[][] adjacent, float strength)
        {
            var next = (Vector3[])values.Clone();
            for (int i = 0; i < values.Length; i++) if (selected[i] && !borders[i] && adjacent[i].Length > 0)
            {
                var mean = Vector3.zero;
                foreach (int j in adjacent[i]) mean += values[j];
                next[i] = Vector3.Lerp(values[i], mean / adjacent[i].Length, strength);
            }
            return next;
        }

        private struct Edge : IEquatable<Edge>
        {
            public int a, b;
            public Edge(int i, int j) { a = Math.Min(i, j); b = Math.Max(i, j); }
            public bool Equals(Edge other) { return a == other.a && b == other.b; }
            public override bool Equals(object obj) { return obj is Edge && Equals((Edge)obj); }
            public override int GetHashCode() { unchecked { return a * 397 ^ b; } }
        }

        public static bool[] BoundaryVertices(Mesh mesh)
        {
            var edges = new Dictionary<Edge, int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i < tris.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var edge = new Edge(tris[i + k], tris[i + (k + 1) % 3]);
                        int count; edges.TryGetValue(edge, out count); edges[edge] = count + 1;
                    }
            }
            var boundary = new bool[mesh.vertexCount];
            foreach (var edge in edges) if (edge.Value != 2) { boundary[edge.Key.a] = true; boundary[edge.Key.b] = true; }
            return boundary;
        }

        public static void TransformUV(Mesh mesh, int channel, bool[] selected, Vector2 offset, Vector2 scale, float degrees)
        {
            var uv = new List<Vector4>(); mesh.GetUVs(channel, uv);
            if (uv.Count != mesh.vertexCount) throw new InvalidOperationException("所选 UV 通道不存在。");
            Vector2 center = Vector2.zero; int count = 0;
            for (int i = 0; i < uv.Count; i++) if (selected[i]) { center += new Vector2(uv[i].x, uv[i].y); count++; }
            if (count == 0) throw new InvalidOperationException("请先选择顶点或 UV。");
            center /= count;
            float sin = Mathf.Sin(degrees * Mathf.Deg2Rad), cos = Mathf.Cos(degrees * Mathf.Deg2Rad);
            for (int i = 0; i < uv.Count; i++) if (selected[i])
            {
                Vector2 d = Vector2.Scale(new Vector2(uv[i].x, uv[i].y) - center, scale);
                Vector2 p = center + new Vector2(d.x * cos - d.y * sin, d.x * sin + d.y * cos) + offset;
                var value = uv[i]; value.x = p.x; value.y = p.y; uv[i] = value;
            }
            SetUV(mesh, channel, uv, mesh.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)));
            mesh.RecalculateTangents();
        }

        public static void RelaxUV(Mesh mesh, int channel, bool[] selected, float strength, int iterations)
        {
            var uv = new List<Vector4>(); mesh.GetUVs(channel, uv);
            if (uv.Count != mesh.vertexCount) throw new InvalidOperationException("所选 UV 通道不存在。");
            int dimension = mesh.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel));
            var neighbors = Neighbors(mesh);
            var boundary = BoundaryVertices(mesh);
            for (int iteration = 0; iteration < Mathf.Clamp(iterations, 1, 30); iteration++)
            {
                var next = new List<Vector4>(uv);
                for (int i = 0; i < uv.Count; i++) if (selected[i] && !boundary[i] && neighbors[i].Length > 0)
                {
                    Vector2 mean = Vector2.zero;
                    foreach (int j in neighbors[i]) mean += new Vector2(uv[j].x, uv[j].y);
                    var p = Vector2.Lerp(new Vector2(uv[i].x, uv[i].y), mean / neighbors[i].Length, Mathf.Clamp01(strength));
                    var value = next[i]; value.x = p.x; value.y = p.y; next[i] = value;
                }
                uv = next;
            }
            SetUV(mesh, channel, uv, dimension);
            mesh.RecalculateTangents();
        }

        public static void SetUV(Mesh mesh, int channel, List<Vector4> uv, int dimension)
        {
            if (dimension == 2) mesh.SetUVs(channel, uv.ConvertAll(v => new Vector2(v.x, v.y)));
            else if (dimension == 3) mesh.SetUVs(channel, uv.ConvertAll(v => new Vector3(v.x, v.y, v.z)));
            else mesh.SetUVs(channel, uv);
        }

        private sealed class ShapeFrame
        {
            public string name; public float weight; public Vector3[] dv, dn, dt;
        }

        private static void VisitShapes(Mesh mesh, Action<ShapeFrame> visitor)
        {
            var data = new ShapeFrame { dv = new Vector3[mesh.vertexCount], dn = new Vector3[mesh.vertexCount], dt = new Vector3[mesh.vertexCount] };
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    data.name = mesh.GetBlendShapeName(shape); data.weight = mesh.GetBlendShapeFrameWeight(shape, frame);
                    mesh.GetBlendShapeFrameVertices(shape, frame, data.dv, data.dn, data.dt);
                    visitor(data);
                }
        }

        private static void RewriteShapes(Mesh mesh, Action<Vector3[], Vector3[], Vector3[]> edit)
        {
            if (mesh.blendShapeCount == 0) return;
            // Stream one frame at a time instead of retaining every avatar expression in managed arrays.
            var source = UnityEngine.Object.Instantiate(mesh);
            source.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                mesh.ClearBlendShapes();
                VisitShapes(source, frame =>
                {
                    edit(frame.dv, frame.dn, frame.dt);
                    mesh.AddBlendShapeFrame(frame.name, frame.weight, frame.dv, frame.dn, frame.dt);
                });
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        public static void TransformExpressionDeltas(Mesh mesh, bool[] selection, Func<int, Vector3, Vector3> transform)
        {
            RewriteShapes(mesh, (dv, dn, dt) =>
            {
                for (int i = 0; i < dv.Length; i++) if (selection[i]) dv[i] = transform(i, dv[i]);
            });
        }

        public static void RecalculateNormals(Mesh mesh, bool shapeNormals)
        {
            mesh.RecalculateNormals();
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) mesh.RecalculateTangents();
            if (!shapeNormals || mesh.blendShapeCount == 0) return;
            var baseVertices = mesh.vertices;
            var baseNormals = mesh.normals;
            var baseTangents = mesh.tangents;
            var scratch = new Mesh { indexFormat = mesh.indexFormat };
            var source = UnityEngine.Object.Instantiate(mesh);
            source.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                scratch.vertices = baseVertices; scratch.subMeshCount = mesh.subMeshCount;
                for (int s = 0; s < mesh.subMeshCount; s++) scratch.SetTriangles(mesh.GetTriangles(s), s);
                scratch.uv = mesh.uv;
                mesh.ClearBlendShapes();
                VisitShapes(source, frame =>
                {
                    var vertices = new Vector3[mesh.vertexCount];
                    for (int i = 0; i < vertices.Length; i++) vertices[i] = baseVertices[i] + frame.dv[i];
                    scratch.vertices = vertices; scratch.RecalculateNormals();
                    var normals = scratch.normals;
                    for (int i = 0; i < vertices.Length; i++) frame.dn[i] = normals[i] - baseNormals[i];
                    if (scratch.uv.Length == vertices.Length)
                    {
                        scratch.RecalculateTangents(); var tangents = scratch.tangents;
                        for (int i = 0; i < vertices.Length; i++) frame.dt[i] = (Vector3)(tangents[i] - baseTangents[i]);
                    }
                    mesh.AddBlendShapeFrame(frame.name, frame.weight, frame.dv, frame.dn, frame.dt);
                });
            }
            finally { UnityEngine.Object.DestroyImmediate(scratch); UnityEngine.Object.DestroyImmediate(source); }
        }

        // Split triangle corners, keeping every UV channel, all skin influences and every blend-shape frame.
        public static Mesh FlatShade(Mesh source, out int[] sourceIndices)
        {
            var map = new List<int>(); var indices = new List<int[]>();
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var tris = source.GetTriangles(s); var dst = new int[tris.Length];
                for (int i = 0; i < tris.Length; i++) { dst[i] = map.Count; map.Add(tris[i]); }
                indices.Add(dst);
            }
            sourceIndices = map.ToArray();
            Mesh result = Remap(source, sourceIndices, indices);
            RecalculateNormals(result, true);
            return result;
        }

        private static T[] Map<T>(T[] values, int[] map)
        {
            if (values.Length == 0) return new T[0];
            var result = new T[map.Length];
            for (int i = 0; i < result.Length; i++) result[i] = values[map[i]];
            return result;
        }

        private static Mesh Remap(Mesh source, int[] map, List<int[]> submeshes)
        {
            var result = new Mesh { name = source.name, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = map.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                result.vertices = Map(source.vertices, map);
                result.normals = Map(source.normals, map);
                result.tangents = Map(source.tangents, map);
                result.colors = Map(source.colors, map);
                result.bindposes = source.bindposes;
                for (int channel = 0; channel < 8; channel++)
                {
                    var values = new List<Vector4>(); source.GetUVs(channel, values);
                    if (values.Count == 0) continue;
                    var mapped = new List<Vector4>(map.Length);
                    foreach (int i in map) mapped.Add(values[i]);
                    SetUV(result, channel, mapped, source.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)));
                }
                using (var counts = source.GetBonesPerVertex())
                using (var weights = source.GetAllBoneWeights())
                {
                    if (counts.Length == source.vertexCount && weights.Length > 0)
                    {
                        var offsets = new int[counts.Length]; int offset = 0, total = 0;
                        for (int i = 0; i < counts.Length; i++) { offsets[i] = offset; offset += counts[i]; }
                        foreach (int i in map) total += counts[i];
                        var mappedCounts = new NativeArray<byte>(map.Length, Allocator.Temp);
                        var mappedWeights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
                        try
                        {
                            int cursor = 0;
                            for (int i = 0; i < map.Length; i++)
                            {
                                int old = map[i]; mappedCounts[i] = counts[old];
                                for (int j = 0; j < counts[old]; j++) mappedWeights[cursor++] = weights[offsets[old] + j];
                            }
                            result.SetBoneWeights(mappedCounts, mappedWeights);
                        }
                        finally { mappedCounts.Dispose(); mappedWeights.Dispose(); }
                    }
                }
                result.subMeshCount = submeshes.Count;
                for (int s = 0; s < submeshes.Count; s++) result.SetTriangles(submeshes[s], s);
                VisitShapes(source, frame =>
                    result.AddBlendShapeFrame(frame.name, frame.weight, Map(frame.dv, map), Map(frame.dn, map), Map(frame.dt, map)));
                KeepBounds(result, source.bounds);
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }

        public static int RemoveDegenerateTriangles(Mesh mesh)
        {
            int removed = 0; var vertices = mesh.vertices;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var source = mesh.GetTriangles(s); var kept = new List<int>(source.Length);
                for (int i = 0; i < source.Length; i += 3)
                {
                    int a = source[i], b = source[i + 1], c = source[i + 2];
                    if (a == b || b == c || a == c || Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude < 1e-20f)
                        removed++;
                    else { kept.Add(a); kept.Add(b); kept.Add(c); }
                }
                mesh.SetTriangles(kept, s, false);
            }
            return removed;
        }

        public static string UVReport(Mesh mesh, int channel)
        {
            var uv = new List<Vector4>(); mesh.GetUVs(channel, uv);
            if (uv.Count == 0) return "该通道没有 UV。";
            int outside = 0, zeroArea = 0, negative = 0;
            foreach (var value in uv) if (value.x < 0 || value.x > 1 || value.y < 0 || value.y > 1) outside++;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector4 a = uv[tris[i]], b = uv[tris[i + 1]], c = uv[tris[i + 2]];
                    float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (Mathf.Abs(area) < 1e-12f) zeroArea++;
                    else if (area < 0) negative++;
                }
            }
            return $"UV 顶点 {uv.Count:N0} · 零面积三角形 {zeroArea:N0}\n反向 UV 三角形 {negative:N0} · 超出 0–1 的顶点 {outside:N0}\n镜像 UV 与平铺 UV 可能是原设计，并不一定有错。";
        }
    }
}
