import unittest
from frame_report import confirmed, draw_event, stage


class FrameEvidenceTests(unittest.TestCase):
    def test_clear_data_does_not_become_a_draw(self):
        event = {"type": "ClearAll", "dataAvailable": True, "dataIndexMatches": True, "draws": 20}
        self.assertTrue(confirmed(event))
        self.assertFalse(draw_event(event))

    def test_wrong_event_index_is_not_confirmed(self):
        self.assertFalse(confirmed({"dataAvailable": True, "dataIndexMatches": False}))

    def test_shadow_attribution_comes_from_frame_debugger_path(self):
        event = {"type": "SRPBatch", "info": "UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/MainLightShadow/Shadows.DrawSRPBatcher"}
        self.assertEqual(stage(event), "MainLightShadow")
        self.assertTrue(draw_event(event))


if __name__ == "__main__":
    unittest.main()
