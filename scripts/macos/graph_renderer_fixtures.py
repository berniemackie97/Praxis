#!/usr/bin/env python3
"""Create repeatable synthetic graph cases for renderer comparisons."""

import argparse
import json
import math
from pathlib import Path

KINDS = ("person", "project", "concept", "document", "event")
RELATIONS = ("part_of", "references", "uses", "related_to")
STATES = ("owner_confirmed", "documented", "suggested", "conflicting")


def node_id(index):
    return f"n{index:04d}"


def make_case(name):
    count, groups = {
        "sparse": (32, 4),
        "connected": (200, 1),
        "clustered": (800, 8),
        "high_degree": (1500, 1),
    }[name]
    nodes = []
    for index in range(count):
        group = min(groups - 1, index * groups // count)
        phase = 2 * math.pi * ((index * 137) % count) / count
        radius = 35 + (index * 47) % 80
        center = 2 * math.pi * group / groups
        x = round(500 + 320 * math.cos(center) + radius * math.cos(phase), 2)
        y = round(500 + 320 * math.sin(center) + radius * math.sin(phase), 2)
        nodes.append({"id": node_id(index), "label": f"Item {index:04d}",
                      "kind": KINDS[index % len(KINDS)], "group": group,
                      "seedX": x, "seedY": y})

    edges = []
    seen = set()

    def connect(source, target):
        if source == target or not (0 <= source < count and 0 <= target < count):
            raise ValueError("Invalid fixture edge")
        pair = (source, target)
        if pair in seen:
            raise ValueError(f"Duplicate fixture edge {pair}")
        seen.add(pair)
        index = len(edges)
        edges.append({"id": f"e{index:05d}", "sourceId": node_id(source),
                      "targetId": node_id(target), "predicate": RELATIONS[index % len(RELATIONS)],
                      "status": STATES[index % len(STATES)]})

    if name == "sparse":
        for index in range(12):
            connect(index * 2, index * 2 + 1)
    elif name == "connected":
        for index in range(count):
            connect(index, (index + 1) % count)
            connect(index, (index + 17) % count)
    elif name == "clustered":
        size = count // groups
        for group in range(groups):
            base = group * size
            for index in range(size):
                connect(base + index, base + (index + 1) % size)
                connect(base + index, base + (index + 7) % size)
        for group in range(groups - 1):
            connect(group * size, (group + 1) * size)
    else:
        for index in range(1, 1200):
            connect(0, index)
        for index in range(1, count - 1):
            connect(index, index + 1)

    degrees = [0] * count
    for source, target in seen:
        degrees[source] += 1
        degrees[target] += 1
    return {"case": name, "schema": 1, "nodes": nodes, "edges": edges,
            "stats": {"nodeCount": len(nodes), "edgeCount": len(edges),
                      "maxDegree": max(degrees)}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New directory for synthetic JSON fixtures")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    for name in ("sparse", "connected", "clustered", "high_degree"):
        destination = args.output / f"{name}.json"
        with destination.open("x", encoding="utf-8") as stream:
            json.dump(make_case(name), stream, separators=(",", ":"), sort_keys=True)
            stream.write("\n")
        print(f"{name}: {destination}")


if __name__ == "__main__":
    main()
