"""Offline score reader or opt-in loopback Ollama baseline runner."""

import argparse
import json
from pathlib import Path
from time import monotonic
from urllib.request import Request, urlopen

from contract import SYSTEM, load_cases, score, user_prompt


def run_ollama(cases, model, timeout):
    predictions = {}
    usage = {}
    for case in cases:
        payload = {
            "model": model, "stream": False, "format": "json",
            "messages": [{"role": "system", "content": SYSTEM},
                         {"role": "user", "content": user_prompt(case["packet"])}],
            "options": {"temperature": 0, "num_predict": 512},
        }
        request = Request("http://127.0.0.1:11434/api/chat", data=json.dumps(payload).encode(),
                          headers={"Content-Type": "application/json"}, method="POST")
        started = monotonic()
        try:
            with urlopen(request, timeout=timeout) as response:
                result = json.load(response)
            predictions[case["id"]] = json.loads(result["message"]["content"])
            usage[case["id"]] = {
                "prompt_tokens": result.get("prompt_eval_count"),
                "generated_tokens": result.get("eval_count"),
                "wall_seconds": round(monotonic() - started, 3),
            }
        except (OSError, ValueError, KeyError) as exc:
            predictions[case["id"]] = {"error": str(exc)}
            usage[case["id"]] = {"wall_seconds": round(monotonic() - started, 3)}
    return predictions, usage


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=Path, default=Path(__file__).parent / "cases/eval.jsonl")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--predictions", type=Path)
    source.add_argument("--ollama", metavar="MODEL")
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    cases = load_cases(args.cases)
    if args.ollama:
        predictions, usage = run_ollama(cases, args.ollama, args.timeout)
    else:
        usage = {}
        predictions = {row["id"]: row["response"] for row in
                       (json.loads(line) for line in args.predictions.read_text().splitlines() if line.strip())}
    report = score(cases, predictions)
    totals = {key: sum(v.get(key, 0) or 0 for v in usage.values())
              for key in ("prompt_tokens", "generated_tokens", "wall_seconds")}
    print(json.dumps({**{key: value for key, value in report.items() if key != "cases"},
                      "usage_totals": totals}, indent=2))
    if args.output:
        if args.output.exists():
            raise FileExistsError(args.output)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps({"model": args.ollama, "results": report,
                                          "predictions": predictions, "usage": usage,
                                          "usage_totals": totals}, indent=2) + "\n")


if __name__ == "__main__":
    main()
