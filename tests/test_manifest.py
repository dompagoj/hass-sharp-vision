"""hass_sharp.toml, read by the rules HassSharp's Add external imports it with: hass-sharp's
custom_components/hass_sharp/external.py, loaded by its path from the checkout HASS_SHARP_ROOT names (it uses the
standard library only). Without HASS_SHARP_ROOT the test is skipped."""

import importlib.util
import os
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "hass_sharp.toml"


def external():
    """hass-sharp's external.py, or None when HASS_SHARP_ROOT is unset."""
    checkout = os.environ.get("HASS_SHARP_ROOT")
    if not checkout:
        return None
    spec = importlib.util.spec_from_file_location("hass_sharp_external", Path(checkout) / "custom_components" / "hass_sharp" / "external.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


x = external()


@unittest.skipIf(x is None, "set HASS_SHARP_ROOT to a hass-sharp checkout")
class ManifestTest(unittest.TestCase):
    def test_repository_manifest(self):
        items = x.parse_manifest(MANIFEST.read_bytes())
        self.assertEqual(items, [
            x.Item("library", "Vision.cs", "nodes/Vision.cs",
                   "Face and person detection on a native library (YuNet and YOLOX-nano on tract), and the Detect faces node", "vision"),
            x.Item("library", "PeopleDetector.cs", "nodes/PeopleDetector.cs",
                   "People detector: a device that watches a camera and reports faces and people seen", "people_detector"),
            x.Item("entity", "Front door people", "nodes/front_door_people.json", "A People detector on camera.front_door", None),
        ])

        files = {}
        for item in items:
            with self.subTest(item.path):
                self.assertIsNone(x.check_path(item.path, item.kind))
                path = ROOT / item.path
                self.assertTrue(path.is_file(), f"{item.path} exists")
                files[item.path] = path.read_bytes()
                self.assertLessEqual(len(files[item.path]), x.CAP, f"{item.path} is at most {x.CAP} bytes")

        vision, people, front_door = items
        self.assertIn("public static class FaceDetection", x.library_source(vision, files[vision.path]))
        self.assertIn("public abstract class PeopleDetector : SharpDevice", x.library_source(people, files[people.path]))

        body = x.entity_body(front_door, files[front_door.path])
        self.assertEqual(body["name"], "Front door people")
        self.assertEqual(body["settings"], [])
        graph = body["logic"]["graph"]
        self.assertEqual(graph["name"], "Front door people")
        self.assertEqual(graph["edges"], [])
        [node] = graph["nodes"]
        self.assertEqual(node["kind"], "csharp_device")
        self.assertRegex(node["config"]["source"], r":\s*PeopleDetector\b")
        self.assertEqual(node["config"]["arguments"]["Camera"], {"kind": "literal", "type": "entity_id:camera", "value": "camera.front_door"})
        # No node kind names a library's id, so the definition is the same whatever ids the libraries are stored under.
        self.assertFalse(any(n["kind"].startswith("library:") for n in graph["nodes"]))
        self.assertEqual(x.rewrite_library_kinds(graph, {"vision": "x", "people_detector": "y"}), graph)


if __name__ == "__main__":
    unittest.main()
