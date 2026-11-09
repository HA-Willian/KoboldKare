import unittest
from analyze import isolation, percentile, summarize, visibility_diff


class AnalysisTests(unittest.TestCase):
    def test_visibility_uses_observation_not_frustum_and_handles_new_objects(self):
        def r(key, visible, active=True):
            return {"key": key, "isVisibleObserved": visible, "frustumCandidate": True, "active": active, "enabled": True}
        a = {"renderers": [r("same", True), r("old", True), r("in-frustum-only", False)]}
        b = {"renderers": [r("same", True), r("new-runtime", True), r("inactive", True, False), r("in-frustum-only", False)]}
        left, right = visibility_diff(a, b)
        self.assertEqual(set(left), {"old"})
        self.assertEqual(set(right), {"new-runtime"})

    def test_missing_gpu_is_not_zero_and_p95_keeps_spikes(self):
        rows = [{"label": "A", "configuration": "original", "path": "matrix", "direction": "AB", "t": "0", "batches": "10", "frameMs": str(t), "gpuNs": "-1"} for t in (1, 1, 1, 9)]
        result = summarize(rows)[0]
        self.assertIsNone(result["gpuNs_median"])
        self.assertEqual(result["frameMs_median"], 1)
        self.assertGreater(result["frameMs_p95"], 7)

    def test_root_reordering_does_not_invent_visibility_changes(self):
        def r(path):
            return {"id": 42, "key": path, "isVisibleObserved": True, "active": True, "enabled": True}
        a = {"renderers": [r("scene::FarmRegion[5]/Rock[4]")]}
        b = {"renderers": [r("scene::FarmRegion[4]/Rock[4]")]}
        self.assertEqual(visibility_diff(a, b), ({}, {}))

    def test_factorial_interaction_and_reverse_repetitions(self):
        rows = []
        for config, value in (("original", 100), ("no-occlusion", 150), ("no-shadows", 80), ("neither", 110)):
            for pose in "AB":
                for suffix, direction in (("", "AB"), ("-repeat", "BA")):
                    rows.append({"label": f"{config}-{pose}{suffix}", "configuration": config, "path": "matrix", "direction": direction, "t": "0", "batches": str(value)})
        effects = isolation(summarize(rows))["A"]["effects"]["batches"]
        self.assertEqual(effects["occlusion_saving_with_shadows"], 50)
        self.assertEqual(effects["occlusion_saving_without_shadows"], 30)
        self.assertEqual(effects["shadow_cost_with_occlusion"], 20)
        self.assertEqual(effects["shadow_cost_without_occlusion"], 40)
        self.assertEqual(effects["interaction"], -20)


if __name__ == "__main__":
    unittest.main()
