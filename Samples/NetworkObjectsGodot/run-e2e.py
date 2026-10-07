#!/usr/bin/env python3
"""Three actual Godot .NET processes; barriers coordinate timing, never transport object state."""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--visible", action="store_true", help="Use the real desktop for both clients; Host stays headless")
    parser.add_argument("--hold-seconds", type=int, default=0, help="Keep spawned/updated Nodes visible before despawn")
    parser.add_argument("--phase-seconds", type=int, default=0, help="Pause before spawn, before owner change and after despawn")
    parser.add_argument("--client-size", default="880x560", help="Real visible client window size, e.g. 620x440 for a 1280px desktop")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    output = (args.output or root / "results" / datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")).resolve()
    output.mkdir(parents=True, exist_ok=False)
    project = root / "Godot"
    processes = []
    deadline = time.monotonic() + 75 + args.hold_seconds + 3 * args.phase_seconds
    client_width, client_height = map(int, args.client_size.split("x"))

    def start(role, peer, port):
        command = [args.godot, "--path", str(project), "--audio-driver", "Dummy", "--max-fps", "60"]
        if role == "host" or not args.visible:
            command += ["--headless"]
        elif peer == 2:
            command += ["--position", "10,50", "--resolution", args.client_size]
        else:
            command += ["--position", f"{client_width + 30},50", "--resolution", args.client_size]
        command += ["--", "--role", role, "--peer", str(peer), "--port", str(port), "--results", str(output),
                    "--timeout", str(60 + args.hold_seconds + 3 * args.phase_seconds), "--hold-seconds", str(args.hold_seconds),
                    "--phase-seconds", str(args.phase_seconds)]
        log = open(output / f"{role}-{peer}.log", "w")
        child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
        processes.append((child, log, role, peer, command))
        return child

    def wait_file(name):
        path = output / name
        while not path.exists():
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for {name}")
            for child, _, role, peer, _ in processes:
                if child.poll() is not None:
                    raise RuntimeError(f"{role} {peer} exited {child.returncode} before {name}; see its log")
            time.sleep(0.05)
        return json.loads(path.read_text())

    summary = {"passed": False, "topology": "dedicated Godot Host + two actual Godot .NET Clients",
               "transport": "source Package native TCP + UDP", "visibleClients": args.visible,
               "testBarriers": "Files coordinate phase timing only; lifecycle/component state uses network packets"}
    try:
        start("host", 1, 0)
        ready = wait_file("host-ready.json")
        start("client", 2, ready["port"])
        wait_file("client-2-initial.json")
        # This process does not exist until the first client already received both initial snapshots.
        start("client", 3, ready["port"])
        for child, _, role, peer, _ in processes:
            code = child.wait(timeout=max(1, deadline - time.monotonic()))
            if code != 0:
                raise RuntimeError(f"{role} {peer} exited {code}; see its log")
        reports = [json.loads((output / f"{role}-{peer}.json").read_text()) for _, _, role, peer, _ in processes]
        assert len({r["ProcessId"] for r in reports}) == 3, "Expected three distinct actual engine processes"
        for report in reports:
            assert report["Passed"] and report["RealGodot"], report
            assert report["Engine"].startswith("4.7.2"), report["Engine"]
            assert report["Spawned"] == report["Despawning"] == report["Released"] == 2, report
            assert report["InstantiatedPackedScenes"] == 1, report
            assert report["RegistryCleared"] and report["DynamicNodeFreed"] and report["SceneNodeRetained"], report
            assert report["MarshalledCalls"] > 0 and report["MainThreadComponentChanges"] >= 4, report
            assert sorted(s["HealthAtSpawned"] for s in report["InitialSnapshots"]) == [37, 73], report
            assert all(s["GodotMainThread"] for s in report["InitialSnapshots"]), report
        assert reports[1]["ClientWriteDenied"] and reports[2]["ClientWriteDenied"]
        assert reports[0]["InitialSnapshotsBeforePeriodicSync"], "Initial state gate was masked by periodic component updates"
        assert reports[2]["LateJoin"] and reports[2]["DeferredSceneRecovered"] and reports[2]["MissingSceneAttempts"] > 0
        summary.update(passed=True, port=ready["port"], reports=reports)
    except Exception as error:
        summary["error"] = repr(error)
    finally:
        for child, log, _, _, _ in processes:
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
            log.close()
        summary["processes"] = [{"role": role, "peer": peer, "pid": child.pid, "exitCode": child.returncode,
                                  "command": command} for child, _, role, peer, command in processes]
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps({"passed": summary["passed"], "output": str(output), "error": summary.get("error", "")}, indent=2))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
