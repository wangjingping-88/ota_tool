import importlib.util
import sys
import unittest
from pathlib import Path


sys.dont_write_bytecode = True
SCRIPT_PATH = Path(__file__).parents[1] / "scripts" / "analyze_ota_logs.py"
SPEC = importlib.util.spec_from_file_location("analyze_ota_logs", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
ANALYZER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ANALYZER)


def make_session(session_id: int, success: bool, elapsed_ms: int) -> dict:
    target = 2
    completed = target if success else target - 1
    return {
        "schema_version": 11,
        "log_directory": "D:/logs",
        "session_id": session_id,
        "session_window": {
            "start": f"2026-08-21T10:0{session_id}:00",
            "end": f"2026-08-21T10:0{session_id}:30",
        },
        "subtask_ids": [session_id + 100],
        "versions": {
            "old": "v1" if session_id % 2 else "v2",
            "new": "v2" if session_id % 2 else "v1",
            "node_type": 5,
        },
        "files": {"gateway": ["gateway.log"], "node": ["node.log"]},
        "expected_node_ids": ["0x0001", "0x0002"],
        "discovered_node_ids": ["0x0001", "0x0002"],
        "target_count_observations": [target],
        "counts": {
            "target": target,
            "ready": completed,
            "boot_report": completed,
            "aggregated_finished": completed,
            "node_logs": target,
            "node_package_verified": completed,
            "node_new_version": completed,
            "node_finished": completed,
        },
        "maintenance": {
            "completed_count": 1,
            "latency_ms": {"p95": elapsed_ms},
            "events": [{"elapsed_ms": elapsed_ms}],
        },
        "retries": {"maintenance_repeat": 1 if not success else 0},
        "sync_frame_timing": {
            "tx_failure_events": 0 if success else 1,
            "inferred_missed_frames": 0,
        },
        "node_link_summary": {
            "weak_link_node_ids": [] if success else ["0x0002"],
        },
        "failure_events": [] if success else ["timeout"],
        "conclusions": {
            "device_upgrade_success": success,
            "parent_task_success": success,
            "overall_success": success,
            "storage_verification_success": success,
            "blocking_reasons": [] if success else ["GATEWAY_NOT_COMPLETED"],
        },
    }


class AnalyzeOtaLogsTests(unittest.TestCase):
    def test_discovers_all_session_ids_in_chronological_order(self) -> None:
        files = [
            (
                Path("gateway.log"),
                "gateway",
                [
                    "[2026-08-21 10:00:00.000] down ota generated sid 101",
                    "[2026-08-21 10:02:00.000] down ota generated sid 202",
                ],
            ),
            (
                Path("async.log"),
                "async",
                [
                    "[2026-08-21 10:00:01.000] async ota manifest rx sid 101",
                    "[2026-08-21 10:02:01.000] async ota manifest rx sid 202",
                ],
            ),
        ]

        self.assertEqual([101, 202], ANALYZER._discover_session_ids(files))
        self.assertEqual(202, ANALYZER._discover_session_id(files))

    def test_combines_cycle_sessions_and_preserves_failed_step(self) -> None:
        result = ANALYZER._combine_session_results([
            make_session(1, True, 200),
            make_session(2, False, 800),
        ])

        self.assertEqual("cycle", result["analysis_mode"])
        self.assertEqual([1, 2], result["session_ids"])
        self.assertEqual(1, result["cycle"]["complete_round_count"])
        self.assertEqual(1, result["cycle"]["successful_session_count"])
        self.assertEqual(1, result["cycle"]["failed_session_count"])
        self.assertFalse(result["conclusions"]["overall_success"])
        self.assertEqual(4, result["counts"]["target"])
        self.assertEqual(3, result["counts"]["node_finished"])
        self.assertEqual(800, result["maintenance"]["latency_ms"]["p95"])
        self.assertIn(
            "SID 2: GATEWAY_NOT_COMPLETED",
            result["conclusions"]["blocking_reasons"],
        )

    def test_single_session_keeps_backward_compatible_shape(self) -> None:
        session = make_session(9, True, 300)

        self.assertIs(session, ANALYZER._combine_session_results([session]))


if __name__ == "__main__":
    unittest.main()
