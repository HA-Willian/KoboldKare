#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace KoboldKare.RenderingDiagnostics {
    // Opt-in only. No scene or prefab reference is required; release players contain no tool.
    [DefaultExecutionOrder(32000)]
    public sealed class RenderCapture : MonoBehaviour {
        public static RenderCapture Active { get; private set; }
        public RenderCaptureConfig Config { get; private set; }
        public string Status { get; private set; }
        public bool Holding { get; private set; }
        Camera cameraTarget;
        Vector3 lockedPosition, oldPosition;
        Quaternion lockedRotation, oldRotation;
        bool oldOcclusion, restored = true, wasPaused, oldRunInBackground;
        OrbitCamera orbit;
        FieldInfo pausedField;
        int oldVSync, oldCap, oldWidth, oldHeight, quality;
        FullScreenMode oldFullScreen;
        UniversalRenderPipelineAsset originalPipeline, diagnosticPipeline;
        RenderPipelineAsset oldGraphicsPipeline, oldQualityPipeline;
        float originalShadowDistance, fov, aspect;
        float oldFov, oldAspect;
        bool finished;
        string output;
        readonly List<Counter> counters = new List<Counter>();
        readonly List<string> rows = new List<string>();
        readonly List<string> events = new List<string>();
        readonly FrameTiming[] timings = new FrameTiming[1];
        readonly Dictionary<string, CaptureTransform> lastRegion = new Dictionary<string, CaptureTransform>();
        readonly List<RegionChange> regionChanges = new List<RegionChange>();
        static readonly FieldInfo ModInstance = typeof(ModManager).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo PlayerConfig = typeof(ModManager).GetField("playerConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        List<ModManager.ModStub> oldModPreferences;
        readonly List<ModManager.ModStub> noMods = new List<ModManager.ModStub>();
        int expectedWidth, expectedHeight;
        bool expectedOcclusion;
        float expectedShadowDistance;
        Counter batchesCounter, setpassCounter, drawsCounter, castersCounter, mainCounter, renderCounter, gpuCounter;
        struct FrameSample {
            public int frame;
            public long batches, setpass, draws, casters, main, render, gpu;
            public double frameMs, cpuMs, gpuMs;
        }

        [Serializable] sealed class RegionChange { public int frame; public CaptureTransform transform; }
        [Serializable] sealed class RegionLog { public List<RegionChange> changes; public List<string> events; }
        [Serializable] sealed class Manifest {
            public string utc, unity, platform, gpu, cpu, pipeline, counterScope, visibilityScope, status;
            public bool editor, development, vanilla;
            public int quality, width, height, msaa, cascades;
            public float renderScale, shadowDistance, fov, aspect;
            public RenderCaptureConfig config;
            public string[] scenes, cameras, availableCounters;
        }
        sealed class Counter : IDisposable {
            public string name, unit;
            public ProfilerRecorder recorder;
            public void Dispose() => recorder.Dispose();
            public long Read() => recorder.Valid && recorder.Count > 0 ? recorder.LastValue : -1;
        }
        sealed class Point { public float t; public double batches, setpass; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--kk-render-capture");
            if (index < 0 || index + 1 >= args.Length) return;
            var config = JsonUtility.FromJson<RenderCaptureConfig>(File.ReadAllText(args[index + 1]));
            Begin(config);
        }

        public static RenderCapture Begin(RenderCaptureConfig config) {
            if (Active) throw new InvalidOperationException("A render capture is already active.");
            var host = new GameObject("Render capture (development only)");
            DontDestroyOnLoad(host);
            var capture = host.AddComponent<RenderCapture>();
            Active = capture;
            capture.Config = config;
            capture.StartCoroutine(capture.GuardedRun());
            return capture;
        }

        IEnumerator GuardedRun() {
            var routine = Run();
            Exception failure = null;
            // Explicit iterator driver catches failures in nested measurement enumerators too.
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (stack.Count > 0) {
                object current = null;
                bool more = false;
                try {
                    more = stack.Peek().MoveNext();
                    if (more) current = stack.Peek().Current;
                } catch (Exception e) { failure = e; }
                if (failure != null) break;
                if (!more) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            if (failure != null) {
                Status = "FAILED: " + failure;
                Debug.LogException(failure);
                if (!string.IsNullOrEmpty(output)) File.WriteAllText(Path.Combine(output, "failure.txt"), Status);
            } else Status = "complete";
            finished = true;
            try { Flush(); } finally { Restore(); }
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(Path.Combine(output, "status.txt"), Status);
            Debug.Log("Render capture: " + Status + " -> " + output);
            if (!Application.isEditor && Config.quitWhenDone) Application.Quit(failure == null ? 0 : 1);
            Destroy(gameObject);
        }

        IEnumerator Run() {
            if (Config.samples < 17 || Config.measureFrames < 2 || Config.settleFrames < 10 || Config.observationFrames < 1)
                throw new ArgumentException("Use >=17 samples, >=2 measured frames, >=10 settle frames and >=1 observation frame.");
            var candidateOutput = string.IsNullOrEmpty(Config.outputDirectory)
                ? Path.Combine(Application.persistentDataPath, "RenderCaptures", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))
                : Path.GetFullPath(Config.outputDirectory);
            if (Directory.Exists(candidateOutput) && Directory.EnumerateFileSystemEntries(candidateOutput).Any())
                throw new InvalidOperationException("Use a new, empty output directory; existing evidence will not be overwritten.");
            output = candidateOutput;
            Directory.CreateDirectory(output);
            Status = "preparing vanilla";
            File.WriteAllText(Path.Combine(output, "status.txt"), Status);
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
            if (Config.startVanilla) {
                double deadline = Time.realtimeSinceStartupAsDouble + 180;
                while (ModInstance?.GetValue(null) == null || !ModManager.GetReady()) {
                    if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Mod initialization did not finish.");
                    yield return null;
                }
                // Session-only: do not call SaveConfig; never rewrite the user's mod preferences.
                oldModPreferences = new List<ModManager.ModStub>(ModManager.GetPlayerConfig());
                var disableMods = ModManager.AllModsSetActive(false);
                while (!disableMods.IsCompleted) yield return null;
                if (disableMods.IsFaulted) throw disableMods.Exception;
                NetworkManager.instance.SetSelectedMap("MainMap");
                yield return NetworkManager.instance.SinglePlayerRoutine();
                MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.None);
                yield return new WaitForSecondsRealtime(5);
            }
            if (!ModManager.GetReady() || !ModManager.HasExactModConfigurationLoaded(noMods))
                throw new InvalidOperationException("Vanilla guard failed: loaded mod assets remain.");
            var timeout = Time.realtimeSinceStartupAsDouble + 60;
            while (!(cameraTarget = OrbitCamera.GetCamera())) {
                if (Time.realtimeSinceStartupAsDouble > timeout) throw new TimeoutException("OrbitCamera was not created.");
                yield return null;
            }
            Acquire();
            yield return new WaitForSecondsRealtime(2);
            if (Config.verticalFov > 0) cameraTarget.fieldOfView = Config.verticalFov;
            if (Config.cameraAspect > 0) cameraTarget.aspect = Config.cameraAspect;
            fov = cameraTarget.fieldOfView; aspect = cameraTarget.aspect;
            expectedWidth = Screen.width;
            expectedHeight = Screen.height;
            if (!Application.isEditor && (expectedWidth != Config.width || expectedHeight != Config.height))
                throw new InvalidOperationException("The requested fixed resolution was not applied.");
            SetupCounters();
            WriteManifest();
            rows.Add("label,configuration,path,direction,t,frame,batches,setpass,draws,shadowCasters,mainThreadNs,renderThreadNs,gpuNs,frameMs,cpuTimingMs,gpuTimingMs");
            if (Config.mode == "holdA" || Config.mode == "holdB") {
                SetPose(Config.mode == "holdA" ? 0 : 1, "combined");
                Holding = true;
                Status = Config.mode;
                File.WriteAllText(Path.Combine(output, "status.txt"), Status);
                while (Holding) yield return null;
                yield break;
            }
            foreach (var configuration in new[] { "original", "no-occlusion", "no-shadows", "neither" }) {
                cameraTarget.useOcclusionCulling = configuration == "original" || configuration == "no-shadows" ? oldOcclusion : false;
                diagnosticPipeline.shadowDistance = configuration == "no-shadows" || configuration == "neither" ? 0 : originalShadowDistance;
                yield return Measure(configuration + "-A", configuration, "matrix", "AB", 0, true);
                yield return Measure(configuration + "-B", configuration, "matrix", "AB", 1, true);
                // Reverse endpoint repeat exposes time/order drift in the isolation matrix.
                yield return Measure(configuration + "-B-repeat", configuration, "matrix", "BA", 1, false);
                yield return Measure(configuration + "-A-repeat", configuration, "matrix", "BA", 0, false);
            }
            cameraTarget.useOcclusionCulling = oldOcclusion;
            diagnosticPipeline.shadowDistance = originalShadowDistance;
            if (Config.mode == "matrix") yield break;
            foreach (var path in new[] { "combined", "position", "rotation" }) {
                foreach (var reverse in new[] { false, true }) {
                    var points = new List<Point>();
                    for (int i = 0; i < Config.samples; i++) {
                        float t = (reverse ? Config.samples - 1 - i : i) / (float)(Config.samples - 1);
                        yield return Measure(path + "-" + (reverse ? "BA" : "AB") + "-" + i, "original", path, reverse ? "BA" : "AB", t, false, points);
                    }
                    points.Sort((a,b) => a.t.CompareTo(b.t));
                    int largest = 0;
                    for (int i = 1; i < points.Count - 1; i++)
                        if (Jump(points[i], points[i+1]) > Jump(points[largest], points[largest+1])) largest = i;
                    var low = points[largest];
                    var high = points[largest+1];
                    for (int depth = 0; depth < Config.bisections; depth++) {
                        var midpoint = new List<Point>();
                        yield return Measure(path + "-bisect-" + (reverse ? "BA" : "AB") + "-" + depth, "original", path, reverse ? "BA-refine" : "AB-refine", (low.t + high.t) / 2, false, midpoint);
                        var middle = midpoint[0];
                        if (Jump(low, middle) >= Jump(middle, high)) high = middle; else low = middle;
                    }
                    File.AppendAllText(Path.Combine(output, "transitions.csv"), string.Join(",", path, reverse ? "BA" : "AB", F(low.t), F(high.t), F(low.batches), F(high.batches), F(low.setpass), F(high.setpass)) + "\n");
                }
            }
            // Four additional views: outside the path, including changed height and facing.
            var offsets = new[] { new Vector3(-1,0,0), new Vector3(1,0,0), new Vector3(0,1,0), new Vector3(0,0,-1) };
            for (int i = 0; i < offsets.Length; i++) {
                yield return Measure("validation-" + i, "original", "validation", "extra", i / 3f, true, null, offsets[i]);
            }
        }

        static double Jump(Point a, Point b) => Math.Abs(a.batches - b.batches) + Math.Abs(a.setpass - b.setpass);
        public void StopHolding() => Holding = false;
        public void SetHeldPose(bool poseB) {
            if (!Holding || UnityEngine.FrameDebugger.enabled) throw new InvalidOperationException("Disable Frame Debugger and hold a pose first.");
            SetPose(poseB ? 1 : 0, "combined");
            Status = poseB ? "holdB" : "holdA";
        }

        void Acquire() {
            originalPipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (!originalPipeline) throw new InvalidOperationException("URP asset is required.");
            oldVSync = QualitySettings.vSyncCount;
            oldCap = Application.targetFrameRate;
            oldRunInBackground = Application.runInBackground;
            oldWidth = Screen.width; oldHeight = Screen.height; oldFullScreen = Screen.fullScreenMode;
            quality = QualitySettings.GetQualityLevel();
            oldPosition = cameraTarget.transform.position; oldRotation = cameraTarget.transform.rotation;
            oldOcclusion = cameraTarget.useOcclusionCulling;
            oldFov = cameraTarget.fieldOfView; oldAspect = cameraTarget.aspect;
            oldGraphicsPipeline = GraphicsSettings.renderPipelineAsset;
            oldQualityPipeline = QualitySettings.renderPipeline;
            originalShadowDistance = Config.shadowDistanceOverride >= 0 ? Config.shadowDistanceOverride : originalPipeline.shadowDistance;
            orbit = cameraTarget.GetComponent<OrbitCamera>();
            pausedField = typeof(OrbitCamera).GetField("paused", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!orbit || pausedField == null) throw new InvalidOperationException("Cannot preserve OrbitCamera pause state.");
            wasPaused = (bool)pausedField.GetValue(orbit);
            restored = false; // All restoration fields have been read before any mutation.
            lockedPosition = oldPosition; lockedRotation = oldRotation;
            OrbitCamera.SetPaused(true); // Disabling the component alone does not stop its tween coroutine.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            diagnosticPipeline = Instantiate(originalPipeline);
            diagnosticPipeline.name = originalPipeline.name + " (capture runtime clone)";
            diagnosticPipeline.shadowDistance = originalShadowDistance;
            if (Config.shadowCascadesOverride > 0) diagnosticPipeline.shadowCascadeCount = Config.shadowCascadesOverride;
            GraphicsSettings.renderPipelineAsset = diagnosticPipeline;
            QualitySettings.renderPipeline = diagnosticPipeline;
            if (!Application.isEditor) Screen.SetResolution(Config.width, Config.height, FullScreenMode.Windowed);
        }

        void SetPose(float t, string path, Vector3 offset = default) {
            lockedPosition = Vector3.Lerp(Config.positionA, Config.positionB, path == "rotation" ? 0 : t) + offset;
            lockedRotation = Quaternion.Slerp(Quaternion.Euler(Config.rotationA), Quaternion.Euler(Config.rotationB), path == "position" ? 0 : t);
            LockPose();
        }

        void LockPose() {
            if (!restored && cameraTarget) cameraTarget.transform.SetPositionAndRotation(lockedPosition, lockedRotation);
        }
        void LateUpdate() => LockPose();

        IEnumerator Measure(string label, string configuration, string path, string direction, float t, bool inventory, List<Point> points = null, Vector3 offset = default) {
            Status = label;
            SetPose(t, path, offset);
            expectedOcclusion = cameraTarget.useOcclusionCulling;
            expectedShadowDistance = diagnosticPipeline.shadowDistance;
            for (int i = 0; i < Config.settleFrames; i++) yield return null;
            if (!ModManager.HasExactModConfigurationLoaded(noMods)) throw new InvalidOperationException("Mod assets were loaded during capture.");
            var batchValues = new List<long>(Config.measureFrames);
            var passValues = new List<long>(Config.measureFrames);
            var frames = new FrameSample[Config.measureFrames];
            // No inventory scans, JSON/CSV I/O, screenshots or per-renderer callbacks in timed frames.
            for (int i = 0; i < Config.measureFrames; i++) {
                yield return null; // ProfilerRecorder.LastValue describes the preceding, completed frame.
                ValidateInvariants();
                uint count = FrameTimingManager.GetLatestTimings(1, timings);
                long batches = Value(batchesCounter), setpass = Value(setpassCounter);
                batchValues.Add(batches); passValues.Add(setpass);
                frames[i] = new FrameSample { frame = Time.frameCount - 1, batches = batches, setpass = setpass,
                    draws = Value(drawsCounter), casters = Value(castersCounter), main = Value(mainCounter), render = Value(renderCounter), gpu = Value(gpuCounter),
                    frameMs = Time.unscaledDeltaTime * 1000.0, cpuMs = count > 0 ? timings[0].cpuFrameTime : 0, gpuMs = count > 0 ? timings[0].gpuFrameTime : 0 };
                FrameTimingManager.CaptureFrameTimings();
            }
            if (!ModManager.HasExactModConfigurationLoaded(noMods)) throw new InvalidOperationException("Mod assets were loaded during capture.");
            foreach (var sample in frames) rows.Add(string.Join(",", label, configuration, path, direction, F(t), sample.frame,
                sample.batches, sample.setpass, sample.draws, sample.casters, sample.main, sample.render, sample.gpu, F(sample.frameMs),
                sample.cpuMs > 0 ? F(sample.cpuMs) : "", sample.gpuMs > 0 ? F(sample.gpuMs) : ""));
            points?.Add(new Point { t = t, batches = Median(batchValues), setpass = Median(passValues) });
            TrackRegion();
            if (inventory) {
                var snapshot = new CaptureInventory { label = label, frame = Time.frameCount, region = RenderCaptureData.RegionTransforms() };
                // Refresh at every observation frame so newly spawned Renderers participate.
                var records = new Dictionary<int, CaptureRenderer>();
                for (int i = 0; i < Config.observationFrames; i++) {
                    yield return new WaitForEndOfFrame();
                    var planes = GeometryUtility.CalculateFrustumPlanes(cameraTarget);
                    foreach (var r in RenderCaptureData.LoadedRenderers()) {
                        if (records.TryGetValue(r.GetInstanceID(), out var old)) {
                            old.isVisibleObserved |= r.isVisible;
                            old.frustumCandidate |= GeometryUtility.TestPlanesAABB(planes, r.bounds);
                        } else records[r.GetInstanceID()] = RenderCaptureData.RendererOf(r, planes);
                    }
                }
                snapshot.renderers.AddRange(records.Values.OrderBy(r => r.key, StringComparer.Ordinal));
                RenderCaptureData.WriteJson(Path.Combine(output, label + "-inventory.json"), snapshot);
                if (Config.screenshots) {
                    ScreenCapture.CaptureScreenshot(Path.Combine(output, label + ".png"));
                    yield return new WaitForEndOfFrame();
                }
            }
            File.WriteAllLines(Path.Combine(output, "frames.csv"), rows);
            File.WriteAllText(Path.Combine(output, "status.txt"), label);
        }

        void ValidateInvariants() {
            if (!cameraTarget || Vector3.Distance(cameraTarget.transform.position, lockedPosition) > .0001f || Quaternion.Angle(cameraTarget.transform.rotation, lockedRotation) > .001f)
                throw new InvalidOperationException("Camera pose drifted during capture.");
            if (QualitySettings.GetQualityLevel() != quality || Screen.width != expectedWidth || Screen.height != expectedHeight ||
                QualitySettings.vSyncCount != 0 || Application.targetFrameRate != -1 || GraphicsSettings.currentRenderPipeline != diagnosticPipeline || cameraTarget.fieldOfView != fov ||
                Mathf.Abs(cameraTarget.aspect - aspect) > .0001f || cameraTarget.useOcclusionCulling != expectedOcclusion || diagnosticPipeline.shadowDistance != expectedShadowDistance)
                throw new InvalidOperationException("Resolution, quality, camera FOV, pipeline or frame cap changed during capture.");
        }

        void SetupCounters() {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var descriptions = handles.Select(ProfilerRecorderHandle.GetDescription).ToArray();
            File.WriteAllLines(Path.Combine(output, "available-counters.txt"), descriptions.Select(d => d.Category.Name + " | " + d.Name + " | " + d.UnitType));
            foreach (var name in new[] { "Batches Count", "SetPass Calls Count", "Draw Calls Count", "Shadow Casters Count", "Main Thread", "Render Thread", "GPU Frame Time" }) {
                var d = descriptions.FirstOrDefault(x => x.Name == name);
                if (d.Name == null) continue;
                counters.Add(new Counter { name = name, unit = d.UnitType.ToString(), recorder = ProfilerRecorder.StartNew(d.Category, name, 1) });
            }
            if (!counters.Any(c => c.name == "Batches Count") || !counters.Any(c => c.name == "SetPass Calls Count"))
                throw new InvalidOperationException("Required render counters are unavailable on this player.");
            batchesCounter = Find("Batches Count"); setpassCounter = Find("SetPass Calls Count");
            drawsCounter = Find("Draw Calls Count"); castersCounter = Find("Shadow Casters Count");
            mainCounter = Find("Main Thread"); renderCounter = Find("Render Thread"); gpuCounter = Find("GPU Frame Time");
        }

        void WriteManifest() {
            var cameras = Camera.allCameras.Select(c => RenderCaptureData.Key(c) + " | type=" + c.cameraType + " | occlusion=" + c.useOcclusionCulling + " | mask=" + c.cullingMask).ToArray();
            RenderCaptureData.WriteJson(Path.Combine(output, "manifest.json"), new Manifest {
                utc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion, platform = Application.platform.ToString(), gpu = SystemInfo.graphicsDeviceName,
                cpu = SystemInfo.processorType, pipeline = originalPipeline.name, editor = Application.isEditor, development = Debug.isDebugBuild,
                vanilla = ModManager.HasExactModConfigurationLoaded(noMods), quality = quality, width = Screen.width, height = Screen.height,
                msaa = diagnosticPipeline.msaaSampleCount, cascades = diagnosticPipeline.shadowCascadeCount, renderScale = diagnosticPipeline.renderScale,
                shadowDistance = originalShadowDistance, fov = fov, aspect = cameraTarget.aspect, config = Config,
                counterScope = "Global completed-frame counters across ALL cameras/passes, not per-camera draw attribution. Missing counters = -1; missing timings = empty.",
                visibilityScope = "Renderer.isVisible is an any-camera/shadow observation; frustum is geometric candidacy only. Neither proves a draw or pass.",
                scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).path).ToArray(),
                cameras = cameras, availableCounters = counters.Select(c => c.name + " | " + c.unit).ToArray(), status = "running"
            });
        }

        void TrackRegion() {
            foreach (var t in RenderCaptureData.RegionTransforms()) {
                if (!lastRegion.TryGetValue(t.key, out var previous) || previous.id != t.id || previous.position != t.position || previous.rotation != t.rotation || previous.lossyScale != t.lossyScale ||
                    previous.localPosition != t.localPosition || previous.localRotation != t.localRotation || previous.localScale != t.localScale) {
                    regionChanges.Add(new RegionChange { frame = Time.frameCount, transform = t });
                    lastRegion[t.key] = t;
                }
            }
        }
        void SceneLoaded(Scene scene, LoadSceneMode mode) { events.Add(Time.frameCount + " loaded " + scene.path + " " + mode); TrackRegion(); }
        void SceneUnloaded(Scene scene) => events.Add(Time.frameCount + " unloaded " + scene.path);
        static double Median(List<long> values) { values.Sort(); return values.Count % 2 == 0 ? ((double)values[values.Count/2-1] + values[values.Count/2])/2 : values[values.Count/2]; }
        static string F(double n) => n.ToString("R", CultureInfo.InvariantCulture);
        Counter Find(string name) => counters.FirstOrDefault(c => c.name == name);
        static long Value(Counter counter) => counter?.Read() ?? -1;

        void Flush() {
            if (string.IsNullOrEmpty(output)) return;
            if (rows.Count > 0) File.WriteAllLines(Path.Combine(output, "frames.csv"), rows);
            RenderCaptureData.WriteJson(Path.Combine(output, "region-runtime.json"), new RegionLog { changes = regionChanges, events = events });
        }
        void Restore() {
            // GameManager saves this list on application exit. Restore it even when preparation fails.
            if (oldModPreferences != null && ModInstance?.GetValue(null) is ModManager manager) {
                PlayerConfig.SetValue(manager, oldModPreferences);
                oldModPreferences = null;
            }
            foreach (var counter in counters) counter.Dispose();
            counters.Clear();
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            if (restored) return;
            restored = true;
            QualitySettings.vSyncCount = oldVSync;
            Application.targetFrameRate = oldCap;
            Application.runInBackground = oldRunInBackground;
            GraphicsSettings.renderPipelineAsset = oldGraphicsPipeline;
            QualitySettings.renderPipeline = oldQualityPipeline;
            if (cameraTarget) {
                cameraTarget.useOcclusionCulling = oldOcclusion;
                cameraTarget.fieldOfView = oldFov;
                cameraTarget.aspect = oldAspect;
                cameraTarget.transform.SetPositionAndRotation(oldPosition, oldRotation);
            }
            if (orbit && pausedField != null) pausedField.SetValue(orbit, wasPaused);
            if (!Application.isEditor) Screen.SetResolution(oldWidth, oldHeight, oldFullScreen);
            if (diagnosticPipeline) Destroy(diagnosticPipeline);
        }
        void OnDisable() {
            Holding = false;
            if (!finished && !string.IsNullOrEmpty(output)) File.WriteAllText(Path.Combine(output, "status.txt"), "aborted: component disabled before completion");
            Flush(); Restore();
        }
        void OnDestroy() { Restore(); if (Active == this) Active = null; }
    }
}
#endif
