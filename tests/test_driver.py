"""Offline contract tests for the optional LLM caller; no API usage or credentials."""
import contextlib
import importlib.util
import io
import json
import os
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("rmm", Path(__file__).parents[1] / "tools" / "rmm.py")
rmm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rmm)


class DriverTests(unittest.TestCase):
    def test_env_loads_literals_and_quoted_values(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
            path = Path(directory) / ".env"
            path.write_text('# comment\nOPENROUTER_API_KEY="example=key"\nRMM_URL=https://localhost:8443\nRMM_API_KEY=\'literal-$NOT_EXPANDED\'\n', encoding="utf-8")
            rmm.load_env(path)
            self.assertEqual(os.environ["OPENROUTER_API_KEY"], "example=key")
            self.assertEqual(os.environ["RMM_API_KEY"], "literal-$NOT_EXPANDED")
            self.assertEqual(os.environ["RMM_URL"], "https://localhost:8443")

    def test_env_preserves_exported_values_and_ignores_unknown_keys(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"OPENROUTER_API_KEY": "existing"}, clear=True):
            path = Path(directory) / ".env"
            path.write_text('OPENROUTER_API_KEY=from-file\nPATH=untrusted\nRMM_CA_FILE=\nmalformed\n', encoding="utf-8")
            rmm.load_env(path)
            self.assertEqual(dict(os.environ), {"OPENROUTER_API_KEY": "existing"})

    def test_missing_env_is_optional(self):
        with tempfile.TemporaryDirectory() as directory:
            rmm.load_env(Path(directory) / "missing")

    def test_openrouter_tool_loop(self):
        answers = [
            {"choices": [{"message": {"role": "assistant", "content": None, "tool_calls": [{"id": "call_1", "type": "function", "function": {"name": "run_diagnostic", "arguments": '{"script":"Get-Process"}'}}]}}]},
            {"choices": [{"message": {"role": "assistant", "content": "Finding supported by abc."}}]},
        ]
        requests = []
        def provider(request, **kwargs):
            self.assertEqual(request.full_url, "https://openrouter.ai/api/v1/chat/completions")
            requests.append(json.loads(request.data))
            return io.BytesIO(json.dumps(answers.pop(0)).encode())
        class Endpoint:
            def execute(self, device, script):
                return {"id": "abc", "status": "succeeded", "result": {"stdout": "observation"}}, 123
        with patch.dict(os.environ, {"OPENROUTER_API_KEY": "offline-test-placeholder"}), patch.object(rmm.urllib.request, "urlopen", provider), contextlib.redirect_stdout(io.StringIO()):
            rmm.ai_driver(Endpoint(), "device", "Investigate")
        self.assertEqual(requests[1]["messages"][-1]["role"], "tool")
        self.assertEqual(requests[1]["messages"][-1]["tool_call_id"], "call_1")
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
        with patch.dict(os.environ, {"OPENROUTER_API_KEY": "", "ANTHROPIC_API_KEY": "offline-test-placeholder", "ANTHROPIC_MODEL": "offline-test-model"}), patch.object(rmm.urllib.request, "urlopen", provider), contextlib.redirect_stdout(io.StringIO()):
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
