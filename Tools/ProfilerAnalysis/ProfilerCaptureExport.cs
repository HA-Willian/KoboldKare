// Copy into Assets/Editor of an empty Unity 2021.3 project and run in batch mode.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

public static class ProfilerCaptureExport {
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private sealed class Sample {
        public double total, self, bytes;
        public int calls;
    }
    private sealed class Frame {
        public int index;
        public double ms, gpuMs;
        public readonly Dictionary<string, Sample> markers = new Dictionary<string, Sample>();
        public readonly Dictionary<string, Sample> edges = new Dictionary<string, Sample>();
    }
    private struct Parent {
        public int index, remaining;
    }
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static string Number(double value) => value.ToString("R", Invariant);
    private static double Percentile(double[] sorted, double fraction) {
        double position = (sorted.Length - 1) * fraction;
        int lower = (int)position;
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
    private static string Stats(IEnumerable<double> values) {
        var sorted = values.OrderBy(x => x).ToArray();
        return string.Join(",", new[] { sorted.Average(), Percentile(sorted, .5), Percentile(sorted, .95), Percentile(sorted, .99), sorted.Max() }.Select(Number));
    }
    public static void Run() {
        try {
            string[] args = Environment.GetCommandLineArgs();
            string input = args[Array.IndexOf(args, "-captureDirectory") + 1];
            string output = args[Array.IndexOf(args, "-exportDirectory") + 1];
            Directory.CreateDirectory(output);
            ProfilerDriver.enabled = false;
            // Runtime-only reader limits; do not change the user's Editor preferences.
            typeof(ProfilerDriver).GetMethod("SetMaxFrameHistoryLength", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new object[] { 2000 });
            UnityEngine.Profiling.Profiler.maxUsedMemory = 1536 * 1024 * 1024;
            foreach (string path in Directory.GetFiles(input, "*.data").OrderBy(x => x)) Export(path, output);
            File.WriteAllText(Path.Combine(output, "complete.txt"), DateTime.UtcNow.ToString("O"));
            EditorApplication.Exit(0);
        } catch (Exception exception) {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }
    private static void Export(string path, string output) {
        ProfilerDriver.ClearAllFrames();
        if (!ProfilerDriver.LoadProfile(path, false)) throw new IOException("Cannot load " + path);
        string stem = Path.GetFileNameWithoutExtension(path);
        int first = ProfilerDriver.firstFrameIndex;
        int last = ProfilerDriver.lastFrameIndex;
        Debug.Log("EXPORT " + stem + " first=" + first + " last=" + last);
        var frames = new List<Frame>();
        var names = new HashSet<string>();
        using (var threads = new StreamWriter(Path.Combine(output, stem + ".threads.csv"))) {
            threads.WriteLine("frame,thread_index,group,name,samples,frame_ms,gpu_ms");
            for (int index = first; index <= last; index++) {
                for (int thread = 0; thread < 512; thread++) {
                    using (var raw = ProfilerDriver.GetRawFrameDataView(index, thread)) {
                        if (!raw.valid) break;
                        threads.WriteLine(string.Join(",", index, thread, Csv(raw.threadGroupName), Csv(raw.threadName), raw.sampleCount, Number(raw.frameTimeMs), Number(raw.frameGpuTimeMs)));
                        if (raw.threadName != "Main Thread") continue;
                        var frame = ReadFrame(raw);
                        if (frame.ms <= 0 || !frame.markers.TryGetValue("PlayerLoop", out var loop) || loop.total <= 0) {
                            Debug.Log("EXCLUDE incomplete main frame " + stem + " index=" + index + " frame_ms=" + frame.ms);
                            continue;
                        }
                        if (frames.Count == 0) ValidateWithHierarchy(frame, thread, output, stem);
                        frames.Add(frame);
                        foreach (string name in frame.markers.Keys) names.Add(name);
                    }
                }
            }
        }
        if (frames.Count == 0) throw new InvalidDataException("No main thread in " + path);
        using (var writer = new StreamWriter(Path.Combine(output, stem + ".edges.csv"))) {
            writer.WriteLine("parent,child,mean_total_ms,mean_calls");
            foreach (var key in frames.SelectMany(frame => frame.edges.Keys).Distinct().OrderBy(x => x)) {
                var parts = key.Split('\t');
                var samples = frames.Select(frame => frame.edges.TryGetValue(key, out var value) ? value : new Sample()).ToArray();
                writer.WriteLine(Csv(parts[0]) + "," + Csv(parts[1]) + "," + Number(samples.Average(x => x.total)) + "," + Number(samples.Average(x => (double)x.calls)));
            }
        }
        using (var writer = new StreamWriter(Path.Combine(output, stem + ".summary.csv"))) {
            writer.WriteLine("marker,frames,present_frames,mean_total_ms,median_total_ms,p95_total_ms,p99_total_ms,max_total_ms,mean_self_ms,median_self_ms,p95_self_ms,p99_self_ms,max_self_ms,mean_calls,mean_inclusive_gc_bytes");
            foreach (string name in names.OrderBy(x => x)) {
                var samples = frames.Select(frame => frame.markers.TryGetValue(name, out var value) ? value : new Sample()).ToArray();
                writer.WriteLine(Csv(name) + "," + frames.Count + "," + samples.Count(x => x.calls > 0) + "," + Stats(samples.Select(x => x.total)) + "," + Stats(samples.Select(x => x.self)) + "," + Number(samples.Average(x => (double)x.calls)) + "," + Number(samples.Average(x => x.bytes)));
            }
        }
        // Keep every frame for reproducible filtering; no automatic trimming of spikes.
        var detailed = names.Where(x => x.StartsWith("Penetration.") || x.StartsWith("Kobold.") || x.StartsWith("Jiggle.") || x.StartsWith("DayNightCycle.") || x.StartsWith("Reagents.") || x.Contains("WaitFor") || x.Contains("Physics") || x.Contains("ScriptRunBehaviour") || x == "PlayerLoop" || x == "GC.Alloc" || x == "GC.Collect" || x == "Animator.Update" || x == "Inl_RenderCameraStack" || x == "FinishFrameRendering").OrderBy(x => x).ToArray();
        using (var writer = new StreamWriter(Path.Combine(output, stem + ".frames.csv"))) {
            writer.WriteLine("frame,frame_ms,gpu_ms," + string.Join(",", detailed.SelectMany(name => new[] { Csv(name + ":total_ms"), Csv(name + ":self_ms"), Csv(name + ":calls"), Csv(name + ":gc_bytes") })));
            foreach (var frame in frames) {
                writer.Write(frame.index + "," + Number(frame.ms) + "," + Number(frame.gpuMs));
                foreach (string name in detailed) {
                    frame.markers.TryGetValue(name, out var sample);
                    sample = sample ?? new Sample();
                    writer.Write("," + Number(sample.total) + "," + Number(sample.self) + "," + sample.calls + "," + Number(sample.bytes));
                }
                writer.WriteLine();
            }
        }
        Debug.Log("EXPORTED " + stem + " main_frames=" + frames.Count + " markers=" + names.Count);
    }
    private static Frame ReadFrame(RawFrameDataView raw) {
        var frame = new Frame { index = raw.frameIndex, ms = raw.frameTimeMs, gpuMs = raw.frameGpuTimeMs };
        int count = raw.sampleCount;
        var parents = new int[count];
        var totals = new double[count];
        var self = new double[count];
        var bytes = new double[count];
        var markerNames = new string[count];
        var stack = new List<Parent>();
        for (int i = 0; i < count; i++) {
            while (stack.Count > 0 && stack[stack.Count - 1].remaining == 0) stack.RemoveAt(stack.Count - 1);
            parents[i] = stack.Count == 0 ? -1 : stack[stack.Count - 1].index;
            if (stack.Count > 0) {
                var parent = stack[stack.Count - 1];
                parent.remaining--;
                stack[stack.Count - 1] = parent;
            }
            totals[i] = self[i] = raw.GetSampleTimeMs(i);
            if (parents[i] >= 0) self[parents[i]] -= totals[i];
            markerNames[i] = raw.GetSampleName(i);
            if (markerNames[i] == "GC.Alloc" && raw.GetSampleMetadataCount(i) > 0) bytes[i] = raw.GetSampleMetadataAsLong(i, 0);
            int children = raw.GetSampleChildrenCount(i);
            if (children > 0) stack.Add(new Parent { index = i, remaining = children });
        }
        for (int i = count - 1; i >= 0; i--) if (parents[i] >= 0) bytes[parents[i]] += bytes[i];
        for (int i = 0; i < count; i++) {
            string name = markerNames[i];
            if (!frame.markers.TryGetValue(name, out var sample)) frame.markers.Add(name, sample = new Sample());
            sample.total += totals[i];
            sample.self += self[i];
            sample.bytes += bytes[i];
            sample.calls++;
            if (name.StartsWith("Penetration.") || name == "GC.Alloc") {
                string edge = (parents[i] >= 0 ? markerNames[parents[i]] : "<root>") + "\t" + name;
                if (!frame.edges.TryGetValue(edge, out var edgeSample)) frame.edges.Add(edge, edgeSample = new Sample());
                edgeSample.total += totals[i];
                edgeSample.calls++;
            }
        }
        return frame;
    }
    private static void ValidateWithHierarchy(Frame frame, int thread, string output, string stem) {
        using (var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(frame.index, thread, HierarchyFrameDataView.ViewModes.Default, HierarchyFrameDataView.columnDontSort, false)) {
            if (!hierarchy.valid) throw new InvalidDataException("Invalid hierarchy for " + stem);
            var native = new Dictionary<string, Sample>();
            var pending = new Stack<int>();
            var children = new List<int>();
            pending.Push(hierarchy.GetRootItemID());
            while (pending.Count > 0) {
                int item = pending.Pop();
                string name = hierarchy.GetItemName(item);
                if (name.StartsWith("Penetration.") || name.StartsWith("Jiggle.") || name.StartsWith("Kobold.")) {
                    if (!native.TryGetValue(name, out var sample)) native.Add(name, sample = new Sample());
                    sample.total += hierarchy.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnTotalTime);
                    sample.self += hierarchy.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnSelfTime);
                    sample.calls += (int)hierarchy.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnCalls);
                }
                children.Clear();
                hierarchy.GetItemChildren(item, children);
                foreach (int child in children) pending.Push(child);
            }
            foreach (var pair in native) {
                if (!frame.markers.TryGetValue(pair.Key, out var raw) || Math.Abs(raw.total - pair.Value.total) > .001 || Math.Abs(raw.self - pair.Value.self) > .001 || raw.calls != pair.Value.calls)
                    throw new InvalidDataException("Raw/hierarchy mismatch for " + stem + " " + pair.Key);
            }
            if (native.Count == 0) throw new InvalidDataException("No instrumented hierarchy samples in " + stem);
            File.WriteAllText(Path.Combine(output, stem + ".validation.txt"), "PASS raw/native hierarchy totals, self times and calls: frame " + frame.index + ", " + native.Count + " markers; tolerance 0.001 ms.\n");
        }
    }
}
