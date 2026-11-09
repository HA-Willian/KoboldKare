using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KoboldKare.RenderingDiagnostics;
using UnityEditor;
using UnityEngine;

// Version-checked adapter for Unity 2021's internal Frame Debugger API.
// An SRP event may contain many draws. Do not label its one associated object as the whole batch.
public static class FrameDebuggerCapture {
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Type Utility = typeof(Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerUtility", true);
    static readonly Type Data = typeof(Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerEventData", true);
    static readonly Type WindowType = typeof(Editor).Assembly.GetType("UnityEditor.FrameDebuggerWindow", true);
    static Export pending;
    static string pendingPath;
    static int pendingIndex, oldLimit;
    static double nextRead, eventDeadline;
    static bool selected;

    [Serializable] sealed class Export {
        public string unity, utc, evidence;
        public bool localEditor;
        public int eventCount;
        public int connectionGuid;
        public string[] batchBreakCauses;
        public List<Event> events = new List<Event>();
    }
    [Serializable] sealed class Event {
        public int index, draws, componentInstanceId, shaderInstanceId, passIndex, batchBreakCause, meshInstanceId;
        public int[] meshInstanceIds;
        public bool dataAvailable, dataIndexMatches;
        public string type, info, shader, pass, lightMode, keywords, associatedObject, rendererKey, objectEvidence;
    }

    static object Call(string name, params object[] args) => Utility.GetMethod(name, Static).Invoke(null, args);
    static object Field(object data, string field) => Data.GetField(field, Instance).GetValue(data);

    public static void Enable() {
        if (!EditorApplication.isPlaying || !RenderCapture.Active || !RenderCapture.Active.Holding)
            throw new InvalidOperationException("Hold pose A or B before enabling the Frame Debugger. Never enable it during timings.");
        var window = GetWindow();
        WindowType.GetMethod("ClickEnableFrameDebugger", Instance).Invoke(window, null);
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }
    static EditorWindow GetWindow() => Resources.FindObjectsOfTypeAll(WindowType).FirstOrDefault() as EditorWindow
        ?? (EditorWindow)WindowType.GetMethod("ShowFrameDebuggerWindow", Static).Invoke(null, null);
    public static void Disable() {
        if (pending != null) CompleteExport();
        WindowType.GetMethod("DisableFrameDebugger", Static).Invoke(null, null);
        if (EditorApplication.isPlaying) EditorApplication.isPaused = false;
    }

    public static void Save(string path) {
        if (!UnityEngine.FrameDebugger.enabled) throw new InvalidOperationException("Enable the Frame Debugger first, then allow a rendered frame.");
        var events = (Array)Call("GetFrameEvents");
        if (events.Length == 0) throw new InvalidOperationException("No Frame Debugger events captured yet.");
        if (pending != null) throw new InvalidOperationException("A Frame Debugger export is already running.");
        var export = new Export {
            unity = Application.unityVersion, utc = DateTime.UtcNow.ToString("O"), localEditor = UnityEngine.FrameDebugger.enabled,
            connectionGuid = (int)Call("GetRemotePlayerGUID"),
            eventCount = events.Length, batchBreakCauses = (string[])Call("GetBatchBreakCauseStrings"),
            evidence = "Frame Debugger events from held pose, separate from performance timing. drawCallCount belongs to an event; associated object may represent only one member of an SRP batch. Missing or mismatched event data is unconfirmed."
        };
        for (int i = 0; i < events.Length; i++) {
            var raw = events.GetValue(i);
            var record = new Event {
                index = i, type = raw.GetType().GetField("type", Instance).GetValue(raw).ToString(),
                info = (string)Call("GetFrameEventInfoName", i),
                draws = -1, objectEvidence = "No complete per-renderer draw attribution."
            };
            var obj = Call("GetFrameEventObject", i) as UnityEngine.Object;
            if (obj) {
                record.associatedObject = obj.name + "#" + obj.GetInstanceID();
                var renderer = obj as Renderer;
                if (!renderer && obj is GameObject go) renderer = go.GetComponent<Renderer>();
                if (renderer) {
                    record.rendererKey = RenderCaptureData.RendererOf(renderer).key;
                    record.objectEvidence = "Object associated by Frame Debugger; draw count pending; does not enumerate an SRP batch.";
                }
            }
            export.events.Add(record);
        }
        pending = export; pendingPath = path; pendingIndex = 0; selected = false;
        oldLimit = (int)Utility.GetProperty("limit", Static).GetValue(null);
        EditorApplication.update += ReadSelectedEvent;
        File.WriteAllText(path + ".status.txt", "selecting events");
    }

    static void ReadSelectedEvent() {
        if (pending == null || EditorApplication.timeSinceStartup < nextRead) return;
        try {
            if (!UnityEngine.FrameDebugger.enabled || pendingIndex >= pending.events.Count) { CompleteExport(); return; }
            if (!selected) {
                WindowType.GetMethod("ChangeFrameEventLimit", Instance).Invoke(GetWindow(), new object[] { pendingIndex + 1 });
                selected = true;
                eventDeadline = EditorApplication.timeSinceStartup + .5;
                nextRead = EditorApplication.timeSinceStartup + .04;
                EditorApplication.QueuePlayerLoopUpdate();
                return;
            }
            var record = pending.events[pendingIndex];
            var data = Activator.CreateInstance(Data);
            bool success = (bool)Call("GetFrameEventData", pendingIndex, data);
            record.dataAvailable = success;
            record.dataIndexMatches = success && (int)Field(data, "frameEventIndex") == pendingIndex;
            if (!record.dataIndexMatches && EditorApplication.timeSinceStartup < eventDeadline) {
                nextRead = EditorApplication.timeSinceStartup + .04;
                GetWindow().Repaint();
                return;
            }
            if (record.dataIndexMatches) {
                record.draws = (int)Field(data, "drawCallCount");
                record.componentInstanceId = (int)Field(data, "componentInstanceID");
                record.shaderInstanceId = (int)Field(data, "shaderInstanceID");
                record.shader = (string)Field(data, "shaderName");
                record.pass = (string)Field(data, "passName");
                record.lightMode = (string)Field(data, "passLightMode");
                record.passIndex = (int)Field(data, "shaderPassIndex");
                record.keywords = (string)Field(data, "shaderKeywords");
                record.batchBreakCause = (int)Field(data, "batchBreakCause");
                record.meshInstanceId = (int)Field(data, "meshInstanceID");
                record.meshInstanceIds = (int[])Field(data, "meshInstanceIDs");
                var component = EditorUtility.InstanceIDToObject(record.componentInstanceId) as Renderer;
                if (component) {
                    record.rendererKey = RenderCaptureData.RendererOf(component).key;
                    record.objectEvidence = record.draws == 1 ? "Renderer associated by Frame Debugger with this single-draw event." : "Associated renderer only; remaining SRP batch members unresolved.";
                }
            }
            pendingIndex++;
            selected = false;
            nextRead = EditorApplication.timeSinceStartup;
        } catch (Exception e) {
            Debug.LogException(e);
            if (pendingPath != null) File.WriteAllText(pendingPath + ".error.txt", e.ToString());
            CompleteExport();
        }
    }

    static void CompleteExport() {
        EditorApplication.update -= ReadSelectedEvent;
        if (pending == null) return;
        RenderCaptureData.WriteJson(pendingPath, pending);
        File.WriteAllText(pendingPath + ".status.txt", "exported " + pendingIndex + "/" + pending.events.Count + " events; valid=" + pending.events.Count(e => e.dataIndexMatches));
        if (UnityEngine.FrameDebugger.enabled) WindowType.GetMethod("ChangeFrameEventLimit", Instance).Invoke(GetWindow(), new object[] { oldLimit });
        pending = null;
    }
}
