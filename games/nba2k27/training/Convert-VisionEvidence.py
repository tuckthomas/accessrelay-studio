#!/usr/bin/env python3
"""Convert explicitly reviewed, overlay-disjoint enriched evidence without erasing provenance."""
import argparse
from pathlib import Path
import sys
from evidence_conversion import convert, write_review_template


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--student-reference", type=Path, required=True)
    parser.add_argument("--review", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--write-review-template", type=Path)
    args = parser.parse_args()
    if args.write_review_template:
        if args.review or args.out: raise ValueError("Review-template generation and conversion are separate steps")
        write_review_template(args.source, args.student_reference, args.write_review_template)
        print("Unapproved review template written. Review the source clips/configuration before setting any confirmation flags.")
        return 0
    if args.review is None or args.out is None: raise ValueError("Conversion requires --review and --out")
    result = convert(args.source, args.student_reference, args.review, args.out)
    print(f"Converted {result['sessionId']}; original captureEvidence retained; no validation/arming evidence supplied.")
    return 0


if __name__ == "__main__":
    try: raise SystemExit(main())
    except (ValueError, OSError, KeyError, TypeError) as error:
        print(f"FAILURE {error}", file=sys.stderr); raise SystemExit(2)
