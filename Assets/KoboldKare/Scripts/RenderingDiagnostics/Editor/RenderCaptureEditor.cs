using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KoboldKare.RenderingDiagnostics;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Fixed, local diagnostic commands. No arbitrary code evaluation or network listener.
[InitializeOnLoad]
public static class RenderCaptureEditor {
    const string Root = "Tools/RenderingDiagnostics";
    const string RequestPath = "Temp/RenderCapture-request.json";
    const string ResponsePath = "Temp/RenderCapture-response.json";
    [Serializable] public sealed class Request {
        public string command, config, output, pose;
        public float smallestOccluder = -1, smallestHole = -1, backfaceThreshold = -1;
    }
    [Serializable] sealed class Response { public string utc, command, result, error; }
    [Serializable] sealed class SavedAudit {
        public string scene, sceneSha256, occlusionGuid, occlusionPath, occlusionSha256, bakeSettings;
        public bool dirtyInEditor;
        public List<CaptureTransform> transforms = new List<CaptureTransform>();
        public List<CaptureRenderer> renderers = new List<CaptureRenderer>();
        public List<MeshAudit> meshes = new List<MeshAudit>();
    }
    [Serializable] sealed class MeshAudit {
        public string rendererKey, mesh, asset, limitation;
        public Bounds localBounds, worldBounds;
        public int vertices, subMeshes, triangles, boundaryEdges, nonManifoldEdges, degenerateTriangles;
        public bool readable, negativeDeterminant;
    }
    static double nextPoll;
    static bool busy;
    static string bakeBackup;
    static bool bakeError, bakeCancelled;
    static double bakeStarted;

    static RenderCaptureEditor() { EditorApplication.update += Poll; EditorApplication.playModeStateChanged += OnPlayMode; }

