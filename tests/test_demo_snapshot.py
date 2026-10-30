"""Exercise only the snapshot endpoint; no camera/model imports or hardware."""
import ast
import base64
from pathlib import Path
import threading
import time
import unittest

class Frame:
    def copy(self):
        return self

class Cv:
    @staticmethod
    def imencode(extension, frame):
        return True, b"demo-jpeg"

class SnapshotTests(unittest.TestCase):
    def setUp(self):
        source = Path(__file__).resolve().parents[1] / "csharp_server/perception_server.py"
        tree = ast.parse(source.read_text(encoding="utf-8-sig"))
        node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "endpoint_demo_snapshot")
        node.decorator_list = []
        self.scope = dict(state_lock=threading.Lock(), latest_state={"timestamp":time.time(), "objects":[{"name":"yellow_cube"}]},
                          latest_frame=Frame(), cv2=Cv, time=time, jsonify=lambda value:value)
        exec(compile(ast.Module(body=[node], type_ignores=[]), str(source), "exec"), self.scope)

    def test_frame_and_detections(self):
        result = self.scope["endpoint_demo_snapshot"]()
        self.assertEqual(base64.b64decode(result["frame_jpeg_base64"]), b"demo-jpeg")
        self.assertEqual(result["objects"], self.scope["latest_state"]["objects"])
        self.assertNotIn("frame_jpeg_base64", self.scope["latest_state"])

    def test_stale_frame(self):
        self.scope["latest_state"]["timestamp"] = time.time() - 10
        self.assertEqual(self.scope["endpoint_demo_snapshot"]()[1], 503)

    def test_no_frame(self):
        self.scope["latest_frame"] = None
        self.assertEqual(self.scope["endpoint_demo_snapshot"]()[1], 503)

if __name__ == "__main__":
    unittest.main()
