using System.Collections;
using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.Profiling;

public class DayNightCycle : MonoBehaviour {
    private static DayNightCycle instance = null;
    [SerializeField, Min(1)] private int metabolizationsPerFrame = 4;
    private readonly MetabolizationSchedule schedule = new MetabolizationSchedule();
    private Coroutine metabolismRoutine;
    private static readonly ProfilerMarker dispatchMarker = new ProfilerMarker("DayNightCycle.DispatchMetabolism");
    public delegate void MetabolizeAction(float time);

    public static void AddMetabolizationListener(MetabolizeAction action) {
        if (instance == null) {
            return;
        }

        instance.schedule.Add(action, Time.timeAsDouble);
    }
    public static void RemoveMetabolizationListener(MetabolizeAction action) {
        if (instance == null) {
            return;
        }
        instance.schedule.Remove(action);
    }

    private void Awake() {
        if (instance == null || instance == this) {
            instance = this;
        } else {
            Destroy(gameObject);
        }
    }
    private void OnEnable() {
        if (instance != this) {
            return;
        }
        schedule.Restart(Time.timeAsDouble);
        metabolismRoutine = StartCoroutine(MetabolizeOccassionally());
    }
    private void OnDisable() {
        if (metabolismRoutine != null) {
            StopCoroutine(metabolismRoutine);
            metabolismRoutine = null;
        }
    }
    private void OnDestroy() {
        if (instance == this) {
            instance = null;
        }
    }
    private IEnumerator MetabolizeOccassionally() {
        while (isActiveAndEnabled) {
            if (Time.timeScale > 0f) {
                using (dispatchMarker.Auto()) {
                    schedule.Dispatch(Time.timeAsDouble, Mathf.Max(1, metabolizationsPerFrame));
                }
            }
            yield return null;
        }
    }
}

// Registration allocates; steady-state dispatch does not. Each listener keeps its
// two-second step. A long frame postpones ticks rather than applying catch-up bursts,
// matching the old WaitForSeconds loop. Initial simultaneous ticks drain over frames.
internal sealed class MetabolizationSchedule {
    private const float interval = 2f;
    private sealed class Listener {
        public DayNightCycle.MetabolizeAction action;
        public double due;
    }
    private readonly List<Listener> listeners = new List<Listener>();
    private int cursor;
    private double nextCheck = double.PositiveInfinity;

    public void Add(DayNightCycle.MetabolizeAction action, double now) {
        if (action == null) {
            return;
        }
        foreach (DayNightCycle.MetabolizeAction callback in action.GetInvocationList()) {
            listeners.Add(new Listener { action = callback, due = now + interval });
        }
        nextCheck = Math.Min(nextCheck, now + interval);
    }

    public void Remove(DayNightCycle.MetabolizeAction action) {
        if (action == null) {
            return;
        }
        var callbacks = action.GetInvocationList();
        // Delegate subtraction removes the last matching contiguous invocation list.
        for (int i = listeners.Count - callbacks.Length; i >= 0; i--) {
            int j = 0;
            for (; j < callbacks.Length; j++) {
                if (!listeners[i + j].action.Equals(callbacks[j])) {
                    break;
                }
            }
            if (j != callbacks.Length) {
                continue;
            }
            for (int k = 0; k < callbacks.Length; k++) {
                RemoveAt(i);
            }
            nextCheck = double.NegativeInfinity;
            return;
        }
    }

    private void RemoveAt(int index) {
        listeners.RemoveAt(index);
        if (index < cursor) {
            cursor--;
        }
    }

    public void Restart(double now) {
        foreach (var listener in listeners) {
            listener.due = now + interval;
        }
        nextCheck = listeners.Count == 0 ? double.PositiveInfinity : now + interval;
    }

    public void Dispatch(double now, int budget) {
        if (now < nextCheck || budget <= 0) {
            return;
        }
        int remaining = listeners.Count;
        int invoked = 0;
        while (remaining-- > 0 && listeners.Count > 0 && invoked < budget) {
            if (cursor >= listeners.Count) {
                cursor = 0;
            }
            var listener = listeners[cursor++];
            if (listener.action.Target is UnityEngine.Object target && target == null) {
                RemoveAt(cursor - 1);
                continue;
            }
            if (now < listener.due) {
                continue;
            }
            listener.due = now + interval;
            invoked++;
            try {
                listener.action(interval);
            } catch (Exception exception) {
                Debug.LogException(exception);
            }
        }
        if (invoked >= budget) {
            // There may be more due listeners. Resume at the cursor next frame,
            // without rescanning the entire population after every small batch.
            nextCheck = now;
            return;
        }
        nextCheck = double.PositiveInfinity;
        foreach (var listener in listeners) {
            nextCheck = Math.Min(nextCheck, listener.due);
        }
    }
}