    static void Poll() {
        if (busy || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + .5;
        if (!File.Exists(RequestPath)) return;
        var request = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath));
        File.Delete(RequestPath);
        busy = true;
        var response = new Response { command = request.command, utc = DateTime.UtcNow.ToString("O") };
        try {
            if (bakeBackup != null && request.command != "status" && request.command != "cancel-bake")
                throw new InvalidOperationException("A bake is running; wait or cancel-bake before another action.");
            switch (request.command) {
                case "status":
                    response.result = "playing=" + EditorApplication.isPlaying + ";capture=" + (RenderCapture.Active ? RenderCapture.Active.Status : "none") + ";dirty=" + SceneManager.GetActiveScene().isDirty + ";baking=" + StaticOcclusionCulling.isRunning;
                    break;
                case "audit": AuditSavedScene(request.output); response.result = "saved scene audited"; break;
                case "build": BuildDevelopment(request.output); response.result = "development build complete"; break;
                case "play":
                    if (EditorApplication.isPlaying) throw new InvalidOperationException("Already in Play Mode.");
                    SessionState.SetString("RenderCapture.PendingConfig", request.config);
                    EditorApplication.playModeStateChanged -= OnPlayMode;
                    EditorApplication.playModeStateChanged += OnPlayMode;
                    EditorApplication.isPlaying = true; response.result = "entering Play Mode"; break;
                case "capture":
                    if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play Mode is required.");
                    RenderCapture.Begin(JsonUtility.FromJson<RenderCaptureConfig>(File.ReadAllText(request.config))); response.result = "capture started"; break;
                case "stop":
                    if (RenderCapture.Active) UnityEngine.Object.Destroy(RenderCapture.Active.gameObject);
                    EditorApplication.isPlaying = false; response.result = "stopped"; break;
                case "frame-api": ExportFrameApi(request.output); response.result = "Frame Debugger API described"; break;
                case "fd-enable": FrameDebuggerCapture.Enable(); response.result = "enabled; wait for a frame before exporting"; break;
                case "fd-export": FrameDebuggerCapture.Save(request.output); response.result = "export started; wait for output .status.txt"; break;
                case "fd-disable": FrameDebuggerCapture.Disable(); response.result = "Frame Debugger disabled"; break;
                case "hold-pose": RenderCapture.Active.SetHeldPose(request.pose == "B"); response.result = "held pose changed"; break;
                case "bake-experiment": BakeExperiment(request); response.result = "background bake started; monitor backup/status.txt"; break;
                case "cancel-bake": bakeCancelled = true; StaticOcclusionCulling.Cancel(); response.result = "cancelling; original bytes will be restored"; break;
                case "restore-bake": RestoreBake(request.output); response.result = "original scene and bake restored"; break;
                case "reload-scene": RequireSavedMainMap(); EditorSceneManager.OpenScene(BakeFiles[0], OpenSceneMode.Single); response.result = "saved MainMap reloaded"; break;
                case "open-mainmap":
                    if (EditorApplication.isPlaying || Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty)) throw new InvalidOperationException("Leave Play Mode and preserve unsaved scenes before opening MainMap.");
                    EditorSceneManager.OpenScene(BakeFiles[0], OpenSceneMode.Single); response.result = "MainMap opened"; break;
                default: throw new ArgumentException("Unknown diagnostic command: " + request.command);
            }
        } catch (Exception e) { response.error = e.ToString(); Debug.LogException(e); }
        finally { RenderCaptureData.WriteJson(ResponsePath, response); busy = false; }
    }

    static void OnPlayMode(PlayModeStateChange change) {
        if (change != PlayModeStateChange.EnteredPlayMode) return;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        string config = SessionState.GetString("RenderCapture.PendingConfig", "");
        SessionState.EraseString("RenderCapture.PendingConfig");
        if (!string.IsNullOrEmpty(config)) RenderCapture.Begin(JsonUtility.FromJson<RenderCaptureConfig>(File.ReadAllText(config)));
    }

    [MenuItem("Tools/Rendering diagnostics/Audit saved MainMap")]
    public static void AuditMenu() => AuditSavedScene(Root + "/Reports/saved-scene.json");

    public static void AuditSavedScene(string output) {
        const string path = "Assets/KoboldKare/Scenes/MainMap.unity";
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Audit the saved scene outside Play Mode.");
        var preview = SceneManager.GetSceneByPath(path);
        bool opened = !preview.IsValid() || !preview.isLoaded;
        if (!opened && preview.isDirty) throw new InvalidOperationException("MainMap has unsaved changes. The tool will not save or discard them.");
        if (opened) preview = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        try {
            var text = File.ReadAllText(path);
            var guid = System.Text.RegularExpressions.Regex.Match(text, @"m_OcclusionCullingData:.*?guid: ([a-f0-9]+)").Groups[1].Value;
            string occlusionPath = AssetDatabase.GUIDToAssetPath(guid);
            var audit = new SavedAudit {
                scene = path, sceneSha256 = Sha(path), occlusionGuid = guid, occlusionPath = occlusionPath,
                occlusionSha256 = File.Exists(occlusionPath) ? Sha(occlusionPath) : "missing",
                bakeSettings = System.Text.RegularExpressions.Regex.Match(text, @"m_OcclusionBakeSettings:[\s\S]*?m_SceneGUID:").Value,
                dirtyInEditor = SceneManager.GetSceneByPath(path).isDirty
            };
            foreach (var root in preview.GetRootGameObjects()) {
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
                    if (t.name == "FarmRegion" || t.name == "PlayerHouse" || t.name.Equals("stone", StringComparison.OrdinalIgnoreCase)) {
                        for (var ancestor = t; ancestor; ancestor = ancestor.parent) {
                            var record = RenderCaptureData.TransformOf(ancestor);
                            if (!audit.transforms.Any(x => x.key == record.key)) audit.transforms.Add(record);
                        }
                    }
                }
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) {
                    audit.renderers.Add(RenderCaptureData.RendererOf(r));
                    if (r.name.Equals("stone", StringComparison.OrdinalIgnoreCase)) audit.meshes.Add(AuditMesh(r));
                }
            }
            RenderCaptureData.WriteJson(output, audit);
        } finally { if (opened) EditorSceneManager.CloseScene(preview, true); }
    }

    static MeshAudit AuditMesh(Renderer renderer) {
        var filter = renderer.GetComponent<MeshFilter>();
        var mesh = filter ? filter.sharedMesh : null;
        var audit = new MeshAudit {
            rendererKey = RenderCaptureData.Key(renderer), mesh = mesh ? mesh.name : "", asset = AssetDatabase.GetAssetPath(mesh),
            localBounds = mesh ? mesh.bounds : default, worldBounds = renderer.bounds,
            readable = mesh && mesh.isReadable, negativeDeterminant = renderer.localToWorldMatrix.determinant < 0
        };
        if (!mesh) { audit.limitation = "No MeshFilter mesh"; return audit; }
        audit.vertices = mesh.vertexCount; audit.subMeshes = mesh.subMeshCount;
        // Editor permits reading imported non-readable meshes outside Play Mode. Catch platforms that don't.
        try {
            var vertices = mesh.vertices;
            var indices = mesh.triangles;
            audit.triangles = indices.Length / 3;
            var edges = new Dictionary<ulong, int>();
            // Weld exactly coincident vertices to avoid calling UV/normal seams geometric holes.
            var welded = new Dictionary<Vector3, int>();
            int[] canonical = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) {
                if (!welded.TryGetValue(vertices[i], out int id)) { id = welded.Count; welded.Add(vertices[i], id); }
                canonical[i] = id;
            }
            for (int i = 0; i < indices.Length; i += 3) {
                if (Vector3.Cross(vertices[indices[i+1]] - vertices[indices[i]], vertices[indices[i+2]] - vertices[indices[i]]).sqrMagnitude < 1e-12f) audit.degenerateTriangles++;
                for (int edge = 0; edge < 3; edge++) {
                    uint a = (uint)canonical[indices[i+edge]], b = (uint)canonical[indices[i+(edge+1)%3]];
                    ulong key = ((ulong)Math.Min(a,b) << 32) | Math.Max(a,b);
                    edges.TryGetValue(key, out int count); edges[key] = count + 1;
                }
            }
            audit.boundaryEdges = edges.Values.Count(n => n == 1);
            audit.nonManifoldEdges = edges.Values.Count(n => n > 2);
            audit.limitation = "Exact-position welded topology only. Boundary edges can be intentional doors/windows. Thickness, winding and openings require spatial inspection; no invalid-bake inference.";
        } catch (Exception e) { audit.limitation = e.Message; }
        return audit;
    }

    public static string Sha(string path) {
        using (var algorithm = SHA256.Create()) using (var file = File.OpenRead(path))
            return BitConverter.ToString(algorithm.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }

    static readonly string[] BakeFiles = {
        "Assets/KoboldKare/Scenes/MainMap.unity",
        "Assets/KoboldKare/Scenes/MainMap/OcclusionCullingData.asset",
        "Assets/KoboldKare/Scenes/MainMap/OcclusionCullingData.asset.meta"
    };

    static void RequireSavedMainMap() {
        if (EditorApplication.isPlaying || SceneManager.sceneCount != 1 || SceneManager.GetActiveScene().path != BakeFiles[0] || SceneManager.GetActiveScene().isDirty || !SceneManager.GetActiveScene().isLoaded || SceneManager.GetActiveScene().rootCount == 0)
            throw new InvalidOperationException("Requires one saved MainMap open outside Play Mode; tool will not save or discard unrelated edits.");
    }

    static void BakeExperiment(Request request) {
        RequireSavedMainMap();
        string backup = Path.GetFullPath(request.output);
        if (Directory.Exists(backup) && Directory.EnumerateFileSystemEntries(backup).Any())
            throw new InvalidOperationException("Use a new empty backup directory for each bake experiment.");
        Directory.CreateDirectory(backup);
        for (int i = 0; i < BakeFiles.Length; i++) File.Copy(BakeFiles[i], Path.Combine(backup, i + ".original"));
        File.WriteAllLines(Path.Combine(backup, "original-hashes.txt"), BakeFiles.Select(p => Sha(p) + " " + p));
        try {
            if (request.smallestOccluder > 0) StaticOcclusionCulling.smallestOccluder = request.smallestOccluder;
            if (request.smallestHole > 0) StaticOcclusionCulling.smallestHole = request.smallestHole;
            if (request.backfaceThreshold >= 0) StaticOcclusionCulling.backfaceThreshold = request.backfaceThreshold;
            bakeBackup = backup; bakeError = false; bakeCancelled = false;
            bakeStarted = EditorApplication.timeSinceStartup;
            Application.logMessageReceived += ObserveBakeLog;
            StaticOcclusionCulling.GenerateInBackground();
            EditorApplication.update += FinishBackgroundBake;
            File.WriteAllText(Path.Combine(backup, "status.txt"), "running");
        } catch {
            Application.logMessageReceived -= ObserveBakeLog;
            bakeBackup = null;
            for (int i = 0; i < BakeFiles.Length; i++) File.Copy(Path.Combine(backup, i + ".original"), BakeFiles[i], true);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            EditorSceneManager.OpenScene(BakeFiles[0], OpenSceneMode.Single);
            throw;
        }
    }

    static void ObserveBakeLog(string condition, string stack, LogType type) {
        if ((type == LogType.Error || type == LogType.Exception) &&
            (condition.IndexOf("TomeGenerator", StringComparison.OrdinalIgnoreCase) >= 0 ||
             condition.IndexOf("occluder data computation", StringComparison.OrdinalIgnoreCase) >= 0 ||
             condition.IndexOf("Out of memory", StringComparison.OrdinalIgnoreCase) >= 0)) bakeError = true;
    }

    static void FinishBackgroundBake() {
        if (StaticOcclusionCulling.isRunning || EditorApplication.timeSinceStartup - bakeStarted < 1) return;
        EditorApplication.update -= FinishBackgroundBake;
        Application.logMessageReceived -= ObserveBakeLog;
        string backup = bakeBackup;
        bakeBackup = null;
        if (backup == null) return;
        try {
            if (bakeCancelled || bakeError)
                throw new InvalidOperationException(bakeCancelled ? "Bake cancelled." : "Bake failed; see Unity log.");
            if (!EditorSceneManager.SaveScene(SceneManager.GetActiveScene())) throw new IOException("Cannot save experimental scene settings.");
            File.WriteAllLines(Path.Combine(backup, "experimental-hashes.txt"), BakeFiles.Select(p => Sha(p) + " " + p));
            File.WriteAllText(Path.Combine(backup, "status.txt"), "complete");
        } catch (Exception e) {
            // Keep generated output for inspection/recovery before restoring the reference.
            for (int i = 0; i < BakeFiles.Length; i++)
                if (File.Exists(BakeFiles[i])) File.Copy(BakeFiles[i], Path.Combine(backup, i + ".generated-before-restore"), true);
            for (int i = 0; i < BakeFiles.Length; i++) File.Copy(Path.Combine(backup, i + ".original"), BakeFiles[i], true);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            EditorSceneManager.OpenScene(BakeFiles[0], OpenSceneMode.Single);
            File.WriteAllText(Path.Combine(backup, "status.txt"), "failed and restored: " + e.Message);
            Debug.LogException(e);
        }
    }

    static void RestoreBake(string directory) {
        RequireSavedMainMap();
        var backup = Path.GetFullPath(directory);
        // Validate every backup before touching any target. Targets are fixed project files.
        for (int i = 0; i < BakeFiles.Length; i++)
            if (!File.Exists(Path.Combine(backup, i + ".original"))) throw new FileNotFoundException("Incomplete bake backup.");
        for (int i = 0; i < BakeFiles.Length; i++) File.Copy(Path.Combine(backup, i + ".original"), BakeFiles[i], true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        EditorSceneManager.OpenScene(BakeFiles[0], OpenSceneMode.Single);
    }


    [MenuItem("Tools/Rendering diagnostics/Build Development capture player")]
    public static void BuildMenu() => BuildDevelopment("Builds/RenderingDiagnostics/KoboldKare.exe");

    public static void BuildDevelopment(string output) {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Leave Play Mode before building.");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Open scene has unsaved changes. Preserve them explicitly before building; tool will not save/discard them.");
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            throw new InvalidOperationException("Select Windows x86_64 before invoking the build.");
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var oldOption = settings.BuildAddressablesWithPlayerBuild;
        bool frameTiming = PlayerSettings.enableFrameTimingStats;
        // Preserve exact bytes, including existing local edits, even if the build pipeline saves settings.
        string settingsPath = AssetDatabase.GetAssetPath(settings);
        var preserved = new Dictionary<string, byte[]>();
        var preservePaths = new HashSet<string>(new[] {
            "ProjectSettings/ProjectSettings.asset", "ProjectSettings/QualitySettings.asset", "ProjectSettings/GraphicsSettings.asset",
            settingsPath, "Assets/AddressableAssetsData/Windows/addressables_content_state.bin",
            AssetDatabase.GetAssetPath(UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset),
            AssetDatabase.GetAssetPath(QualitySettings.renderPipeline)
        });
        // Play Mode settings and OnValidate can leave assets dirty before Addressables saves them.
        foreach (var asset in Resources.FindObjectsOfTypeAll<UnityEngine.Object>()) {
            if (asset && EditorUtility.IsDirty(asset) && AssetDatabase.Contains(asset)) preservePaths.Add(AssetDatabase.GetAssetPath(asset));
        }
        // Imported network prefabs can be reserialized during content validation even when initially clean.
        foreach (var prefab in Directory.EnumerateFiles("Assets/KoboldKare/NetworkedPrefabs", "*.prefab", SearchOption.AllDirectories)) preservePaths.Add(prefab);
        foreach (var addressableAsset in Directory.EnumerateFiles("Assets/AddressableAssetsData", "*.asset", SearchOption.AllDirectories)) preservePaths.Add(addressableAsset);
        foreach (var path in preservePaths)
            if (File.Exists(path)) preserved[path] = File.ReadAllBytes(path);
        string sceneSha = Sha("Assets/KoboldKare/Scenes/MainMap.unity");
        string bakePath = "Assets/KoboldKare/Scenes/MainMap/OcclusionCullingData.asset";
        string bakeSha = Sha(bakePath);
        try {
            PlayerSettings.enableFrameTimingStats = true;
            // A full content build is required: cached bundles could contain a different scene/bake.
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            AddressableAssetSettings.BuildPlayerContent(out var content);
            if (!string.IsNullOrEmpty(content.Error)) throw new InvalidOperationException(content.Error);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            var report = BuildPipeline.BuildPlayer(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(), output,
                BuildTarget.StandaloneWindows64, BuildOptions.Development);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "build-result.txt"), report.summary.result + "\n" + report.summary.totalTime + "\nscene=" + sceneSha + "\nbake=" + bakeSha);
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Build failed: " + report.summary.result);
        } finally {
            PlayerSettings.enableFrameTimingStats = frameTiming;
            settings.BuildAddressablesWithPlayerBuild = oldOption;
            foreach (var pair in preserved) File.WriteAllBytes(pair.Key, pair.Value);
        }
        if (Sha("Assets/KoboldKare/Scenes/MainMap.unity") != sceneSha || Sha(bakePath) != bakeSha)
            throw new InvalidOperationException("Scene/bake changed during build; do not use this reference.");
    }

    static void ExportFrameApi(string output) {
        var builder = new StringBuilder();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.GetName().Name.StartsWith("UnityEditor")) continue;
            foreach (var type in assembly.GetTypes().Where(t => t.FullName.Contains("FrameDebugger"))) {
                builder.AppendLine(type.FullName);
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) builder.AppendLine("  " + member);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output, builder.ToString());
    }

}
