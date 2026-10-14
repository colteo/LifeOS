"""QA-001 CLI. Run from the repository root: uv run --no-project python tools/qa-dataset/qa001.py --help"""
import argparse
import json
import os
import sys
from pathlib import Path

from dataset import DEFAULT_REFERENCE_DATE, DEFAULT_SEED, generate, validate
from importer import (STATUS_GUIDANCE, Api, ApiFailure, ImportFailure, binding, check_approval, empty_report,
                      import_dataset, save, whoami)

TOKEN_VARIABLE = "LIFEOS_QA_TOKEN"


def preview(manifest, output):
    expected = manifest["expected"]
    return {"mode": "dry-run", "networkCalls": 0, "manifest": str(output), "sha256": manifest["sha256"],
            "seed": manifest["seed"], "referenceDate": manifest["referenceDate"],
            "window": [manifest["startDate"], manifest["endDate"]], "counts": manifest["counts"],
            "finance": expected["finance"]["kinds"], "budgets": expected["finance"]["months"],
            "nutrition": expected["nutrition"], "journalQuestions": expected["journal"]["questions"],
            "gym": {"workouts": expected["gym"]["workouts"], "status": manifest["gymPolicy"]["importStatus"]}}


def main(argv=None):
    parser = argparse.ArgumentParser(description="QA-001 synthetic 90-day dataset. Offline dry-run by default; "
                                                 "Gym is always offline-only (ADR-008).")
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--reference-date", default=DEFAULT_REFERENCE_DATE,
                        help="The dataset covers the 90 days before this date")
    parser.add_argument("--manifest", type=Path, help="Read and validate an existing manifest instead of generating")
    parser.add_argument("--output", type=Path, default=Path("artifacts/qa001/manifest.json"))
    parser.add_argument("--config", type=Path, help="Local target/user/account/category mapping JSON (never committed)")
    parser.add_argument("--approval-template", type=Path, help="Write an UNAPPROVED binding for operator review")
    parser.add_argument("--whoami", action="store_true", help="Read-only: show the token's user, accounts and categories")
    parser.add_argument("--apply", action="store_true", help="Explicitly request the approved API import")
    parser.add_argument("--approval", type=Path, help="Operator-approved binding with expiry; never generated as approved")
    parser.add_argument("--allow-remote", action="store_true", help="Permit a non-loopback HTTPS origin (still bound by approval)")
    parser.add_argument("--state", type=Path, default=Path("artifacts/qa001/import-state.json"))
    parser.add_argument("--report", type=Path, default=Path("artifacts/qa001/import-report.json"))
    args = parser.parse_args(argv)
    report = None
    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8")) if args.manifest else generate(args.seed, args.reference_date)
        validate(manifest)
        config = json.loads(args.config.read_text(encoding="utf-8")) if args.config else None
        if (args.apply or args.whoami or args.approval_template) and not config:
            raise ImportFailure("--apply, --whoami and --approval-template require --config")
        if args.apply and (args.whoami or args.approval_template or not args.approval):
            raise ImportFailure("--apply requires a separately approved --approval file and no other mode")
        if args.whoami:
            api = Api(config["baseUrl"], os.environ.get(TOKEN_VARIABLE, ""), args.allow_remote)
            print(json.dumps(whoami(api), indent=2, ensure_ascii=False))
            return 0
        if args.apply:
            approval = json.loads(args.approval.read_text(encoding="utf-8"))
            check_approval(manifest, config, approval, args.allow_remote)  # No network before the gate passes.
            api = Api(config["baseUrl"], os.environ.get(TOKEN_VARIABLE, ""), args.allow_remote)
            report = empty_report(manifest, "apply")
            import_dataset(manifest, config, approval, args.state, api, report, args.allow_remote)
            save(args.report, report)
            print(json.dumps(report, indent=2, ensure_ascii=False))
            return 0
        save(args.output, manifest)
        if args.approval_template:
            save(args.approval_template, {**binding(manifest, config, args.allow_remote), "approved": False,
                                          "dedicatedTestAccount": False, "expiresAtUtc": None})
        print(json.dumps(preview(manifest, args.output), indent=2, ensure_ascii=False))
        return 0
    except ApiFailure as error:
        message = f"API request failed (HTTP {error.status or 'none'}): {STATUS_GUIDANCE.get(error.status, STATUS_GUIDANCE[422])}"
        return fail(message, report, args.report)
    except (ImportFailure, ValueError) as error:
        # Our own messages: no server bodies, tokens or profile data.
        return fail(str(error), report, args.report)
    except (KeyError, TypeError, OSError, OverflowError):
        return fail("invalid input, configuration, state or API response shape", report, args.report)


def fail(message, report, report_path):
    if report is not None:
        report["outcome"] = "failed"
        try:
            save(report_path, report)
            message += f" (partial-import report: {report_path})"
        except OSError:
            pass
    print("QA-001 failed: " + message + ". No automatic write retry.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
