using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Profiling;

public class ReactionsDatabase : Database<ScriptableReagentReaction> {
    private static readonly ProfilerMarker reactionsMarker = new ProfilerMarker("Reagents.Reactions");
    private static ReactionIndex index;

    private sealed class IndexedReaction {
        public int order;
        public ScriptableReagentReaction reaction;
    }

    private sealed class ReactionBucket {
        // -1 keeps malformed/zero-coefficient recipes on the original execution path.
        public int firstReactant;
        public readonly List<IndexedReaction> entries = new List<IndexedReaction>();
    }

    private sealed class ReactionIndex {
        public int reactionsRevision;
        public int reagentsRevision;
        public readonly List<ReactionBucket> buckets = new List<ReactionBucket>();
        public readonly List<IndexedReaction> all = new List<IndexedReaction>();
    }

    // Mod loading/unloading invalidates automatically through Database revisions.
    // Call this after editing reactants in place at runtime without adding an asset.
    public static void InvalidateReactionCache() {
        index = null;
    }

    private static ReactionIndex GetIndex() {
        if (index != null && index.reactionsRevision == Revision &&
            index.reagentsRevision == ReagentDatabase.Revision) {
            return index;
        }
        var rebuilt = new ReactionIndex {
            reactionsRevision = Revision,
            reagentsRevision = ReagentDatabase.Revision
        };
        foreach (var pair in instance.assets) {
            var reaction = pair.value[^1].obj;
            int first = -1;
            if (reaction.reactants != null && reaction.reactants.Length > 0 &&
                reaction.reactants[0].reactant != null && reaction.reactants[0].coefficient > 0f) {
                first = (byte)ReagentDatabase.GetID(reaction.reactants[0].reactant);
                foreach (var reactant in reaction.reactants) {
                    if (reactant.coefficient < 0f) {
                        first = -1;
                        break;
                    }
                }
            }
            ReactionBucket bucket = null;
            foreach (var candidate in rebuilt.buckets) {
                if (candidate.firstReactant == first) {
                    bucket = candidate;
                    break;
                }
            }
            if (bucket == null) {
                bucket = new ReactionBucket { firstReactant = first };
                rebuilt.buckets.Add(bucket);
            }
            var entry = new IndexedReaction { order = rebuilt.all.Count, reaction = reaction };
            rebuilt.all.Add(entry);
            bucket.entries.Add(entry);
        }
        index = rebuilt;
        return rebuilt;
    }

    private static IndexedReaction FindNext(ReactionIndex snapshot, GenericReagentContainer container, int after) {
        // Negative contents can be authored through OverrideReagent; the old recipe
        // evaluator may then react even with a zero first reactant. Preserve that path.
        foreach (var reagent in container.GetContents().Values) {
            if (reagent.volume < 0f) {
                return after + 1 < snapshot.all.Count ? snapshot.all[after + 1] : null;
            }
        }
        IndexedReaction next = null;
        foreach (var bucket in snapshot.buckets) {
            if (bucket.firstReactant >= 0 && container.GetVolumeOf((byte)bucket.firstReactant) == 0f) {
                continue;
            }
            int low = 0;
            int high = bucket.entries.Count;
            while (low < high) {
                int mid = low + (high - low) / 2;
                if (bucket.entries[mid].order <= after) {
                    low = mid + 1;
                } else {
                    high = mid;
                }
            }
            if (low < bucket.entries.Count && (next == null || bucket.entries[low].order < next.order)) {
                next = bucket.entries[low];
            }
        }
        return next;
    }

    public static void DoReactions(GenericReagentContainer container, byte introducedReactant) {
        using (reactionsMarker.Auto()) {
            var snapshot = GetIndex();
            int after = -1;
            IndexedReaction next;
            // Re-evaluate eligibility after every reaction: products and callbacks can
            // enable a later recipe. Selecting by original order preserves depth-first
            // product reactions, including ready recipes unrelated to the introduced ID.
            while ((next = FindNext(snapshot, container, after)) != null) {
                after = next.order;
                next.reaction.DoReaction(container);
            }
        }
    }
}
