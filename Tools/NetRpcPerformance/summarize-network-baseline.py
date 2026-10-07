#!/usr/bin/env python3
"""Summarize raw repeated cases without idle/control subtraction or zero claims."""
import csv
import json
import pathlib
import statistics
import sys

root = pathlib.Path(sys.argv[1])
cases = {}
for path in sorted(root.glob('r*/*-aggregate.json')):
    data = json.loads(path.read_text())
    if not data['Passed']:
        raise RuntimeError(f'Failed case: {path}')
    cases.setdefault(data['Case'], []).append((path, data))
rows = []
for name, entries in sorted(cases.items()):
    for path, data in entries:
        reports = data['Reports']
        host = next(r for r in reports if r['Role'] == 'host')
        clients = [r for r in reports if r['Role'].startswith('client')]
        row = dict(case=name, repeat=path.parent.name,
            allocations=sum(r['AllocatedBytes'] for r in reports),
            host_operations=host['Completed'], client_completions=sum(r['Completed'] for r in clients),
            host_allocations=host['AllocatedBytes'], client_allocations=sum(r['AllocatedBytes'] for r in clients),
            bytes_per_host_operation=data['BytesPerCompletedOperation'],
            summed_role_bytes_per_second=sum(r['AllocatedBytes']/r['Seconds'] for r in reports),
            host_window_seconds=host['Seconds'], host_operations_per_second=host['Completed']/host['Seconds'],
            successful_sends=sum((r['ReliableSends'] or 0)+(r['UdpSends'] or 0) for r in reports),
            sent_application_frame_bytes=sum(r['SentBytes'] or 0 for r in reports),
            received_application_frame_bytes=sum(r['ReceivedBytes'] or 0 for r in reports),
            client_udp_component_frames=sum(r['ComponentFrames'] or 0 for r in clients),
            client_callbacks=sum(r['Callbacks'] for r in clients),
            gen0=sum(r['Gen0'] for r in reports),gen1=sum(r['Gen1'] for r in reports),gen2=sum(r['Gen2'] for r in reports))
        rows.append(row)
with (root/'summary.csv').open('w',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
summary=[]
for name in sorted(cases):
    selected=[r for r in rows if r['case']==name]
    values=[r['bytes_per_host_operation'] for r in selected if r['bytes_per_host_operation'] is not None]
    result=dict(case=name,repetitions=len(selected),
        median_bytes_per_host_operation=statistics.median(values) if values else None,
        min_bytes_per_host_operation=min(values) if values else None,
        max_bytes_per_host_operation=max(values) if values else None,
        median_sum_role_bytes_per_second=statistics.median(r['summed_role_bytes_per_second'] for r in selected),
        median_host_allocations=statistics.median(r['host_allocations'] for r in selected),
        client_callbacks=[r['client_callbacks'] for r in selected],
        gc_collections=[[r['gen0'],r['gen1'],r['gen2']] for r in selected])
    summary.append(result)
(root/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
for result in summary: print(json.dumps(result))
