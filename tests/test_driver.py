"""Offline contract tests for the optional LLM caller; no API usage or credentials."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("rmm", Path(__file__).parents[1] / "tools" / "rmm.py")
rmm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rmm)


class DriverTests(unittest.TestCase):
    def test_llm_tool_result_is_returned_as_untrusted_data(self):
        answers = [
            {"content": [{"type": "tool_use", "id": "call_1", "name": "run_diagnostic", "input": {"script": "Get-Process | Select-Object -First 1"}}]},
            {"content": [{"type": "text", "text": "Investigation complete; execution abc supports the finding."}]},
        ]
        requests = []

        def provider(request, **kwargs):
            requests.append(json.loads(request.data))
            return io.BytesIO(json.dumps(answers.pop(0)).encode())

        class Endpoint:
            calls = []
            def execute(self, device, script):
                self.calls.append((device, script))
                return {"id": "abc", "status": "succeeded", "result": {"stdout": "untrusted endpoint output"}}, 123

        endpoint = Endpoint()
        with patch.dict(os.environ, {"ANTHROPIC_API_KEY": "offline-test-placeholder", "ANTHROPIC_MODEL": "offline-test-model"}), patch.object(rmm.urllib.request, "urlopen", provider), contextlib.redirect_stdout(io.StringIO()):
            rmm.ai_driver(endpoint, "device", "Why is this machine slow?")
        self.assertEqual(endpoint.calls, [("device", "Get-Process | Select-Object -First 1")])
        tool_result = requests[1]["messages"][-1]["content"][0]
        self.assertEqual(tool_result["tool_use_id"], "call_1")
        data = json.loads(tool_result["content"])
        self.assertEqual(data["executionId"], "abc")
        self.assertEqual(data["untrustedEndpointData"]["stdout"], "untrusted endpoint output")

    def test_diagnostic_does_not_interpolate_endpoint_supplied_script(self):
        class Endpoint:
            calls = 0
            def execute(self, device, script):
                self.calls += 1
                output = {"FreePhysicalMemory": 10, "TotalVisibleMemorySize": 100} if self.calls == 1 else {"Id": "1; Remove-Item C:\\"}
                return {"status": "succeeded", "result": {"stdout": json.dumps(output)}}, 1
        endpoint = Endpoint()
        with self.assertRaises(ValueError):
            rmm.diagnose(endpoint, "device")
        self.assertEqual(endpoint.calls, 2)


if __name__ == "__main__":
    unittest.main()
