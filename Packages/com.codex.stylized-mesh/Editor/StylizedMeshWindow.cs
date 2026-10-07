using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace StylizedMesh
{
    public sealed class StylizedMeshWindow : EditorWindow
    {
        [SerializeField] private GameObject source;
        [SerializeField] private ReduceSettings reduction = new ReduceSettings();
        private readonly List<MeshSession> sessions = new List<MeshSession>();
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();
        private readonly HashSet<int> cameraNavigationViews = new HashSet<int>();
        private GameObject preview;
        private Renderer[] previewRenderers;
        private bool previewVisible = true, poseDirty;
        private Mesh expressionNamesMesh;
        private string[] expressionNames;
        private int activeIndex, tab, sceneTool, selectionMode, flattenAxis, uvChannel;
        private Vector2 scroll;
        private Vector3 previewOffset = new Vector3(1.2f, 0, 0);
        private float brushRadius = 24f, gridStep = 0.01f, smoothStrength = 0.3f, inflateAmount = 0.005f;
        private int smoothIterations = 1;
        private bool sceneEditing, showWire = true, selectBackfaces = true, linkCoincident = true, mirrorSelection;
        private bool preserveSmoothBorders = true, transformExpressions = true, shapeNormals = true;
        private Vector3 numericMove, numericRotate, numericScale = Vector3.one;
        private Vector2 uvMove, uvScale = Vector2.one;
        private float uvRotate;
        private string outputFolder = "Assets/StylizedMeshOutput", status = "请选择完整模型根对象。";
        private bool outputColorBlocks;
        private Mesh posedMesh;
        private Vector3[] displayedVertices, displayedNormals;
        private int[] wireTriangles;
        private Vector2 boxStart, boxEnd;
        private bool boxSelecting, brushSelecting, subtractSelection;
        private int selectionUndoGroup = -1;
        private Vector3[] dragOriginal, dragWorld;
        private Matrix4x4[] dragSkin;
        private Vector3 handlePivot, dragPivot, handleScale = Vector3.one;
        private Quaternion handleRotation = Quaternion.identity;
        private Matrix4x4 dragWorldTransform = Matrix4x4.identity;
        private int dragUndoGroup = -1;
        private int expressionIndex;

        private MeshSession Active { get { return activeIndex >= 0 && activeIndex < sessions.Count ? sessions[activeIndex] : null; } }

        [MenuItem("Tools/Stylized Mesh Studio/网格改模工具")]
        public static void Open()
        {
            var window = GetWindow<StylizedMeshWindow>();
            window.titleContent = new GUIContent("网格改模");
            window.minSize = new Vector2(420, 650);
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += SceneGUI;
            Undo.undoRedoPerformed += OnUndo;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= SceneGUI;
            Undo.undoRedoPerformed -= OnUndo;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            Dispose();
        }

        private void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) Dispose();
        }

        private void Dispose()
        {
            EndDrag();
            Tools.hidden = false;
            if (preview != null) DestroyImmediate(preview);
            if (posedMesh != null) DestroyImmediate(posedMesh);
            foreach (var session in sessions)
            {
                if (session == null) continue;
                Undo.ClearUndo(session);
                DestroyImmediate(session);
            }
            foreach (var mesh in ownedMeshes.Distinct())
            {
                if (mesh == null || EditorUtility.IsPersistent(mesh)) continue;
                Undo.ClearUndo(mesh); DestroyImmediate(mesh);
            }
            sessions.Clear(); ownedMeshes.Clear();
            cameraNavigationViews.Clear();
            preview = null; posedMesh = null; displayedVertices = null;
            previewRenderers = null; expressionNamesMesh = null; expressionNames = null; poseDirty = false;
            sceneEditing = false;
        }

        private void OnUndo()
        {
            dragOriginal = null; dragSkin = null; dragUndoGroup = -1;
            dragWorld = null; handleRotation = Quaternion.identity; handleScale = Vector3.one;
            foreach (var session in sessions) if (session != null) session.Refresh();
            RefreshGeometry(); Repaint(); SceneView.RepaintAll();
        }

        private void Load()
        {
            if (source == null) throw new InvalidOperationException("请先指定模型根对象。");
            if (EditorApplication.isPlaying) throw new InvalidOperationException("请退出播放模式后编辑。");
            Dispose();
            var bindings = MeshBinding.Find(source);
            if (bindings.Count == 0) throw new InvalidOperationException("对象中没有可编辑的网格。");
            try
            {
                foreach (var binding in bindings)
                {
                    var session = MeshSession.Create(binding);
                    sessions.Add(session); ownedMeshes.Add(session.working);
                }
                preview = MeshPreview.Create(source, sessions, previewOffset);
                previewRenderers = preview.GetComponentsInChildren<Renderer>(true);
                SetPreviewVisible(true);
                activeIndex = 0; RefreshGeometry();
                status = $"已载入 {sessions.Count} 个部件。所有操作都作用于右侧预览副本。";
            }
            catch { Dispose(); throw; }
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { status = "已取消，当前网格保留。"; }
            catch (Exception exception) { status = exception.Message; Debug.LogException(exception); }
            finally { EditorUtility.ClearProgressBar(); Repaint(); SceneView.RepaintAll(); }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Stylized Mesh Studio", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("副本编辑 · 顶点塑形 · UV 保护", EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(sessions.Count > 0))
                source = (GameObject)EditorGUILayout.ObjectField("模型根对象", source, typeof(GameObject), true);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(sessions.Count == 0 ? "载入并创建预览" : "重新载入原模型")) Safe(Load);
                if (GUILayout.Button("使用当前选中对象", GUILayout.Width(130)))
                {
                    Dispose(); source = Selection.activeGameObject;
                }
            }
            if (sessions.Count == 0)
            {
                EditorGUILayout.HelpBox("选择包含骨骼和所有部件的模型根对象，然后载入。原始 FBX、Prefab、网格和材质均不会被覆盖。关闭窗口或重载脚本前，请先保存副本。", MessageType.Info);
                EditorGUILayout.HelpBox(status, MessageType.None);
                return;
            }
            if (source == null || preview == null) { Dispose(); return; }
            string[] labels = sessions.Select(s => s.binding.label).ToArray();
            int next = EditorGUILayout.Popup("编辑部件", activeIndex, labels);
            if (next != activeIndex) { EndDrag(); activeIndex = next; expressionIndex = 0; RefreshGeometry(); }
            var active = Active;
            EditorGUILayout.LabelField($"三角形 {MeshOperations.TriangleCount(active.original):N0} → {MeshOperations.TriangleCount(active.working):N0}    顶点 {active.working.vertexCount:N0}");
            EditorGUILayout.LabelField($"表情 {active.working.blendShapeCount}    选中 {Count(active.selected):N0}    保护 {Count(active.protectedVertices):N0}", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("聚焦预览"))
                {
                    if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.Frame(active.previewRenderer.bounds, false);
                }
                if (GUILayout.Button(previewVisible ? "隐藏预览" : "显示预览")) SetPreviewVisible(!previewVisible);
                if (GUILayout.Button("恢复部件初始状态")) Safe(() =>
                {
                    Mesh clone = MeshOperations.Clone(active.original); ownedMeshes.Add(clone);
                    active.Replace(clone, new bool[clone.vertexCount]); RefreshGeometry(); status = "当前部件已还原；可以撤销。";
                });
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("撤销上一步")) Safe(() => HistoryStep(false));
                if (GUILayout.Button("重做上一步")) Safe(() => HistoryStep(true));
            }
            EditorGUILayout.LabelField(previewVisible ? "正在显示编辑副本；原模型仍在原位置。" : "编辑副本已隐藏；原模型仍在原位置。", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            Vector3 offset = EditorGUILayout.Vector3Field("预览位置偏移", previewOffset);
            if (EditorGUI.EndChangeCheck())
            {
                EndDrag(); preview.transform.position += offset - previewOffset; previewOffset = offset; RefreshGeometry();
            }
            int newTab = GUILayout.Toolbar(tab, new[] { "减面", "顶点编辑", "UV", "表面 / 保存" });
            if (tab != newTab) { EndDrag(); tab = newTab; Tools.hidden = previewVisible && sceneEditing && tab == 1; }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (tab == 0) DrawReduction();
            else if (tab == 1) DrawVertices();
            else if (tab == 2) DrawUV();
            else DrawSurface();
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private static int Count(bool[] mask) { int count = 0; foreach (bool value in mask) if (value) count++; return count; }

        private void SetPreviewVisible(bool visible)
        {
            EndDrag();
            previewVisible = visible;
            if (preview != null)
            {
                // Explicit rendering suppression also covers editor renderers with hidden save flags.
                // Keep each renderer's original enabled state intact when showing the preview again.
                if (previewRenderers == null) previewRenderers = preview.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in previewRenderers) if (renderer != null) renderer.forceRenderingOff = !visible;
                preview.SetActive(visible);
            }
            if (!visible)
            {
                cameraNavigationViews.Clear();
                if (boxSelecting || brushSelecting) GUIUtility.hotControl = 0;
                boxSelecting = brushSelecting = false;
                if (selectionUndoGroup >= 0)
                {
                    Undo.CollapseUndoOperations(selectionUndoGroup); Undo.IncrementCurrentGroup(); selectionUndoGroup = -1;
                }
            }
            Tools.hidden = visible && sceneEditing && tab == 1;
            EditorApplication.QueuePlayerLoopUpdate(); Repaint(); SceneView.RepaintAll();
        }

        private void HistoryStep(bool redo)
        {
            EndDrag(); Undo.FlushUndoRecordObjects();
            if (redo) Undo.PerformRedo(); else Undo.PerformUndo();
            status = redo ? "已重做上一步。" : "已撤销上一步。";
        }

        private void DrawReduction()
        {
            EditorGUILayout.HelpBox("保留率越小，减面越强。保护区域可能限制实际减面幅度；UV 接缝保护不能保证图案完全不变形。", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保守预设")) reduction = new ReduceSettings { quality = 0.65f, curvature = true };
                if (GUILayout.Button("头发 / 色块预设")) reduction = new ReduceSettings { quality = 0.2f, borders = false, boneBoundaries = true };
            }
            reduction.quality = EditorGUILayout.Slider("三角形保留率", reduction.quality, 0.01f, 1f);
            EditorGUILayout.LabelField($"当前目标约 {Mathf.RoundToInt(MeshOperations.TriangleCount(Active.working) * reduction.quality):N0} 个三角形");
            reduction.borders = EditorGUILayout.Toggle("保护开放边界", reduction.borders);
            reduction.uvSeams = EditorGUILayout.Toggle("保护 UV 接缝", reduction.uvSeams);
            reduction.uvFoldovers = EditorGUILayout.Toggle("保护 UV 折叠边界", reduction.uvFoldovers);
            reduction.materialBoundaries = EditorGUILayout.Toggle("保护材质交界", reduction.materialBoundaries);
            reduction.movingBlendShapeVertices = EditorGUILayout.Toggle("保护表情移动区域", reduction.movingBlendShapeVertices);
            reduction.boneBoundaries = EditorGUILayout.Toggle("保护骨骼主权重交界", reduction.boneBoundaries);
            reduction.curvature = EditorGUILayout.Toggle("优先保持曲面", reduction.curvature);
            EditorGUILayout.HelpBox("脸部有大量形态键时，“保护表情移动区域”可能保护大部分顶点。关闭后仍保留表情名称与帧，但减面会近似表情形状，需要检查眨眼与口型。", MessageType.None);
            if (GUILayout.Button("对当前部件减面", GUILayout.Height(32))) Safe(() =>
            {
                EndDrag(); var session = Active;
                int before = MeshOperations.TriangleCount(session.working);
                var reduced = MeshOperations.Reduce(session.working, session.protectedVertices, reduction,
                    progress => EditorUtility.DisplayCancelableProgressBar("网格减面", session.binding.label, progress));
                ownedMeshes.Add(reduced);
                var mask = MeshOperations.RemapProtection(session.working, session.protectedVertices, reduced);
                session.Replace(reduced, mask); RefreshGeometry();
                status = $"减面完成：{before:N0} → {MeshOperations.TriangleCount(reduced):N0} 个三角形。请检查 UV 与表情。";
            });
            DrawProtection();
            DrawExpressionPreview();
        }

        private void DrawProtection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("减面保护（与顶点编辑共用选择）", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保护选中顶点")) ChangeMask("保护顶点", () =>
                { for (int i = 0; i < Active.selected.Length; i++) if (Active.selected[i]) Active.protectedVertices[i] = true; });
                if (GUILayout.Button("解除选中保护")) ChangeMask("解除顶点保护", () =>
                { for (int i = 0; i < Active.selected.Length; i++) if (Active.selected[i]) Active.protectedVertices[i] = false; });
                if (GUILayout.Button("清空保护")) ChangeMask("清空保护", () => Array.Clear(Active.protectedVertices, 0, Active.protectedVertices.Length));
            }
        }

        private void DrawVertices()
        {
            sceneEditing = EditorGUILayout.Toggle("启用场景顶点编辑", sceneEditing);
            Tools.hidden = previewVisible && sceneEditing && tab == 1;
            sceneTool = GUILayout.Toolbar(sceneTool, new[] { "选择", "移动", "旋转", "缩放" });
            selectionMode = GUILayout.Toolbar(selectionMode, new[] { "刷选", "框选" });
            brushRadius = EditorGUILayout.Slider("刷选半径（像素）", brushRadius, 3, 100);
            selectBackfaces = EditorGUILayout.Toggle(new GUIContent("包含背面顶点", "开启时可穿透选择；关闭时按法线过滤背面，不做遮挡深度检测。"), selectBackfaces);
            linkCoincident = EditorGUILayout.Toggle("联选同位置顶点", linkCoincident);
            mirrorSelection = EditorGUILayout.Toggle("镜像选择（局部 X）", mirrorSelection);
            showWire = EditorGUILayout.Toggle("显示线框", showWire);
            EditorGUILayout.HelpBox("场景中刷选 / 框选；Shift 添加，Ctrl 或 Cmd 移除。按住右键时 WASD 移动相机，Alt 保留视图导航；松开右键后，W/E/R 切换移动/旋转/缩放，Q 返回选择。橙色为保护点，蓝色为选中点。点与线框会抽样显示，但选择检查全部顶点。", MessageType.None);
            DrawSelectionButtons(); DrawProtection();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("数值塑形（模型局部坐标）", EditorStyles.boldLabel);
            numericMove = EditorGUILayout.Vector3Field("移动距离", numericMove);
            numericRotate = EditorGUILayout.Vector3Field("旋转角度", numericRotate);
            numericScale = EditorGUILayout.Vector3Field("缩放比例", numericScale);
            transformExpressions = EditorGUILayout.Toggle("同步变换表情位移", transformExpressions);
            shapeNormals = EditorGUILayout.Toggle("更新表情法线（较慢）", shapeNormals);
            if (GUILayout.Button("应用到选中顶点")) Edit("顶点变换", () =>
            {
                Vector3 center = LocalCenter();
                var matrix = Matrix4x4.TRS(center + numericMove, Quaternion.Euler(numericRotate), numericScale) * Matrix4x4.Translate(-center);
                MeshOperations.TransformVertices(Active.working, Active.selected, matrix, transformExpressions);
            });
            EditorGUILayout.Space();
            flattenAxis = EditorGUILayout.Popup("压平轴", flattenAxis, new[] { "X", "Y", "Z" });
            if (GUILayout.Button("沿该轴压平到选择中心")) Edit("局部压平", () =>
            {
                Vector3 center = LocalCenter(), scale = Vector3.one; scale[flattenAxis] = 0;
                MeshOperations.TransformVertices(Active.working, Active.selected,
                    Matrix4x4.Translate(center) * Matrix4x4.Scale(scale) * Matrix4x4.Translate(-center), transformExpressions);
            });
            gridStep = EditorGUILayout.FloatField("吸附间距（局部单位）", gridStep);
            if (GUILayout.Button("选中顶点吸附网格")) Edit("顶点吸附", () => MeshOperations.Snap(Active.working, Active.selected, gridStep));
            inflateAmount = EditorGUILayout.FloatField("沿法线偏移", inflateAmount);
            if (GUILayout.Button("膨胀 / 收缩选中区域")) Edit("沿法线偏移", () => MeshOperations.Inflate(Active.working, Active.selected, inflateAmount));
            smoothStrength = EditorGUILayout.Slider("平滑强度", smoothStrength, 0, 1);
            smoothIterations = EditorGUILayout.IntSlider("平滑次数", smoothIterations, 1, 20);
            preserveSmoothBorders = EditorGUILayout.Toggle("平滑时固定开放边界", preserveSmoothBorders);
            if (GUILayout.Button("平滑选中区域")) Edit("局部平滑", () => MeshOperations.Smooth(Active.working, Active.selected, smoothStrength, smoothIterations, preserveSmoothBorders));
            DrawExpressionPreview();
        }

        private void DrawSelectionButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全选")) ChangeMask("选择全部顶点", () => { for (int i = 0; i < Active.selected.Length; i++) Active.selected[i] = true; });
                if (GUILayout.Button("清空")) ChangeMask("清空选择", () => Array.Clear(Active.selected, 0, Active.selected.Length));
                if (GUILayout.Button("反选")) ChangeMask("反转选择", () => { for (int i = 0; i < Active.selected.Length; i++) Active.selected[i] = !Active.selected[i]; });
                if (GUILayout.Button("扩选一圈")) ChangeMask("扩展选择", () =>
                {
                    var adjacent = MeshOperations.Neighbors(Active.working); var selected = (bool[])Active.selected.Clone();
                    for (int i = 0; i < selected.Length; i++) if (selected[i]) foreach (int j in adjacent[i]) Active.selected[j] = true;
                });
            }
        }

        private void ChangeMask(string label, Action edit)
        {
            EndDrag(); Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(label);
            Undo.RegisterCompleteObjectUndo(Active, label); edit(); EditorUtility.SetDirty(Active);
            Undo.CollapseUndoOperations(group); Undo.IncrementCurrentGroup(); Repaint(); SceneView.RepaintAll();
        }

        private Vector3 LocalCenter()
        {
            var vertices = Active.working.vertices; Vector3 sum = Vector3.zero; int count = 0;
            for (int i = 0; i < vertices.Length; i++) if (Active.selected[i]) { sum += vertices[i]; count++; }
            if (count == 0) throw new InvalidOperationException("请先选择顶点。");
            return sum / count;
        }

        private void Edit(string label, Action action, bool normals = true)
        {
            Safe(() =>
            {
                EndDrag();
                if (Count(Active.selected) == 0 && normals) throw new InvalidOperationException("请先选择顶点。");
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(label);
                // Mesh undo does not reliably restore native buffers after blend-shape reconstruction.
                // Keep the prior mesh intact and undo the session's mesh reference instead.
                Undo.RegisterCompleteObjectUndo(Active, label);
                Mesh edited = MeshOperations.Clone(Active.working); ownedMeshes.Add(edited); Active.working = edited;
                try
                {
                action();
                if (normals) MeshOperations.RecalculateNormals(Active.working, shapeNormals);
                Active.changed = true; EditorUtility.SetDirty(Active.working); Active.Refresh(); RefreshGeometry();
                Undo.CollapseUndoOperations(group); status = label + "完成，可以撤销。";
                Undo.IncrementCurrentGroup();
                }
                catch
                {
                    Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(group);
                    Active.Refresh(); RefreshGeometry(); throw;
                }
            });
        }

        private void DrawUV()
        {
            uvChannel = EditorGUILayout.Popup("UV 通道", uvChannel, new[] { "UV0（主贴图）", "UV1", "UV2", "UV3", "UV4", "UV5", "UV6", "UV7" });
            EditorGUILayout.HelpBox("保留并编辑现有 UV。点击 UV 顶点选择，双击选择相连 UV 区域；Shift 添加、Ctrl 移除。重新展开 UV 会改变与原贴图的对应，本工具不会自动重展开主 UV。", MessageType.Info);
            DrawUVCanvas();
            DrawSelectionButtons();
            uvMove = EditorGUILayout.Vector2Field("UV 偏移", uvMove);
            uvScale = EditorGUILayout.Vector2Field("UV 缩放", uvScale);
            uvRotate = EditorGUILayout.FloatField("UV 旋转角度", uvRotate);
            if (GUILayout.Button("调整选中 UV")) Edit("UV 变换", () => MeshOperations.TransformUV(Active.working, uvChannel, Active.selected, uvMove, uvScale, uvRotate), false);
            smoothStrength = EditorGUILayout.Slider("UV 松弛强度", smoothStrength, 0, 1);
            smoothIterations = EditorGUILayout.IntSlider("UV 松弛次数", smoothIterations, 1, 20);
            EditorGUILayout.HelpBox("UV 松弛固定拓扑开放边界，仍可能改变图案形状。材质或法线拆点也可能被视为边界。", MessageType.None);
            if (GUILayout.Button("松弛选中 UV（固定边界）")) Edit("UV 松弛", () => MeshOperations.RelaxUV(Active.working, uvChannel, Active.selected, smoothStrength, smoothIterations), false);
            EditorGUILayout.HelpBox(MeshOperations.UVReport(Active.working, uvChannel), MessageType.None);
        }

        private void DrawUVCanvas()
        {
            Rect rect = GUILayoutUtility.GetRect(200, 300, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.13f, 0.15f));
            var uv = new List<Vector4>(); Active.working.GetUVs(uvChannel, uv);
            if (uv.Count == 0) { GUI.Label(rect, "无 UV 数据"); return; }
            Vector2 min = Vector2.zero, max = Vector2.one;
            foreach (var value in uv) { min = Vector2.Min(min, new Vector2(value.x, value.y)); max = Vector2.Max(max, new Vector2(value.x, value.y)); }
            float range = Mathf.Max(max.x - min.x, max.y - min.y, 0.01f);
            float size = Mathf.Min(rect.width - 20, rect.height - 20);
            Vector2 origin = rect.center - new Vector2(size, -size) * 0.5f;
            Func<int, Vector2> point = i => origin + new Vector2(uv[i].x - min.x, -(uv[i].y - min.y)) * (size / range);
            if (Event.current.type == EventType.Repaint)
            {
                Handles.BeginGUI(); Handles.color = new Color(0.45f, 0.48f, 0.52f, 0.6f);
                int[] tris = Active.working.triangles; int stride = Math.Max(3, (tris.Length / 6000 / 3 + 1) * 3);
                for (int i = 0; i < tris.Length; i += stride)
                    Handles.DrawAAPolyLine(1, point(tris[i]), point(tris[i + 1]), point(tris[i + 2]), point(tris[i]));
                Handles.EndGUI();
                for (int i = 0; i < uv.Count; i++) if (Active.selected[i])
                { var p = point(i); EditorGUI.DrawRect(new Rect(p.x - 2, p.y - 2, 4, 4), new Color(0.2f, 0.75f, 1f)); }
            }
            var evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                float best = 64; int nearest = -1;
                for (int i = 0; i < uv.Count; i++) { float d = (point(i) - evt.mousePosition).sqrMagnitude; if (d < best) { best = d; nearest = i; } }
                if (nearest >= 0)
                {
                    bool subtract = evt.control || evt.command;
                    ChangeMask("选择 UV", () =>
                    {
                        if (!evt.shift && !subtract) Array.Clear(Active.selected, 0, Active.selected.Length);
                        var indices = new HashSet<int> { nearest };
                        if (evt.clickCount >= 2)
                        {
                            var adjacent = MeshOperations.Neighbors(Active.working); var queue = new Queue<int>(); queue.Enqueue(nearest);
                            while (queue.Count > 0) foreach (int neighbor in adjacent[queue.Dequeue()]) if (indices.Add(neighbor)) queue.Enqueue(neighbor);
                        }
                        foreach (int index in indices) Active.selected[index] = !subtract;
                    });
                }
                evt.Use();
            }
        }

        private void DrawSurface()
        {
            EditorGUILayout.LabelField("表面处理", EditorStyles.boldLabel);
            shapeNormals = EditorGUILayout.Toggle("同时更新表情法线", shapeNormals);
            if (GUILayout.Button("重建平滑法线")) Edit("重建法线", () => MeshOperations.RecalculateNormals(Active.working, shapeNormals), false);
            EditorGUILayout.HelpBox("平面着色拆开每个三角形的顶点，三角形数量不变，顶点数量通常增加。保留 UV、全部蒙皮权重及表情帧。", MessageType.None);
            if (GUILayout.Button("整个部件转为平面着色")) Safe(() =>
            {
                EndDrag(); int[] map;
                var result = MeshOperations.FlatShade(Active.working, out map); ownedMeshes.Add(result);
                var protection = new bool[map.Length];
                for (int i = 0; i < map.Length; i++) protection[i] = Active.protectedVertices[map[i]];
                Active.Replace(result, protection); RefreshGeometry(); status = "已转为平面着色，三角形数不变。";
            });
            if (GUILayout.Button("清理零面积三角形")) Edit("清理退化面", () =>
            { int count = MeshOperations.RemoveDegenerateTriangles(Active.working); status = $"已清理 {count} 个退化三角形。"; }, false);
            DrawExpressionPreview();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("另存完整模型副本", EditorStyles.boldLabel);
            outputFolder = EditorGUILayout.TextField("输出目录", outputFolder);
            outputColorBlocks = EditorGUILayout.Toggle("生成色块材质副本", outputColorBlocks);
            if (outputColorBlocks) EditorGUILayout.HelpBox("可选色块 Shader 面向 Built-in 渲染管线 / VRChat PC，使用 Alpha Clip；不复刻原材质的透明、特效或移动端兼容性。默认保留原材质。", MessageType.Warning);
            if (GUILayout.Button("保存网格资产 + 模型 Prefab", GUILayout.Height(34))) Safe(() =>
            {
                EndDrag();
                string path = MeshExporter.Export(source, sessions, outputFolder, outputColorBlocks);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                EditorGUIUtility.PingObject(asset); Selection.activeObject = asset;
                status = "已保存：" + path + "。可将新 Prefab 拖入场景。原模型保留。";
            });
            EditorGUILayout.HelpBox("保存副本保留完整骨骼、原有组件和内部引用，并清空副本的 VRChat Blueprint ID。关闭工具会释放未保存的预览与撤销历史。", MessageType.Info);
            if (GUILayout.Button("释放旧撤销历史（保留当前结果）"))
            {
                EndDrag();
                var retained = new HashSet<Mesh>(sessions.Select(s => s.working));
                foreach (var session in sessions) Undo.ClearUndo(session);
                foreach (var mesh in ownedMeshes.Distinct().ToArray())
                {
                    if (mesh == null) continue; Undo.ClearUndo(mesh);
                    if (!retained.Contains(mesh)) DestroyImmediate(mesh);
                }
                ownedMeshes.Clear(); ownedMeshes.AddRange(retained);
                status = "当前结果保留，旧撤销历史已释放。";
            }
        }

        private void DrawExpressionPreview()
        {
            var skinned = Active.previewRenderer as SkinnedMeshRenderer;
            if (skinned == null || Active.working.blendShapeCount == 0) return;
            EditorGUILayout.Space(); EditorGUILayout.LabelField("表情检查（仅预览）", EditorStyles.boldLabel);
            if (expressionNamesMesh != Active.working)
            {
                expressionNamesMesh = Active.working;
                expressionNames = new string[Active.working.blendShapeCount];
                for (int i = 0; i < expressionNames.Length; i++) expressionNames[i] = Active.working.GetBlendShapeName(i);
            }
            string[] names = expressionNames;
            expressionIndex = Mathf.Clamp(expressionIndex, 0, names.Length - 1);
            expressionIndex = EditorGUILayout.Popup("表情", expressionIndex, names);
            EditorGUI.BeginChangeCheck();
            float value = EditorGUILayout.Slider("权重", skinned.GetBlendShapeWeight(expressionIndex), 0, 100);
            if (EditorGUI.EndChangeCheck()) SetExpressionWeight(expressionIndex, value);
            if (GUILayout.Button("预览表情全部归零"))
            {
                EndDrag();
                for (int i = 0; i < names.Length; i++) skinned.SetBlendShapeWeight(i, 0);
                InvalidatePose();
            }
        }

        private void SetExpressionWeight(int index, float weight)
        {
            var skinned = Active.previewRenderer as SkinnedMeshRenderer;
            if (skinned == null) return;
            EndDrag(); skinned.SetBlendShapeWeight(index, weight); InvalidatePose();
        }

        private void InvalidatePose()
        {
            // Unity renders blend shapes directly. Bake only when vertex tools need the CPU pose.
            // Multiple slider events are coalesced until the scene next needs those positions.
            poseDirty = true;
            EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); Repaint();
        }

        private void RefreshPose()
        {
            var active = Active; if (active == null || active.previewRenderer == null) return;
            var skinned = active.previewRenderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                if (posedMesh == null) posedMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skinned.BakeMesh(posedMesh, true);
                displayedVertices = posedMesh.vertices; displayedNormals = posedMesh.normals;
            }
            else { displayedVertices = active.working.vertices; displayedNormals = active.working.normals; }
            poseDirty = false;
        }

        private void RefreshGeometry()
        {
            var active = Active; if (active == null || active.previewRenderer == null) return;
            active.Refresh();
            RefreshPose();
            wireTriangles = active.working.triangles;
            handleRotation = Quaternion.identity; handleScale = Vector3.one;
        }

        private Vector3 WorldVertex(int index)
        {
            if (poseDirty) RefreshPose();
            return Active.previewRenderer.transform.TransformPoint(displayedVertices[index]);
        }

        private void SceneGUI(SceneView view)
        {
            if (Active == null || preview == null || !previewVisible || !preview.activeInHierarchy || tab != 1 || !sceneEditing || EditorApplication.isPlaying) return;
            Tools.hidden = true;
            if (displayedVertices == null || displayedVertices.Length != Active.working.vertexCount) RefreshGeometry();
            else if (poseDirty) RefreshPose();
            var evt = Event.current;
            // The camera can consume mouse input before duringSceneGui runs. Its native view-tool
            // override still tells us to leave navigation keys alone, even without a hot control.
            bool navigationActive = Tools.viewToolActive && (Tools.current != Tool.View || GUIUtility.hotControl != 0);
            bool navigating = HandleSceneInput(evt, view.GetInstanceID(), navigationActive);
            if (evt.type == EventType.Repaint) DrawScenePoints();
            if (!navigating)
            {
                if (sceneTool == 0) SceneSelection(view, evt);
                else DrawTransformHandle(evt);
            }
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12, 12, 320, 60), EditorStyles.helpBox);
            GUILayout.Label("网格改模 · " + Active.binding.renderer.name, EditorStyles.boldLabel);
            GUILayout.Label($"选中 {Count(Active.selected):N0} 个顶点 · 右键导航 / 松开后 Q/W/E/R");
            GUILayout.EndArea();
            if (boxSelecting)
            {
                Rect rect = SelectionRect(boxStart, boxEnd);
                EditorGUI.DrawRect(rect, new Color(0.2f, 0.65f, 1f, 0.15f)); GUI.Box(rect, GUIContent.none);
            }
            if (sceneTool == 0 && selectionMode == 0)
            {
                Handles.color = Color.cyan;
                Handles.DrawWireDisc(new Vector3(evt.mousePosition.x, evt.mousePosition.y, 0), Vector3.forward, brushRadius);
            }
            Handles.EndGUI();
            if (evt.type == EventType.MouseMove) view.Repaint();
        }

        private bool HandleSceneInput(Event evt, int viewId, bool navigationActive)
        {
            // Keyboard events do not carry the held mouse button. Track the raw mouse events,
            // including events already consumed by Unity's camera controller, separately per view.
            if ((evt.rawType == EventType.MouseDown || evt.rawType == EventType.MouseDrag) && evt.button == 1)
                cameraNavigationViews.Add(viewId);
            if ((evt.rawType == EventType.MouseUp && evt.button == 1) || evt.rawType == EventType.MouseLeaveWindow || evt.rawType == EventType.Ignore)
                cameraNavigationViews.Remove(viewId);
            bool navigating = navigationActive || cameraNavigationViews.Contains(viewId) || evt.alt;
            if (navigating || evt.type != EventType.KeyDown || evt.shift || evt.control || evt.command ||
                EditorGUIUtility.editingTextField || GUIUtility.hotControl != 0) return navigating;
            int next = evt.keyCode == KeyCode.Q ? 0 : evt.keyCode == KeyCode.W ? 1 : evt.keyCode == KeyCode.E ? 2 : evt.keyCode == KeyCode.R ? 3 : -1;
            if (next >= 0) { EndDrag(); sceneTool = next; evt.Use(); Repaint(); }
            if (evt.keyCode == KeyCode.Escape) { EndDrag(); sceneTool = 0; boxSelecting = false; brushSelecting = false; evt.Use(); }
            return navigating;
        }

        private void DrawScenePoints()
        {
            if (showWire && wireTriangles != null)
            {
                Handles.color = new Color(0.35f, 0.65f, 0.85f, 0.45f);
                int stride = Math.Max(3, (wireTriangles.Length / 4500 / 3 + 1) * 3);
                for (int i = 0; i < wireTriangles.Length; i += stride)
                    Handles.DrawAAPolyLine(1, WorldVertex(wireTriangles[i]), WorldVertex(wireTriangles[i + 1]), WorldVertex(wireTriangles[i + 2]), WorldVertex(wireTriangles[i]));
            }
            int sampleStride = Math.Max(1, displayedVertices.Length / 4000);
            for (int i = 0; i < displayedVertices.Length; i++)
            {
                bool selected = Active.selected[i], locked = Active.protectedVertices[i];
                if (!selected && !locked && i % sampleStride != 0) continue;
                if ((selected || locked) && i % Math.Max(1, displayedVertices.Length / 16000) != 0) continue;
                Vector3 point = WorldVertex(i);
                Handles.color = selected ? Color.cyan : locked ? new Color(1f, 0.55f, 0.15f) : new Color(0.75f, 0.75f, 0.75f, 0.55f);
                Handles.DotHandleCap(0, point, Quaternion.identity, HandleUtility.GetHandleSize(point) * (selected ? 0.025f : 0.014f), EventType.Repaint);
            }
        }

        private static Rect SelectionRect(Vector2 a, Vector2 b)
        { return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)); }

        private void SceneSelection(SceneView view, Event evt)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            if (evt.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            if (evt.alt || evt.button != 0) return;
            if (evt.type == EventType.MouseDown)
            {
                GUIUtility.hotControl = control;
                Undo.IncrementCurrentGroup(); selectionUndoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(Active, "选择顶点");
                subtractSelection = evt.control || evt.command;
                if (!evt.shift && !subtractSelection) Array.Clear(Active.selected, 0, Active.selected.Length);
                if (selectionMode == 1) { boxSelecting = true; boxStart = boxEnd = evt.mousePosition; }
                else { brushSelecting = true; SelectScreen(view, evt.mousePosition, null); }
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            {
                if (boxSelecting) boxEnd = evt.mousePosition;
                if (brushSelecting) SelectScreen(view, evt.mousePosition, null);
                evt.Use();
            }
            else if (evt.type == EventType.MouseUp && GUIUtility.hotControl == control)
            {
                if (boxSelecting) SelectScreen(view, evt.mousePosition, SelectionRect(boxStart, evt.mousePosition));
                boxSelecting = false; brushSelecting = false; GUIUtility.hotControl = 0;
                if (selectionUndoGroup >= 0) Undo.CollapseUndoOperations(selectionUndoGroup);
                Undo.IncrementCurrentGroup();
                selectionUndoGroup = -1; EditorUtility.SetDirty(Active); evt.Use();
            }
            Repaint();
        }

        private void SelectScreen(SceneView view, Vector2 mouse, Rect? rectangle)
        {
            var hit = new bool[Active.selected.Length];
            for (int i = 0; i < hit.Length; i++)
            {
                var world = WorldVertex(i); var screen = view.camera.WorldToViewportPoint(world);
                if (screen.z <= 0) continue;
                if (!selectBackfaces && displayedNormals.Length == hit.Length)
                {
                    Vector3 normal = Active.previewRenderer.transform.localToWorldMatrix.inverse.transpose.MultiplyVector(displayedNormals[i]);
                    if (Vector3.Dot(normal, view.camera.transform.position - world) <= 0) continue;
                }
                Vector2 point = HandleUtility.WorldToGUIPoint(world);
                if (rectangle.HasValue ? rectangle.Value.Contains(point) : (point - mouse).sqrMagnitude <= brushRadius * brushRadius) hit[i] = true;
            }
            if (linkCoincident || mirrorSelection) MeshOperations.ExpandCoincident(Active.working.vertices, hit, mirrorSelection);
            for (int i = 0; i < hit.Length; i++) if (hit[i]) Active.selected[i] = !subtractSelection;
        }

        private Vector3 SelectionWorldCenter()
        {
            if (poseDirty) RefreshPose();
            Vector3 sum = Vector3.zero; int count = 0;
            for (int i = 0; i < displayedVertices.Length; i++) if (Active.selected[i]) { sum += WorldVertex(i); count++; }
            return count == 0 ? Active.previewRenderer.bounds.center : sum / count;
        }

        private void StartDrag()
        {
            Undo.IncrementCurrentGroup(); dragUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("场景顶点塑形");
            Undo.RegisterCompleteObjectUndo(Active, "场景顶点塑形");
            Mesh edited = MeshOperations.Clone(Active.working); ownedMeshes.Add(edited); Active.working = edited;
            Active.Refresh();
            dragOriginal = Active.working.vertices;
            dragWorld = new Vector3[dragOriginal.Length];
            for (int i = 0; i < dragWorld.Length; i++) dragWorld[i] = WorldVertex(i);
            dragPivot = SelectionWorldCenter();
            dragSkin = SkinMatrices();
        }

        private Matrix4x4[] SkinMatrices()
        {
            var matrices = new Matrix4x4[Active.working.vertexCount];
            for (int i = 0; i < matrices.Length; i++) matrices[i] = Matrix4x4.identity;
            var renderer = Active.previewRenderer as SkinnedMeshRenderer;
            if (renderer == null) return matrices;
            var bindposes = Active.working.bindposes; var bones = renderer.bones;
            var bonesMatrices = new Matrix4x4[bindposes.Length];
            for (int i = 0; i < bonesMatrices.Length; i++)
                bonesMatrices[i] = i < bones.Length && bones[i] != null ? renderer.transform.worldToLocalMatrix * bones[i].localToWorldMatrix * bindposes[i] : Matrix4x4.identity;
            using (var counts = Active.working.GetBonesPerVertex())
            using (var weights = Active.working.GetAllBoneWeights())
            {
                if (counts.Length != matrices.Length || weights.Length == 0) return matrices;
                int cursor = 0;
                for (int i = 0; i < matrices.Length; i++)
                {
                    var matrix = new Matrix4x4(); float sum = 0;
                    for (int j = 0; j < counts[i]; j++)
                    {
                        var influence = weights[cursor++];
                        if (influence.boneIndex < 0 || influence.boneIndex >= bonesMatrices.Length) continue;
                        for (int k = 0; k < 16; k++) matrix[k] += bonesMatrices[influence.boneIndex][k] * influence.weight;
                        sum += influence.weight;
                    }
                    if (sum > 0 && Mathf.Abs(matrix.determinant) > 1e-10f) matrices[i] = matrix;
                }
            }
            return matrices;
        }

        private void DrawTransformHandle(Event evt)
        {
            if (Count(Active.selected) == 0) return;
            if (dragOriginal == null) handlePivot = SelectionWorldCenter();
            EditorGUI.BeginChangeCheck();
            Vector3 pivot = handlePivot, scale = handleScale; Quaternion rotation = handleRotation;
            if (sceneTool == 1) pivot = Handles.PositionHandle(handlePivot, Quaternion.identity);
            else if (sceneTool == 2) rotation = Handles.RotationHandle(handleRotation, handlePivot);
            else scale = Handles.ScaleHandle(handleScale, handlePivot, Quaternion.identity, HandleUtility.GetHandleSize(handlePivot));
            if (EditorGUI.EndChangeCheck())
            {
                ApplyDrag(pivot, rotation, scale);
            }
            // Unity handles consume MouseUp, so rawType must be checked as well.
            if (evt.rawType == EventType.MouseUp && dragOriginal != null) Safe(EndDrag);
        }

        private void ApplyDrag(Vector3 pivot, Quaternion rotation, Vector3 scale)
        {
                if (dragOriginal == null) StartDrag();
                handlePivot = pivot; handleScale = scale; handleRotation = rotation;
                dragWorldTransform = Matrix4x4.TRS(handlePivot, handleRotation, handleScale) * Matrix4x4.Translate(-dragPivot);
                var vertices = (Vector3[])dragOriginal.Clone();
                Transform transform = Active.previewRenderer.transform;
                for (int i = 0; i < vertices.Length; i++) if (Active.selected[i])
                {
                    Vector3 worldDelta = dragWorldTransform.MultiplyPoint3x4(dragWorld[i]) - dragWorld[i];
                    vertices[i] += dragSkin[i].inverse.MultiplyVector(transform.InverseTransformVector(worldDelta));
                }
                var bounds = Active.working.bounds; Active.working.vertices = vertices;
                MeshOperations.KeepBounds(Active.working, bounds); Active.Refresh();
                var skinned = Active.previewRenderer as SkinnedMeshRenderer;
                if (skinned != null) { skinned.BakeMesh(posedMesh, true); displayedVertices = posedMesh.vertices; }
                else displayedVertices = vertices;
                Active.changed = true; Repaint(); SceneView.RepaintAll();
        }

        private void EndDrag()
        {
            if (dragOriginal == null || Active == null) return;
            try
            {
                if (transformExpressions && sceneTool != 1)
                {
                    Transform transform = Active.previewRenderer.transform;
                    Matrix4x4 local = transform.worldToLocalMatrix * dragWorldTransform * transform.localToWorldMatrix;
                    var skinned = Active.previewRenderer as SkinnedMeshRenderer;
                    Vector3[] desiredPosedVertices = skinned != null ? (Vector3[])displayedVertices.Clone() : null;
                    MeshOperations.TransformExpressionDeltas(Active.working, Active.selected,
                        (i, delta) => dragSkin[i].inverse.MultiplyVector(local.MultiplyVector(dragSkin[i].MultiplyVector(delta))));
                    // A live expression contributed to the dragged positions. After transforming its
                    // displacement, compensate the rest positions so the displayed pose is not transformed twice.
                    if (skinned != null)
                    {
                        skinned.BakeMesh(posedMesh, true);
                        var after = posedMesh.vertices; var vertices = Active.working.vertices;
                        for (int i = 0; i < vertices.Length; i++) if (Active.selected[i])
                            vertices[i] += dragSkin[i].inverse.MultiplyVector(desiredPosedVertices[i] - after[i]);
                        var bounds = Active.working.bounds; Active.working.vertices = vertices;
                        MeshOperations.KeepBounds(Active.working, bounds);
                    }
                }
                MeshOperations.RecalculateNormals(Active.working, shapeNormals);
                EditorUtility.SetDirty(Active.working); Active.Refresh();
                if (dragUndoGroup >= 0) Undo.CollapseUndoOperations(dragUndoGroup);
                Undo.IncrementCurrentGroup();
                status = "顶点已更新，可以撤销；请检查表情与关节变形。";
            }
            finally
            {
                dragOriginal = null; dragWorld = null; dragSkin = null; dragUndoGroup = -1;
                handleRotation = Quaternion.identity; handleScale = Vector3.one;
                RefreshGeometry();
            }
        }
    }
}
