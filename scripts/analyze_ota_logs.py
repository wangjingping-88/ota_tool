#!/usr/bin/env python3
"""分析 EcoLink 四端 OTA 串口日志并给出保守的闭环判定。"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable


TIMESTAMP_RE = re.compile(r"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]")
SID_RE = re.compile(r"\bsid (\d+)\b", re.IGNORECASE)
GENERATED_SID_RE = re.compile(r"down ota generated sid (\d+)", re.IGNORECASE)
MANIFEST_SID_RE = re.compile(
    r"(?:async ota manifest rx|downstream ota manifest tx) sid (\d+)",
    re.IGNORECASE,
)
WEAK_LINK_MIN_MISSING = 3
WEAK_LINK_MIN_SHARE = 0.50


def _read_text(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8-sig", errors="replace").splitlines()


def _timestamp(line: str) -> datetime | None:
    match = TIMESTAMP_RE.match(line)
    if not match:
        return None
    try:
        return datetime.strptime(match.group(1), "%Y-%m-%d %H:%M:%S.%f")
    except ValueError:
        return None


def _role_from_name(path: Path) -> str | None:
    name = path.name.lower()
    for role in ("gateway", "sync", "async", "node"):
        if name.startswith(role):
            return role
    return None


def _node_id_from_name(path: Path) -> int | None:
    match = re.search(
        r"^node[^-]*-(?:0x)?([0-9a-f]{4,8})(?:[-_]|$)",
        path.stem,
        re.IGNORECASE,
    )
    return int(match.group(1), 16) if match else None


def _canonical_node_id(node_id: int) -> str:
    width = 4 if node_id <= 0xFFFF else 8
    return f"0x{node_id:0{width}X}"


def _parse_node_id(value: str | int) -> int:
    if isinstance(value, int):
        return value
    text = value.strip()
    base = 16 if text.lower().startswith("0x") else 10
    return int(text, base)


def _percentile(values: list[int], ratio: float) -> int | None:
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, math.ceil(len(ordered) * ratio) - 1)
    return ordered[index]


def _metric_summary(values: list[int]) -> dict[str, int | None]:
    """返回一组毫秒样本的保守分位统计。"""

    return {
        "count": len(values),
        "min": min(values) if values else None,
        "p50": _percentile(values, 0.50),
        "p95": _percentile(values, 0.95),
        "max": max(values) if values else None,
    }


def _bitmap_offsets(low: int, high: int) -> list[int]:
    """把 Node 输出的低 32 位/高 19 位窗口掩码还原为相对块偏移。"""

    result: list[int] = []
    for index in range(32):
        if low & (1 << index):
            result.append(index)
    for index in range(19):
        if high & (1 << index):
            result.append(index + 32)
    return result


def _discover_session_ids(
    files: list[tuple[Path, str, list[str]]],
) -> list[int]:
    """按开始时间返回日志中所有 OTA 会话，供循环升级逐次分析。"""

    candidates: list[tuple[datetime, int, int]] = []
    fallback_time = datetime.min
    order = 0
    for _path, _role, lines in files:
        for line in lines:
            order += 1
            timestamp = _timestamp(line) or fallback_time
            match = GENERATED_SID_RE.search(line)
            priority = 2
            if not match:
                match = MANIFEST_SID_RE.search(line)
                priority = 1
            if match:
                candidates.append((timestamp, priority * 10_000_000 + order,
                                   int(match.group(1))))
    if not candidates:
        for _path, _role, lines in files:
            for line in lines:
                if "ota" not in line.lower():
                    continue
                match = SID_RE.search(line)
                if match:
                    order += 1
                    candidates.append((_timestamp(line) or fallback_time, order,
                                       int(match.group(1))))
    if not candidates:
        raise ValueError("日志中未找到 OTA session_id")
    candidates.sort(key=lambda item: (item[0], item[1]))
    session_ids: list[int] = []
    for _timestamp_value, _order_value, session_id in candidates:
        if session_id not in session_ids:
            session_ids.append(session_id)
    return session_ids


def _discover_session_id(files: list[tuple[Path, str, list[str]]]) -> int:
    """兼容单次分析调用：返回日志中的最新 OTA 会话。"""

    return _discover_session_ids(files)[-1]


def _session_time_window(
    files: list[tuple[Path, str, list[str]]],
    session_id: int,
) -> tuple[datetime | None, datetime | None]:
    """返回 Async 子任务从 start 到 phase 8 的日志时间窗。"""

    start: datetime | None = None
    end: datetime | None = None
    for _path, role, lines in files:
        if role != "async":
            continue
        for line in lines:
            timestamp = _timestamp(line)
            if timestamp is None:
                continue
            if re.search(
                rf"async ota start sid {session_id}\b",
                line,
                re.IGNORECASE,
            ):
                start = timestamp if start is None else min(start, timestamp)
            if re.search(
                rf"async ota status tx sid {session_id} phase 8\b",
                line,
                re.IGNORECASE,
            ):
                end = timestamp if end is None else max(end, timestamp)
    return start, end


def _make_node(node_id: int | None, file_name: str) -> dict[str, Any]:
    return {
        "node_id": _canonical_node_id(node_id) if node_id is not None else None,
        "files": [file_name],
        "subtask_ids": [],
        "begin": False,
        "package_verified": False,
        "versions": [],
        "resume_phases": [],
        "finished": False,
        "maintenance_count": 0,
        "maintenance_total_ms": [],
        "maintenance_timings": [],
        "fragment_diagnostics": [],
        "rx_path_diagnostics": [],
        "air_rx_events": [],
        "first_pass_stages": [],
        "first_pass_missing_count": 0,
        "missing_share_percent": 0.0,
        "sync_transient_recovery_count": 0,
        "app_sync_lost_count": 0,
        "weak_link_suspected": False,
        "weak_link_reasons": [],
    }


def _add_unique(items: list[Any], value: Any) -> None:
    if value not in items:
        items.append(value)


def analyze_log_directory(
    log_dir: str | Path,
    session_id: int | None = None,
    expected_node_ids: Iterable[str | int] | None = None,
) -> dict[str, Any]:
    """分析一个抓取目录；目录应包含同轮 Gateway/Sync/Async/Node 日志。"""

    root = Path(log_dir).resolve()
    if not root.is_dir():
        raise ValueError(f"日志目录不存在: {root}")

    files: list[tuple[Path, str, list[str]]] = []
    for path in sorted(root.glob("*.log")):
        role = _role_from_name(path)
        if role:
            files.append((path, role, _read_text(path)))
    if not files:
        raise ValueError(f"目录中没有可识别的四端 .log 文件: {root}")

    selected_sid = session_id or _discover_session_id(files)
    session_start, session_end = _session_time_window(files, selected_sid)
    expected_ids = {
        _parse_node_id(value) for value in (expected_node_ids or [])
    }

    role_files: dict[str, list[str]] = defaultdict(list)
    for path, role, _lines in files:
        role_files[role].append(path.name)

    evidence: dict[str, Any] = {
        "gateway_generated": False,
        "gateway_manifest_sent": False,
        "gateway_global_commit": False,
        "async_commit_confirmed": False,
        "commit_confirmed": False,
        "gateway_completed": False,
        "gateway_phase8": False,
        "sync_phase8": False,
        "async_phase8": False,
        "storage_verified": False,
        "storage_verified_detail": None,
    }
    versions: dict[str, Any] = {"old": None, "new": None, "node_type": None}
    target_observations: list[int] = []
    subtask_ids: set[int] = set()
    ready_count = 0
    boot_report_count = 0
    aggregated_finished_count = 0
    failure_events: list[str] = []
    async_discovered_ids: set[int] = set()
    stages_by_first: dict[int, dict[str, Any]] = {}
    maintenance_events: list[dict[str, int]] = []
    node_maintenance_timings: list[dict[str, Any]] = []
    maintenance_response_events: list[dict[str, Any]] = []
    async_fragment_events: list[dict[str, int]] = []
    post_app_fragment_events: list[dict[str, int]] = []
    async_air_tx_events: list[dict[str, int]] = []
    air_protection = {
        "observed": False,
        "guard_count": 0,
        "guard_total_ms": 0,
        "double_pass_stage_count": 0,
        "extra_block_count": 0,
    }
    retries = {
        "prepare_retry": 0,
        "maintenance_repeat": 0,
        "maintenance_retry_total": 0,
        "maintenance_repeat_total": 0,
        "air_tx_fail": 0,
        "query_silent": 0,
        "commit_transmission": 0,
        "sync_tx_failed": 0,
        "ready_after_commit": 0,
    }
    sync_frame_timing = {
        "tx_failure_events": 0,
        "cadence_abnormal_events": 0,
        "frame_head_reject_events": 0,
        "frame_head_missed_events": 0,
        "inferred_missed_frames": 0,
        "frame_head_reanchor_events": 0,
    }
    nodes_by_id: dict[int, dict[str, Any]] = {}
    unknown_nodes: list[dict[str, Any]] = []

    gateway_status_re = re.compile(
        r"down ota sid (\d+) sub (\d+) phase (\d+) (\d+)/(\d+)",
        re.IGNORECASE,
    )
    sync_status_re = re.compile(
        r"downstream ota status rx phase (\d+) reason (\d+) bytes (\d+) "
        r"ready (\d+)/(\d+)",
        re.IGNORECASE,
    )
    async_start_re = re.compile(
        r"async ota start sid (\d+) sub (\d+) type (\d+) size (\d+)",
        re.IGNORECASE,
    )
    storage_verified_re = re.compile(
        r"STORAGE_VERIFIED sid (\d+) external 0x([0-9a-f]+) "
        r"length (\d+) crc 0x([0-9a-f]+)",
        re.IGNORECASE,
    )
    stage_select_re = re.compile(
        r"stage select sid (\d+) first (\d+) count (\d+) offset (\d+) phase (\d+)",
        re.IGNORECASE,
    )
    repair_re = re.compile(
        r"repair staged blocks sid (\d+) first (\d+) count (\d+) round (\d+) "
        r"missing (\d+) passes (\d+)",
        re.IGNORECASE,
    )
    maintenance_done_re = re.compile(
        r"maintenance done sid (\d+) seq (\d+) action (\d+) blocks (\d+)-(\d+) "
        r"elapsed (\d+) repeat_total (\d+) retry (\d+)",
        re.IGNORECASE,
    )
    maintenance_rx_re = re.compile(
        r"(?:maintenance rx|mr) sid (\d+) node 0x([0-9a-f]+)",
        re.IGNORECASE,
    )
    maintenance_response_re = re.compile(
        r"async ota mr sid (\d+) node 0x([0-9a-f]+) seq (\d+) "
        r"action (\d+) result (\d+) latest (\d+) first (\d+) repeat (\d+)",
        re.IGNORECASE,
    )
    node_maintenance_timing_re = re.compile(
        r"node ota mt sid (\d+) seq (\d+) action (\d+) barrier (\d+) "
        r"flash (\d+) recovery (\d+) uplink (\d+) total (\d+) retry (\d+)",
        re.IGNORECASE,
    )
    air_protection_re = re.compile(
        r"air protection sid (\d+) guards (\d+)/(\d+) ms "
        r"double_pass_stages (\d+) extra_blocks (\d+)",
        re.IGNORECASE,
    )
    fragment_diag_re = re.compile(
        r"ota fd sid (\d+) w (\d+) f (\d+) n (\d+) "
        r"x (\d+)/(\d+)/(\d+)/(\d+) "
        r"m (\d+) h (\d+) t (\d+) b (\d+) c (\d+) o (\d+) "
        r"hm([0-9a-f]+)/([0-9a-f]+) "
        r"tm([0-9a-f]+)/([0-9a-f]+) "
        r"cm([0-9a-f]+)/([0-9a-f]+)",
        re.IGNORECASE,
    )
    fragment_diag_legacy_re = re.compile(
        r"ota fd sid (\d+) w (\d+) f (\d+) n (\d+) "
        r"x (\d+)/(\d+)/(\d+)/(\d+) "
        r"h([0-9a-f]+)/([0-9a-f]+) "
        r"t([0-9a-f]+)/([0-9a-f]+) "
        r"c([0-9a-f]+)/([0-9a-f]+)",
        re.IGNORECASE,
    )
    rx_path_diag_re = re.compile(
        r"node ota rxp sid (\d+) w (\d+) s (\d+)\+(\d+) "
        r"cb (\d+)/(\d+)/(\d+) ext (\d+) hi (\d+) "
        r"d (\d+)/(\d+)/(\d+)/(\d+) b (\d+)/(\d+)",
        re.IGNORECASE,
    )
    async_fragment_diag_re = re.compile(
        r"async ota fd sid (\d+) phase (\d+) first (\d+) count (\d+) "
        r"h (\d+)/(\d+)/(\d+) t (\d+)/(\d+)/(\d+) "
        r"q (\d+) gap (\d+)",
        re.IGNORECASE,
    )
    post_app_fragment_re = re.compile(
        r"ota post-app sid (\d+) block (\d+) off (\d+) events (\d+) "
        r"uplink (\d+) target (\d+) gap (\d+)"
        r"(?: node 0x([0-9a-f]+) guards (\d+) "
        r"prev (\d+):(\d+)/(\d+)@(\d+) "
        r"queued (\d+):(\d+)/(\d+) split (\d+))?",
        re.IGNORECASE,
    )
    frame_head_missed_re = re.compile(
        r"sync frame head missed total=\d+ current=(\d+) rf=\d+",
        re.IGNORECASE,
    )
    async_air_tx_re = re.compile(
        r"ota air tx sid (\d+) block (\d+) off (\d+) target (\d+) "
        r"submit (\d+) callback (\d+) result (\d+)",
        re.IGNORECASE,
    )
    node_air_rx_re = re.compile(
        r"node ota arx sid (\d+) b (\d+) o (\d+) base (\d+)",
        re.IGNORECASE,
    )
    node_air_reject_re = re.compile(
        r"node ota arj sid (\d+) b (\d+) o (\d+) rf (\d+) base (\d+) "
        r"w (\d+) p (\d+) s (\d+)",
        re.IGNORECASE,
    )

    for path, role, lines in files:
        current_sid: int | None = None
        current_sync_sid: int | None = None
        active_async = False
        node_id = _node_id_from_name(path) if role == "node" else None
        if role == "node" and node_id is None:
            for line in lines:
                match = re.search(r"\bmod id\b.*\b0x([0-9a-f]+)\b", line,
                                  re.IGNORECASE)
                if match:
                    node_id = int(match.group(1), 16)
                    break
        node = None
        if role == "node":
            if node_id is None:
                node = _make_node(None, path.name)
                unknown_nodes.append(node)
            else:
                node = nodes_by_id.get(node_id)
                if node is None:
                    node = _make_node(node_id, path.name)
                    nodes_by_id[node_id] = node
                elif path.name not in node["files"]:
                    node["files"].append(path.name)

        for line in lines:
            lower = line.lower()
            sid_match = SID_RE.search(line)
            line_sid = int(sid_match.group(1)) if sid_match else None

            if role == "gateway":
                match = GENERATED_SID_RE.search(line)
                if match:
                    current_sid = int(match.group(1))
                    if current_sid == selected_sid:
                        evidence["gateway_generated"] = True
                if current_sid == selected_sid:
                    match = re.search(r"\bold_version\s+([^\s]+)", line,
                                      re.IGNORECASE)
                    if match:
                        versions["old"] = match.group(1)
                    match = re.search(r"\bnew_version\s+([^\s]+)", line,
                                      re.IGNORECASE)
                    if match:
                        versions["new"] = match.group(1)
                    if "down ota manifest" in lower and "sent" in lower:
                        evidence["gateway_manifest_sent"] = True
                if line_sid == selected_sid:
                    if "down ota global commit" in lower:
                        evidence["gateway_global_commit"] = True
                    if "down ota completed" in lower:
                        evidence["gateway_completed"] = True
                    match = gateway_status_re.search(line)
                    if match:
                        subtask_ids.add(int(match.group(2)))
                        phase = int(match.group(3))
                        count = int(match.group(4))
                        total = int(match.group(5))
                        target_observations.append(total)
                        if phase == 8:
                            evidence["gateway_phase8"] = count >= total
                            aggregated_finished_count = max(
                                aggregated_finished_count, count)
                if (line_sid == selected_sid and
                        re.search(r"down ota .*\b(?:failed|timeout)\b", lower)):
                    failure_events.append(line.strip())

            elif role == "sync":
                match = re.search(
                    r"downstream ota manifest tx sid (\d+).*targets (\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match:
                    current_sync_sid = int(match.group(1))
                    if current_sync_sid == selected_sid:
                        target_observations.append(int(match.group(2)))
                if current_sync_sid == selected_sid:
                    match = sync_status_re.search(line)
                    if match:
                        phase = int(match.group(1))
                        reason = int(match.group(2))
                        count = int(match.group(4))
                        total = int(match.group(5))
                        target_observations.append(total)
                        if phase == 5 and reason == 0:
                            ready_count = max(ready_count, count)
                        if phase == 8:
                            evidence["sync_phase8"] = reason == 0 and count >= total
                            aggregated_finished_count = max(
                                aggregated_finished_count, count)
                        if reason != 0:
                            failure_events.append(line.strip())

            elif role == "async":
                match = storage_verified_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    evidence["storage_verified"] = True
                    evidence["storage_verified_detail"] = {
                        "offset": int(match.group(2), 16),
                        "length": int(match.group(3)),
                        "crc32": int(match.group(4), 16),
                    }
                    active_async = True
                match = re.search(
                    r"async ota manifest rx sid (\d+).*targets (\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match and int(match.group(1)) == selected_sid:
                    target_observations.append(int(match.group(2)))
                    active_async = True
                match = async_start_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    subtask_ids.add(int(match.group(2)))
                    active_async = True
                match = stage_select_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    first = int(match.group(2))
                    stages_by_first.setdefault(first, {
                        "first": first,
                        "count": int(match.group(3)),
                        "offset": int(match.group(4)),
                        "first_pass_missing": 0,
                        "last_missing": 0,
                        "max_missing": 0,
                        "repair_rounds": 0,
                        "repair_passes": 1,
                    })
                match = repair_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    first = int(match.group(2))
                    missing = int(match.group(5))
                    round_number = int(match.group(4))
                    stage = stages_by_first.setdefault(first, {
                        "first": first,
                        "count": int(match.group(3)),
                        "offset": first * 80,
                        "first_pass_missing": 0,
                        "last_missing": 0,
                        "max_missing": 0,
                        "repair_rounds": 0,
                        "repair_passes": 1,
                    })
                    if round_number == 1:
                        stage["first_pass_missing"] = missing
                    stage["last_missing"] = missing
                    stage["max_missing"] = max(stage["max_missing"], missing)
                    stage["repair_rounds"] = max(
                        stage["repair_rounds"], round_number)
                    stage["repair_passes"] = max(
                        stage["repair_passes"], int(match.group(6)))
                match = maintenance_done_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    event = {
                        "seq": int(match.group(2)),
                        "action": int(match.group(3)),
                        "first": int(match.group(4)),
                        "last": int(match.group(5)),
                        "elapsed_ms": int(match.group(6)),
                        "repeat_total": int(match.group(7)),
                        "retry": int(match.group(8)),
                    }
                    maintenance_events.append(event)
                    retries["maintenance_retry_total"] += event["retry"]
                    retries["maintenance_repeat_total"] = max(
                        retries["maintenance_repeat_total"],
                        event["repeat_total"],
                    )
                match = maintenance_rx_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    async_discovered_ids.add(int(match.group(2), 16))
                match = maintenance_response_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    maintenance_response_events.append({
                        "node_id": _canonical_node_id(
                            int(match.group(2), 16)),
                        "seq": int(match.group(3)),
                        "action": int(match.group(4)),
                        "result": int(match.group(5)),
                        "latest_submit_to_rx_ms": int(match.group(6)),
                        "first_submit_to_rx_ms": int(match.group(7)),
                        "repeat": int(match.group(8)),
                    })
                match = air_protection_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    air_protection = {
                        "observed": True,
                        "guard_count": int(match.group(2)),
                        "guard_total_ms": int(match.group(3)),
                        "double_pass_stage_count": int(match.group(4)),
                        "extra_block_count": int(match.group(5)),
                    }
                match = async_fragment_diag_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    async_fragment_events.append({
                        "phase": int(match.group(2)),
                        "first": int(match.group(3)),
                        "count": int(match.group(4)),
                        "head_submit": int(match.group(5)),
                        "head_success": int(match.group(6)),
                        "head_fail": int(match.group(7)),
                        "tail_submit": int(match.group(8)),
                        "tail_success": int(match.group(9)),
                        "tail_fail": int(match.group(10)),
                        "queue_fail": int(match.group(11)),
                        "write_request_gap_ms": int(match.group(12)),
                    })
                match = post_app_fragment_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    post_app_fragment_events.append({
                        "block": int(match.group(2)),
                        "fragment_offset": int(match.group(3)),
                        "app_events": int(match.group(4)),
                        "uplink_rf": int(match.group(5)),
                        "target_rf": int(match.group(6)),
                        "gap_us": int(match.group(7)),
                        "boundary_observed": int(
                            match.group(8) is not None),
                        "node_id": (
                            int(match.group(8), 16)
                            if match.group(8) is not None else 0),
                        "guard_frames": (
                            int(match.group(9))
                            if match.group(9) is not None else 0),
                        "previous_valid": (
                            int(match.group(10))
                            if match.group(10) is not None else 0),
                        "previous_block": (
                            int(match.group(11))
                            if match.group(11) is not None else 0),
                        "previous_fragment_offset": (
                            int(match.group(12))
                            if match.group(12) is not None else 0),
                        "previous_target_rf": (
                            int(match.group(13))
                            if match.group(13) is not None else 0),
                        "queued_valid": (
                            int(match.group(14))
                            if match.group(14) is not None else 0),
                        "queued_block": (
                            int(match.group(15))
                            if match.group(15) is not None else 0),
                        "queued_fragment_offset": (
                            int(match.group(16))
                            if match.group(16) is not None else 0),
                        "split_block": (
                            int(match.group(17))
                            if match.group(17) is not None else 0),
                    })
                match = async_air_tx_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    async_air_tx_events.append({
                        "block": int(match.group(2)),
                        "fragment_offset": int(match.group(3)),
                        "target_rf": int(match.group(4)),
                        "submit_rf": int(match.group(5)),
                        "callback_rf": int(match.group(6)),
                        "result": int(match.group(7)),
                    })
                match = re.search(
                    r"async ota boot verified sid (\d+) nodes (\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match and int(match.group(1)) == selected_sid:
                    boot_report_count = max(boot_report_count,
                                            int(match.group(2)))
                match = re.search(
                    r"async ota status tx sid (\d+) phase 8 ret (-?\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match and int(match.group(1)) == selected_sid:
                    evidence["async_phase8"] = int(match.group(2)) == 0
                    active_async = False
                if line_sid == selected_sid:
                    if "async ota prepare retry" in lower:
                        retries["prepare_retry"] += 1
                    if "async ota maintenance repeat" in lower:
                        retries["maintenance_repeat"] += 1
                    if "async ota air tx fail" in lower:
                        retries["air_tx_fail"] += 1
                    if "async ota query silent" in lower:
                        retries["query_silent"] += 1
                    if "async ota commit tx confirmed" in lower:
                        retries["commit_transmission"] += 1
                        evidence["async_commit_confirmed"] = True
                    if "async ota ready after commit" in lower:
                        retries["ready_after_commit"] += 1
                    if (re.search(r"async ota .*\b(?:failed|timeout)\b", lower)
                            and "air tx fail" not in lower):
                        failure_events.append(line.strip())
                if active_async and "sync tx failed" in lower:
                    retries["sync_tx_failed"] += 1
                    sync_frame_timing["tx_failure_events"] += 1
                if active_async and "sync cadence abnormal" in lower:
                    sync_frame_timing["cadence_abnormal_events"] += 1
                if active_async and "sync frame head rejected" in lower:
                    sync_frame_timing["frame_head_reject_events"] += 1
                if active_async and "sync frame head reanchor" in lower:
                    sync_frame_timing["frame_head_reanchor_events"] += 1
                if active_async:
                    match = frame_head_missed_re.search(line)
                    if match:
                        sync_frame_timing["frame_head_missed_events"] += 1
                        sync_frame_timing["inferred_missed_frames"] += int(
                            match.group(1))

            elif role == "node" and node is not None:
                timestamp = _timestamp(line)
                in_session_window = (
                    session_start is not None
                    and timestamp is not None
                    and timestamp >= session_start
                    and (session_end is None or timestamp <= session_end)
                )
                if in_session_window:
                    if "sync transient recovery rx missed" in lower:
                        node["sync_transient_recovery_count"] += 1
                    if "[c]" in lower and "sync_lost" in lower:
                        node["app_sync_lost_count"] += 1
                type_match = re.search(r"node ota type (\d+) version ", line,
                                       re.IGNORECASE)
                if type_match:
                    versions["node_type"] = int(type_match.group(1))
                if line_sid is not None and "node ota" in lower:
                    current_sid = line_sid
                relevant = line_sid == selected_sid or current_sid == selected_sid
                if not relevant:
                    continue
                match = re.search(
                    r"node ota (?:begin|resume|finished) sid \d+ sub (\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match:
                    subtask = int(match.group(1))
                    subtask_ids.add(subtask)
                    _add_unique(node["subtask_ids"], subtask)
                if "node ota begin sid" in lower and line_sid == selected_sid:
                    node["begin"] = True
                if "node ota package verified sid" in lower and line_sid == selected_sid:
                    node["package_verified"] = True
                if in_session_window:
                    match = re.search(
                        r"node ota type (\d+) version ([^\s]+)",
                        line,
                        re.IGNORECASE,
                    )
                    if match:
                        _add_unique(node["versions"], match.group(2))
                    else:
                        match = re.search(
                            r"node ota version ([^\s]+)",
                            line,
                            re.IGNORECASE,
                        )
                        if match:
                            _add_unique(node["versions"], match.group(1))
                match = re.search(
                    r"node ota resume sid \d+ sub \d+ phase (\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match:
                    _add_unique(node["resume_phases"], int(match.group(1)))
                if "node ota finished sid" in lower and line_sid == selected_sid:
                    node["finished"] = True
                match = re.search(
                    r"node ota maintenance sid \d+.* total (\d+)",
                    line,
                    re.IGNORECASE,
                )
                if match:
                    node["maintenance_count"] += 1
                    node["maintenance_total_ms"].append(int(match.group(1)))
                match = node_maintenance_timing_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    timing = {
                        "node_id": node["node_id"],
                        "seq": int(match.group(2)),
                        "action": int(match.group(3)),
                        "barrier_ms": int(match.group(4)),
                        "flash_ms": int(match.group(5)),
                        "recovery_ms": int(match.group(6)),
                        "uplink_ms": int(match.group(7)),
                        "node_total_ms": int(match.group(8)),
                        "tx_retry": int(match.group(9)),
                    }
                    node["maintenance_timings"].append(timing)
                    node_maintenance_timings.append(timing)
                match = fragment_diag_re.search(line)
                new_fragment_format = match is not None
                if match is None:
                    match = fragment_diag_legacy_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    count = int(match.group(4))
                    valid_low = ((1 << min(count, 32)) - 1
                                 if count else 0)
                    high_count = max(0, min(count - 32, 19))
                    valid_high = ((1 << high_count) - 1
                                  if high_count else 0)
                    mask_group = 15 if new_fragment_format else 9
                    head_low = valid_low & ~int(
                        match.group(mask_group), 16)
                    head_high = valid_high & ~int(
                        match.group(mask_group + 1), 16)
                    tail_low = valid_low & ~int(
                        match.group(mask_group + 2), 16)
                    tail_high = valid_high & ~int(
                        match.group(mask_group + 3), 16)
                    crc_low = valid_low & int(
                        match.group(mask_group + 4), 16)
                    crc_high = valid_high & int(
                        match.group(mask_group + 5), 16)
                    both_low = head_low & tail_low
                    both_high = head_high & tail_high
                    head_offsets = _bitmap_offsets(head_low, head_high)
                    tail_offsets = _bitmap_offsets(tail_low, tail_high)
                    crc_offsets = _bitmap_offsets(crc_low, crc_high)
                    node["fragment_diagnostics"].append({
                        "window": int(match.group(2)),
                        "first": int(match.group(3)),
                        "count": count,
                        "head_missing": len(head_offsets),
                        "tail_missing": len(tail_offsets),
                        "both_missing": len(_bitmap_offsets(
                            both_low, both_high)),
                        "crc_failed": len(crc_offsets),
                        "before": int(match.group(5)),
                        "after": int(match.group(6)),
                        "invalid": int(match.group(7)),
                        "duplicate": int(match.group(8)),
                        "classified_missing": (
                            int(match.group(9)) if new_fragment_format
                            else len(set(head_offsets) |
                                     set(tail_offsets) |
                                     set(crc_offsets))),
                        "classified_head_only": (
                            int(match.group(10)) if new_fragment_format
                            else len(set(head_offsets) - set(tail_offsets))),
                        "classified_tail_only": (
                            int(match.group(11)) if new_fragment_format
                            else len(set(tail_offsets) - set(head_offsets))),
                        "classified_both": (
                            int(match.group(12)) if new_fragment_format
                            else len(set(head_offsets) & set(tail_offsets))),
                        "classified_crc": (
                            int(match.group(13)) if new_fragment_format
                            else len(crc_offsets)),
                        "classified_other": (
                            int(match.group(14)) if new_fragment_format
                            else 0),
                        "head_offsets": head_offsets,
                        "tail_offsets": tail_offsets,
                        "crc_offsets": crc_offsets,
                    })
                match = rx_path_diag_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    node["rx_path_diagnostics"].append({
                        "window": int(match.group(2)),
                        "first": int(match.group(3)),
                        "count": int(match.group(4)),
                        "callback": int(match.group(5)),
                        "valid": int(match.group(6)),
                        "invalid": int(match.group(7)),
                        "extender": int(match.group(8)),
                        "header_invalid": int(match.group(9)),
                        "data_seen": int(match.group(10)),
                        "data_accepted": int(match.group(11)),
                        "data_rejected": int(match.group(12)),
                        "data_dispatched": int(match.group(13)),
                        "blocks_received": int(match.group(14)),
                        "blocks_total": int(match.group(15)),
                    })
                match = node_air_rx_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    node["air_rx_events"].append({
                        "block": int(match.group(2)),
                        "fragment_offset": int(match.group(3)),
                        "recv_rf": 0,
                        "base_rf": int(match.group(4)),
                        "accepted": 1,
                        "window_hit": 1,
                        "period_hit": 1,
                        "scan_header": 0,
                    })
                match = node_air_reject_re.search(line)
                if match and int(match.group(1)) == selected_sid:
                    node["air_rx_events"].append({
                        "block": int(match.group(2)),
                        "fragment_offset": int(match.group(3)),
                        "recv_rf": int(match.group(4)),
                        "base_rf": int(match.group(5)),
                        "accepted": 0,
                        "window_hit": int(match.group(6)),
                        "period_hit": int(match.group(7)),
                        "scan_header": int(match.group(8)),
                    })
                if (line_sid == selected_sid and
                        re.search(r"node ota .*\b(?:error|aborted|timeout)\b", lower)):
                    failure_events.append(line.strip())

    session_node_ids = {
        node_id for node_id, node in nodes_by_id.items()
        if (node["begin"] or node["package_verified"] or
            node["subtask_ids"] or node["finished"])
    }
    discovered_ids = session_node_ids | async_discovered_ids
    if not expected_ids:
        expected_ids = set(async_discovered_ids or session_node_ids)
    if expected_ids:
        target_observations.append(len(expected_ids))
    target_values = sorted(set(target_observations))
    target_count = max(target_values) if target_values else 0

    for node_id in sorted(expected_ids):
        if node_id not in nodes_by_id:
            nodes_by_id[node_id] = _make_node(node_id, "")
            nodes_by_id[node_id]["files"] = []

    nodes = [nodes_by_id[node_id] for node_id in sorted(nodes_by_id)]
    nodes.extend(unknown_nodes)
    target_nodes = [nodes_by_id[node_id] for node_id in sorted(expected_ids)]
    if not versions["new"] and target_nodes:
        boot_versions = [
            node["versions"][-1]
            for node in target_nodes
            if node["versions"]
        ]
        if (len(boot_versions) == len(target_nodes)
                and 1 == len(set(boot_versions))):
            versions["new"] = boot_versions[0]
    new_version = versions["new"]
    node_log_count = sum(1 for node in target_nodes if node["files"])
    if new_version:
        node_new_version_count = sum(
            1 for node in target_nodes if new_version in node["versions"])
    else:
        node_new_version_count = sum(
            1 for node in target_nodes if 7 in node["resume_phases"])
    node_finished_count = sum(
        1 for node in target_nodes if node["finished"])
    package_verified_count = sum(
        1 for node in target_nodes if node["package_verified"])

    missing_nodes_by_block: dict[int, set[str]] = defaultdict(set)
    missing_occurrence_total = 0
    for node in target_nodes:
        seen_stage_first: set[int] = set()
        node_missing_blocks: list[int] = []
        for event in node["fragment_diagnostics"]:
            stage_first = event["first"]
            if stage_first in seen_stage_first:
                continue
            seen_stage_first.add(stage_first)
            head_blocks = {
                stage_first + offset for offset in event["head_offsets"]
            }
            tail_blocks = {
                stage_first + offset for offset in event["tail_offsets"]
            }
            crc_blocks = {
                stage_first + offset for offset in event["crc_offsets"]
            }
            missing_blocks = sorted(head_blocks | tail_blocks | crc_blocks)
            node["first_pass_stages"].append({
                "first": stage_first,
                "count": event["count"],
                "missing_blocks": missing_blocks,
                "missing_count": len(missing_blocks),
                "head_missing": len(head_blocks),
                "tail_missing": len(tail_blocks),
                "both_missing": len(head_blocks & tail_blocks),
                "crc_failed": len(crc_blocks),
            })
            node_missing_blocks.extend(missing_blocks)
        node["first_pass_missing_count"] = len(node_missing_blocks)
        missing_occurrence_total += len(node_missing_blocks)
        for block in node_missing_blocks:
            missing_nodes_by_block[block].add(node["node_id"])

    transient_counts = [
        node["sync_transient_recovery_count"] for node in target_nodes
    ]
    max_transient = max(transient_counts, default=0)
    max_transient_count = transient_counts.count(max_transient)
    weak_node_ids: list[str] = []
    for node in target_nodes:
        if missing_occurrence_total:
            node["missing_share_percent"] = round(
                node["first_pass_missing_count"] * 100.0 /
                missing_occurrence_total,
                2,
            )
        if (
            node["first_pass_missing_count"] >= WEAK_LINK_MIN_MISSING
            and node["missing_share_percent"] >=
            WEAK_LINK_MIN_SHARE * 100.0
        ):
            node["weak_link_reasons"].append("MISSING_CONCENTRATION")
        if (
            node["sync_transient_recovery_count"] >= 2
            and node["sync_transient_recovery_count"] == max_transient
            and max_transient_count == 1
        ):
            node["weak_link_reasons"].append("SYNC_TRANSIENT_CONCENTRATION")
        node["weak_link_suspected"] = bool(node["weak_link_reasons"])
        if node["weak_link_suspected"]:
            weak_node_ids.append(node["node_id"])

    broadcast_air_blocks = 0
    directed_air_blocks = 0
    avoidable_healthy_receptions = 0
    for block, node_ids in missing_nodes_by_block.items():
        passes = 1
        for stage in stages_by_first.values():
            stage_end = stage["first"] + stage["count"]
            if stage["first"] <= block < stage_end:
                passes = max(1, stage.get("repair_passes", 1))
                break
        broadcast_air_blocks += passes
        directed_air_blocks += len(node_ids) * passes
        avoidable_healthy_receptions += max(
            0,
            target_count - len(node_ids),
        ) * passes
    unique_missing_blocks = len(missing_nodes_by_block)
    if unique_missing_blocks:
        directional_recommendation = "KEEP_BROADCAST"
    else:
        directional_recommendation = "NO_REPAIR_SAMPLE"
    directional_repair_evaluation = {
        "unique_missing_blocks": unique_missing_blocks,
        "node_block_occurrences": missing_occurrence_total,
        "broadcast_air_blocks": broadcast_air_blocks,
        "directed_air_blocks": directed_air_blocks,
        "estimated_air_block_saving": (
            broadcast_air_blocks - directed_air_blocks
        ),
        "avoidable_healthy_block_receptions": avoidable_healthy_receptions,
        "protocol_change_required": True,
        "recommendation": directional_recommendation,
    }
    node_link_summary = {
        "missing_occurrence_total": missing_occurrence_total,
        "weak_link_node_ids": weak_node_ids,
    }

    fragment_totals = {
        "windows": 0,
        "head_missing": 0,
        "tail_missing": 0,
        "both_missing": 0,
        "crc_failed": 0,
        "before": 0,
        "after": 0,
        "invalid": 0,
        "duplicate": 0,
    }
    fragment_hotspots: dict[str, dict[int, int]] = {
        "head_missing": defaultdict(int),
        "tail_missing": defaultdict(int),
        "crc_failed": defaultdict(int),
    }
    for node in target_nodes:
        for event in node["fragment_diagnostics"]:
            fragment_totals["windows"] += 1
            for key in (
                "head_missing", "tail_missing", "both_missing",
                "crc_failed", "before", "after", "invalid", "duplicate",
            ):
                fragment_totals[key] += event[key]
            for key, offsets_key in (
                ("head_missing", "head_offsets"),
                ("tail_missing", "tail_offsets"),
                ("crc_failed", "crc_offsets"),
            ):
                for relative_offset in event[offsets_key]:
                    block_index = event["first"] + relative_offset
                    fragment_hotspots[key][block_index] += 1

    fragment_summary = {
        **fragment_totals,
        "hotspots": {
            key: [
                {"block": block, "hits": hits}
                for block, hits in sorted(
                    values.items(),
                    key=lambda item: (-item[1], item[0]),
                )
            ]
            for key, values in fragment_hotspots.items()
        },
    }
    rx_path_events: list[dict[str, Any]] = []
    rx_path_first_attempts: list[dict[str, Any]] = []
    for node in target_nodes:
        seen_stages: set[tuple[int, int]] = set()
        for source_event in node["rx_path_diagnostics"]:
            event = {"node_id": node["node_id"], **source_event}
            rx_path_events.append(event)
            stage_key = (event["first"], event["count"])
            if stage_key not in seen_stages:
                rx_path_first_attempts.append(event)
                seen_stages.add(stage_key)
    rx_path_summary = {
        "events": rx_path_events,
        "first_attempts": rx_path_first_attempts,
        "first_attempt_count": len(rx_path_first_attempts),
        "payload_invalid": sum(
            event["invalid"] for event in rx_path_first_attempts
        ),
        "header_invalid": sum(
            event["header_invalid"] for event in rx_path_first_attempts
        ),
        "data_rejected": sum(
            event["data_rejected"] for event in rx_path_first_attempts
        ),
        "dispatch_gap": sum(
            max(0, event["data_seen"] - event["data_dispatched"])
            for event in rx_path_first_attempts
        ),
    }
    async_fragment_summary = {
        "stages": len(async_fragment_events),
        "head_submit": sum(event["head_submit"]
                           for event in async_fragment_events),
        "head_success": sum(event["head_success"]
                            for event in async_fragment_events),
        "head_fail": sum(event["head_fail"]
                         for event in async_fragment_events),
        "tail_submit": sum(event["tail_submit"]
                           for event in async_fragment_events),
        "tail_success": sum(event["tail_success"]
                            for event in async_fragment_events),
        "tail_fail": sum(event["tail_fail"]
                         for event in async_fragment_events),
        "queue_fail": sum(event["queue_fail"]
                          for event in async_fragment_events),
        "events": async_fragment_events,
        "post_app_events": post_app_fragment_events,
    }
    successful_air_tx = [
        event for event in async_air_tx_events if 0 == event["result"]
    ]
    tx_fragment_counts = Counter(
        (event["block"], event["fragment_offset"])
        for event in successful_air_tx
    )
    air_frame_nodes: list[dict[str, Any]] = []
    for node in target_nodes:
        accepted_events = [
            event for event in node["air_rx_events"]
            if 0 != event["accepted"]
        ]
        rx_fragment_counts = Counter(
            (event["block"], event["fragment_offset"])
            for event in accepted_events
        )
        missing_fragments = []
        for identity, tx_count in sorted(tx_fragment_counts.items()):
            missing_count = max(0, tx_count - rx_fragment_counts[identity])
            if missing_count:
                missing_fragments.append({
                    "block": identity[0],
                    "fragment_offset": identity[1],
                    "missing_count": missing_count,
                })
        air_frame_nodes.append({
            "node_id": node["node_id"],
            "accepted_count": len(accepted_events),
            "rejected_count": sum(
                1 for event in node["air_rx_events"]
                if 0 == event["accepted"]
            ),
            "missing_occurrence_count": sum(
                item["missing_count"] for item in missing_fragments
            ),
            "missing_fragments": missing_fragments,
        })
    air_frame_trace = {
        "tx_callback_success": len(successful_air_tx),
        "tx_callback_fail": sum(
            1 for event in async_air_tx_events if 0 != event["result"]
        ),
        "tx_events": async_air_tx_events,
        "nodes": air_frame_nodes,
    }

    latency_values = [event["elapsed_ms"] for event in maintenance_events]
    latency_summary = {
        "min": min(latency_values) if latency_values else None,
        "p50": _percentile(latency_values, 0.50),
        "p95": _percentile(latency_values, 0.95),
        "max": max(latency_values) if latency_values else None,
    }
    by_action: dict[str, dict[str, Any]] = {}
    for action in sorted({event["action"] for event in maintenance_events}):
        values = [event["elapsed_ms"] for event in maintenance_events
                  if event["action"] == action]
        by_action[str(action)] = {
            "count": len(values),
            "p50_ms": _percentile(values, 0.50),
            "p95_ms": _percentile(values, 0.95),
            "max_ms": max(values),
        }

    node_timing_by_key = {
        (event["node_id"], event["seq"], event["action"]): event
        for event in node_maintenance_timings
    }
    maintenance_timelines: list[dict[str, Any]] = []
    for response in maintenance_response_events:
        timeline = dict(response)
        node_timing = node_timing_by_key.get((
            response["node_id"],
            response["seq"],
            response["action"],
        ))
        if node_timing:
            timeline.update({
                key: value for key, value in node_timing.items()
                if key not in ("node_id", "seq", "action")
            })
        maintenance_timelines.append(timeline)

    segment_fields = (
        "barrier_ms",
        "flash_ms",
        "recovery_ms",
        "uplink_ms",
        "node_total_ms",
        "latest_submit_to_rx_ms",
        "first_submit_to_rx_ms",
    )
    maintenance_segments = {
        field: _metric_summary([
            int(event[field])
            for event in (node_maintenance_timings +
                          maintenance_response_events)
            if field in event
        ])
        for field in segment_fields
    }
    evidence["commit_confirmed"] = (
        evidence["gateway_global_commit"]
        or evidence["async_commit_confirmed"]
    )

    blocking_reasons: list[str] = []
    if target_count <= 0:
        blocking_reasons.append("TARGET_COUNT_UNKNOWN")
    if len(target_values) > 1:
        blocking_reasons.append("TARGET_COUNT_MISMATCH")
    if len(subtask_ids) != 1:
        blocking_reasons.append("SUBTASK_ID_NOT_UNIQUE")
    if not evidence["gateway_generated"]:
        blocking_reasons.append("GATEWAY_SESSION_NOT_FOUND")
    if not evidence["gateway_manifest_sent"]:
        blocking_reasons.append("GATEWAY_MANIFEST_NOT_SENT")
    if not versions["new"]:
        blocking_reasons.append("NEW_VERSION_UNKNOWN")
    if ready_count < target_count:
        blocking_reasons.append("READY_INCOMPLETE")
    if boot_report_count < target_count:
        blocking_reasons.append("BOOT_REPORT_INCOMPLETE")
    if node_log_count < target_count:
        blocking_reasons.append("NODE_LOG_INCOMPLETE")
    if node_new_version_count < target_count:
        blocking_reasons.append("NODE_NEW_VERSION_INCOMPLETE")
    if node_finished_count < target_count:
        blocking_reasons.append("NODE_FINISHED_INCOMPLETE")
    if not evidence["commit_confirmed"]:
        blocking_reasons.append("GLOBAL_COMMIT_NOT_FOUND")
    if not evidence["gateway_completed"]:
        blocking_reasons.append("GATEWAY_NOT_COMPLETED")
    if not evidence["gateway_phase8"]:
        blocking_reasons.append("GATEWAY_PHASE8_INCOMPLETE")
    if not evidence["sync_phase8"]:
        blocking_reasons.append("SYNC_PHASE8_INCOMPLETE")
    if not evidence["async_phase8"]:
        blocking_reasons.append("ASYNC_PHASE8_INCOMPLETE")
    if aggregated_finished_count < target_count:
        blocking_reasons.append("AGGREGATED_FINISHED_INCOMPLETE")
    if failure_events:
        blocking_reasons.append("FATAL_EVENT_FOUND")

    device_upgrade_success = (
        target_count > 0
        and bool(versions["new"])
        and boot_report_count >= target_count
        and node_log_count >= target_count
        and node_new_version_count >= target_count
        and not failure_events
    )
    parent_task_success = (
        target_count > 0
        and evidence["gateway_generated"]
        and evidence["gateway_manifest_sent"]
        and ready_count >= target_count
        and evidence["commit_confirmed"]
        and evidence["gateway_completed"]
        and evidence["gateway_phase8"]
        and evidence["sync_phase8"]
        and evidence["async_phase8"]
        and aggregated_finished_count >= target_count
        and not failure_events
    )
    overall_success = (
        device_upgrade_success
        and parent_task_success
        and node_finished_count >= target_count
        and len(target_values) == 1
        and len(subtask_ids) == 1
    )
    # 外部存储 CRC 是已经成立的事实；之后的 Gateway 超时或人工 CANCEL
    # 只表示完整升级未闭环，不应抹掉存储读回校验结果。
    storage_verification_success = evidence["storage_verified"]

    return {
        "schema_version": 11,
        "log_directory": str(root),
        "session_id": selected_sid,
        "session_window": {
            "start": session_start.isoformat() if session_start else None,
            "end": session_end.isoformat() if session_end else None,
        },
        "subtask_ids": sorted(subtask_ids),
        "versions": versions,
        "files": dict(sorted(role_files.items())),
        "expected_node_ids": [
            _canonical_node_id(node_id) for node_id in sorted(expected_ids)
        ],
        "discovered_node_ids": [
            _canonical_node_id(node_id) for node_id in sorted(discovered_ids)
        ],
        "target_count_observations": target_values,
        "counts": {
            "target": target_count,
            "ready": ready_count,
            "boot_report": boot_report_count,
            "aggregated_finished": aggregated_finished_count,
            "node_logs": node_log_count,
            "node_package_verified": package_verified_count,
            "node_new_version": node_new_version_count,
            "node_finished": node_finished_count,
        },
        "stages": [stages_by_first[first]
                   for first in sorted(stages_by_first)],
        "maintenance": {
            "completed_count": len(maintenance_events),
            "latency_ms": latency_summary,
            "by_action": by_action,
            "events": maintenance_events,
            "segments": maintenance_segments,
            "node_timing_events": node_maintenance_timings,
            "response_events": maintenance_response_events,
            "timelines": maintenance_timelines,
        },
        "fragment_diagnostics": fragment_summary,
        "rx_path_diagnostics": rx_path_summary,
        "air_fragment_tx": async_fragment_summary,
        "air_frame_trace": air_frame_trace,
        "air_protection": air_protection,
        "sync_frame_timing": sync_frame_timing,
        "node_link_summary": node_link_summary,
        "directional_repair_evaluation": directional_repair_evaluation,
        "retries": retries,
        "nodes": nodes,
        "evidence": evidence,
        "failure_events": failure_events,
        "conclusions": {
            "device_upgrade_success": device_upgrade_success,
            "parent_task_success": parent_task_success,
            "overall_success": overall_success,
            "storage_verification_success":
                storage_verification_success,
            "blocking_reasons": blocking_reasons,
        },
    }


def _combine_session_results(
    session_results: list[dict[str, Any]],
) -> dict[str, Any]:
    """把同一批日志中的多个 SID 汇总为循环升级结果。"""

    if not session_results:
        raise ValueError("没有可汇总的 OTA 会话")
    if 1 == len(session_results):
        return session_results[0]

    def sum_count(name: str) -> int:
        return sum(int(item["counts"].get(name, 0))
                   for item in session_results)

    def sum_nested(section: str, name: str) -> int:
        return sum(int(item.get(section, {}).get(name, 0))
                   for item in session_results)

    session_steps: list[dict[str, Any]] = []
    for index, item in enumerate(session_results, start=1):
        conclusions = item["conclusions"]
        versions = item["versions"]
        session_steps.append({
            "index": index,
            "round": (index + 1) // 2,
            "direction": "forward" if index % 2 else "reverse",
            "session_id": item["session_id"],
            "old_version": versions.get("old"),
            "new_version": versions.get("new"),
            "target_count": item["counts"].get("target", 0),
            "device_upgrade_success":
                conclusions.get("device_upgrade_success", False),
            "parent_task_success":
                conclusions.get("parent_task_success", False),
            "overall_success": conclusions.get("overall_success", False),
            "blocking_reasons": conclusions.get("blocking_reasons", []),
        })

    successful_steps = sum(
        1 for item in session_steps if item["overall_success"]
    )
    device_success = all(
        item["conclusions"].get("device_upgrade_success", False)
        for item in session_results
    )
    parent_success = all(
        item["conclusions"].get("parent_task_success", False)
        for item in session_results
    )
    overall_success = successful_steps == len(session_results)

    maintenance_events: list[dict[str, Any]] = []
    maintenance_latency_values: list[int] = []
    for item in session_results:
        session_id = item["session_id"]
        for event in item.get("maintenance", {}).get("events", []):
            tagged = {"session_id": session_id, **event}
            maintenance_events.append(tagged)
            if "elapsed_ms" in event:
                maintenance_latency_values.append(int(event["elapsed_ms"]))

    retry_names = sorted({
        name
        for item in session_results
        for name in item.get("retries", {})
    })
    retries = {
        name: sum_nested("retries", name)
        for name in retry_names
    }
    sync_names = sorted({
        name
        for item in session_results
        for name, value in item.get("sync_frame_timing", {}).items()
        if isinstance(value, (int, float)) and not isinstance(value, bool)
    })
    sync_frame_timing = {
        name: sum_nested("sync_frame_timing", name)
        for name in sync_names
    }

    role_files: dict[str, list[str]] = defaultdict(list)
    for item in session_results:
        for role, names in item.get("files", {}).items():
            for name in names:
                _add_unique(role_files[role], name)

    blocking_reasons = [
        f"SID {item['session_id']}: {reason}"
        for item in session_results
        for reason in item["conclusions"].get("blocking_reasons", [])
    ]
    failure_events = [
        f"SID {item['session_id']}: {event}"
        for item in session_results
        for event in item.get("failure_events", [])
    ]
    weak_link_node_ids = sorted({
        node_id
        for item in session_results
        for node_id in item.get("node_link_summary", {}).get(
            "weak_link_node_ids", [])
    })
    expected_node_ids = sorted({
        node_id
        for item in session_results
        for node_id in item.get("expected_node_ids", [])
    })
    discovered_node_ids = sorted({
        node_id
        for item in session_results
        for node_id in item.get("discovered_node_ids", [])
    })
    node_type_values = {
        item["versions"].get("node_type")
        for item in session_results
        if item["versions"].get("node_type") is not None
    }
    first_versions = session_results[0]["versions"]
    last_versions = session_results[-1]["versions"]
    starts = [
        item.get("session_window", {}).get("start")
        for item in session_results
        if item.get("session_window", {}).get("start")
    ]
    ends = [
        item.get("session_window", {}).get("end")
        for item in session_results
        if item.get("session_window", {}).get("end")
    ]

    return {
        "schema_version": 12,
        "analysis_mode": "cycle",
        "log_directory": session_results[0]["log_directory"],
        "session_id": None,
        "session_ids": [item["session_id"] for item in session_results],
        "session_window": {
            "start": min(starts) if starts else None,
            "end": max(ends) if ends else None,
        },
        "subtask_ids": sorted({
            subtask_id
            for item in session_results
            for subtask_id in item.get("subtask_ids", [])
        }),
        "versions": {
            "old": first_versions.get("old"),
            "new": last_versions.get("new"),
            "node_type": (next(iter(node_type_values))
                          if 1 == len(node_type_values) else None),
            "directions": [
                {
                    "session_id": item["session_id"],
                    "old": item["versions"].get("old"),
                    "new": item["versions"].get("new"),
                }
                for item in session_results
            ],
        },
        "files": dict(sorted(role_files.items())),
        "expected_node_ids": expected_node_ids,
        "discovered_node_ids": discovered_node_ids,
        "target_count_observations": [
            value
            for item in session_results
            for value in item.get("target_count_observations", [])
        ],
        "counts": {
            name: sum_count(name)
            for name in (
                "target",
                "ready",
                "boot_report",
                "aggregated_finished",
                "node_logs",
                "node_package_verified",
                "node_new_version",
                "node_finished",
            )
        },
        "maintenance": {
            "completed_count": len(maintenance_events),
            "latency_ms": _metric_summary(maintenance_latency_values),
            "events": maintenance_events,
        },
        "retries": retries,
        "sync_frame_timing": sync_frame_timing,
        "node_link_summary": {
            "weak_link_node_ids": weak_link_node_ids,
        },
        "failure_events": failure_events,
        "conclusions": {
            "device_upgrade_success": device_success,
            "parent_task_success": parent_success,
            "overall_success": overall_success,
            "storage_verification_success": all(
                item["conclusions"].get(
                    "storage_verification_success", False)
                for item in session_results
            ),
            "blocking_reasons": blocking_reasons,
        },
        "cycle": {
            "session_count": len(session_results),
            "successful_session_count": successful_steps,
            "failed_session_count": len(session_results) - successful_steps,
            "complete_round_count": len(session_results) // 2,
            "has_incomplete_round": bool(len(session_results) % 2),
            "all_success": overall_success,
            "steps": session_steps,
        },
        "sessions": session_results,
    }


def analyze_log_sessions(
    log_dir: str | Path,
    session_id: int | None = None,
    expected_node_ids: Iterable[str | int] | None = None,
) -> dict[str, Any]:
    """自动识别全部 SID；多 SID 按循环升级汇总，单 SID 保持原结构。"""

    root = Path(log_dir).resolve()
    if not root.is_dir():
        raise ValueError(f"日志目录不存在: {root}")
    files: list[tuple[Path, str, list[str]]] = []
    for path in sorted(root.glob("*.log")):
        role = _role_from_name(path)
        if role:
            files.append((path, role, _read_text(path)))
    if not files:
        raise ValueError(f"目录中没有可识别的四端 .log 文件: {root}")

    selected_session_ids = (
        [session_id] if session_id is not None else _discover_session_ids(files)
    )
    expected_ids = list(expected_node_ids or [])
    results = [
        analyze_log_directory(
            root,
            session_id=selected_session_id,
            expected_node_ids=expected_ids,
        )
        for selected_session_id in selected_session_ids
    ]
    return _combine_session_results(results)


def _format_ratio(value: int, total: int) -> str:
    return f"{value}/{total}" if total else f"{value}/?"


def print_human_summary(result: dict[str, Any]) -> None:
    if result.get("analysis_mode") == "cycle":
        cycle = result["cycle"]
        status = "全部成功" if cycle["all_success"] else "存在未通过会话"
        print(f"循环 OTA 日志判定：{status}")
        print(
            "识别 {sessions} 次单次升级，完整轮次 {rounds}，"
            "成功 {success}，失败 {failed}{partial}".format(
                sessions=cycle["session_count"],
                rounds=cycle["complete_round_count"],
                success=cycle["successful_session_count"],
                failed=cycle["failed_session_count"],
                partial="，另有半轮日志" if cycle["has_incomplete_round"] else "",
            )
        )
        for step in cycle["steps"]:
            direction = "正向" if step["direction"] == "forward" else "反向"
            step_status = "成功" if step["overall_success"] else "未通过"
            version = f"{step['old_version']} -> {step['new_version']}"
            print(
                f"第 {step['round']} 轮{direction}：SID {step['session_id']}，"
                f"{version}，目标 {step['target_count']}，{step_status}"
            )
            if step["blocking_reasons"]:
                print("  阻断原因：" + ", ".join(step["blocking_reasons"]))
        maintenance = result["maintenance"]
        latency = maintenance["latency_ms"]
        print(
            "循环汇总：目标 {target}，完成 {finished}，维护事件 {maintenance}，"
            "维护延迟 P50/P95/MAX={p50}/{p95}/{maximum} ms".format(
                target=result["counts"]["target"],
                finished=result["counts"]["node_finished"],
                maintenance=maintenance["completed_count"],
                p50=latency["p50"],
                p95=latency["p95"],
                maximum=latency["max"],
            )
        )
        return

    conclusions = result["conclusions"]
    counts = result["counts"]
    target = counts["target"]
    if conclusions["overall_success"]:
        status = "成功"
    elif conclusions["storage_verification_success"]:
        status = "STORAGE_VERIFIED（仅存储校验通过，非完整 OTA 成功）"
    else:
        status = "未通过"
    print(f"OTA 日志判定：{status}")
    print(f"SID：{result['session_id']}，子任务：{result['subtask_ids']}")
    print(
        "版本：{old} -> {new}，node_type={node_type}".format(
            **result["versions"])
    )
    print(
        "计数：READY {ready}，BOOT_REPORT {boot}，Node FINISHED {finished}，"
        "聚合 FINISHED {aggregated}".format(
            ready=_format_ratio(counts["ready"], target),
            boot=_format_ratio(counts["boot_report"], target),
            finished=_format_ratio(counts["node_finished"], target),
            aggregated=_format_ratio(counts["aggregated_finished"], target),
        )
    )
    print(
        "设备升级：{device}；父任务闭环：{parent}".format(
            device="成功" if conclusions["device_upgrade_success"] else "未确认",
            parent="成功" if conclusions["parent_task_success"] else "未完成",
        )
    )
    if result["stages"]:
        stage_text = ", ".join(
            f"{stage['first']}+{stage['count']}:首轮缺{stage['first_pass_missing']}"
            for stage in result["stages"]
        )
        print(f"Stage：{stage_text}")
    maintenance = result["maintenance"]
    print(
        "维护：完成 {count} 次，延迟 P50/P95/MAX={p50}/{p95}/{maximum} ms，"
        "重复 {repeat} 次".format(
            count=maintenance["completed_count"],
            p50=maintenance["latency_ms"]["p50"],
            p95=maintenance["latency_ms"]["p95"],
            maximum=maintenance["latency_ms"]["max"],
            repeat=result["retries"]["maintenance_repeat"],
        )
    )
    segments = maintenance["segments"]
    if segments["node_total_ms"]["count"]:
        print(
            "维护分段 P95：屏障 {barrier}，Flash {flash}，恢复 {recovery}，"
            "上行槽 {uplink}，Async 首发至收到 {async_rx} ms".format(
                barrier=segments["barrier_ms"]["p95"],
                flash=segments["flash_ms"]["p95"],
                recovery=segments["recovery_ms"]["p95"],
                uplink=segments["uplink_ms"]["p95"],
                async_rx=segments["first_submit_to_rx_ms"]["p95"],
            )
        )
    fragment = result["fragment_diagnostics"]
    if fragment["windows"]:
        print(
            "分片诊断：窗口 {windows}，首片缺失 {head_missing}，"
            "尾片缺失 {tail_missing}，两片全缺 {both_missing}，"
            "CRC 失败 {crc_failed}，越窗前/后 {before}/{after}，"
            "非法 {invalid}，重复 {duplicate}".format(**fragment)
        )
    rx_path = result["rx_path_diagnostics"]
    if rx_path["first_attempt_count"]:
        print(
            "接收路径首检：阶段 {first_attempt_count}，底层无效 "
            "{payload_invalid}，帧头非法 {header_invalid}，DATA 拒绝 "
            "{data_rejected}，识别后未分发 {dispatch_gap}".format(
                **rx_path,
            )
        )
    air_fragment = result["air_fragment_tx"]
    if air_fragment["stages"]:
        print(
            "Async 分片：阶段 {stages}，首片提交/成功/失败 "
            "{head_submit}/{head_success}/{head_fail}，尾片 "
            "{tail_submit}/{tail_success}/{tail_fail}，入帧失败 "
            "{queue_fail}，应用恢复后首片事件 {post_app}".format(
                post_app=len(air_fragment["post_app_events"]),
                **air_fragment,
            )
        )
    air_trace = result["air_frame_trace"]
    if air_trace["tx_events"]:
        node_trace = ", ".join(
            "{node_id}:收{accepted_count} 拒{rejected_count} 缺{missing}".format(
                missing=node["missing_occurrence_count"],
                **node,
            )
            for node in air_trace["nodes"]
        )
        print(
            "逐帧对齐：Async 成功/失败 "
            f"{air_trace['tx_callback_success']}/"
            f"{air_trace['tx_callback_fail']}，{node_trace}"
        )
    protection = result["air_protection"]
    if protection["observed"]:
        print(
            "发送保护：首块保护 {guard_count} 次/{guard_total_ms} ms，"
            "双遍窗口 {double_pass_stage_count} 个，额外块 {extra_block_count} 个".format(
                **protection
            )
        )
    sync_timing = result["sync_frame_timing"]
    print(
        "同步节拍：发送失败 {tx_failure_events}，提交节拍异常 "
        "{cadence_abnormal_events}，帧头拒绝 {frame_head_reject_events}，"
        "推断漏帧 {inferred_missed_frames}（{frame_head_missed_events} 次），"
        "重新锚定 {frame_head_reanchor_events}".format(**sync_timing)
    )
    expected_node_ids = set(result["expected_node_ids"])
    target_nodes = [
        node for node in result["nodes"]
        if node["node_id"] in expected_node_ids
    ]
    if target_nodes:
        node_text = ", ".join(
            "{node_id}:缺{missing}({share:.2f}%) 瞬态{transient} "
            "SYNC_LOST={lost}".format(
                node_id=node["node_id"],
                missing=node["first_pass_missing_count"],
                share=node["missing_share_percent"],
                transient=node["sync_transient_recovery_count"],
                lost=node["app_sync_lost_count"],
            )
            for node in target_nodes
        )
        print(f"分 Node：{node_text}")
    weak_nodes = result["node_link_summary"]["weak_link_node_ids"]
    if weak_nodes:
        print("弱链路提示：" + ", ".join(weak_nodes))
    directional = result["directional_repair_evaluation"]
    if directional["unique_missing_blocks"]:
        print(
            "定向补传评估：广播 {broadcast_air_blocks} 块次，"
            "逐 Node 定向 {directed_air_blocks} 块次，空口节省 "
            "{estimated_air_block_saving}，建议 {recommendation}".format(
                **directional
            )
        )
    if conclusions["blocking_reasons"]:
        print("阻断原因：" + ", ".join(conclusions["blocking_reasons"]))


def _parse_expected_nodes(values: list[str]) -> list[int]:
    result: list[int] = []
    for value in values:
        for item in value.split(","):
            item = item.strip()
            if item:
                result.append(_parse_node_id(item))
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "分析 EcoLink 四端 OTA 日志；自动按全部 SID 解析循环升级，"
            "也可指定单个 SID。"
        )
    )
    parser.add_argument("log_dir", help="包含四端 .log 文件的单次或循环抓取目录")
    parser.add_argument(
        "--session-id",
        type=int,
        help="只分析指定 SID；默认分析日志中的全部 SID",
    )
    parser.add_argument(
        "--expected-node",
        action="append",
        default=[],
        help="期望 Node ID，可重复或逗号分隔，例如 0xFB81,0xFAF2",
    )
    parser.add_argument("--json-out", help="把完整结构化结果写入 JSON 文件")
    args = parser.parse_args(argv)

    try:
        result = analyze_log_sessions(
            args.log_dir,
            session_id=args.session_id,
            expected_node_ids=_parse_expected_nodes(args.expected_node),
        )
    except (OSError, ValueError) as error:
        print(f"日志分析失败：{error}", file=sys.stderr)
        return 3

    print_human_summary(result)
    if args.json_out:
        output = Path(args.json_out).resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(result, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"JSON：{output}")
    return 0 if result["conclusions"]["overall_success"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
