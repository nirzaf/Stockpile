#!/usr/bin/env python3
"""Fixture-backed tests for scripts/github_evidence.py."""

from __future__ import annotations

import json
import datetime
import sys
import unittest
from pathlib import Path
from urllib.parse import parse_qs, urlparse

sys.path.insert(0, str(Path(__file__).parent))
from github_evidence import API_ROOT, GitHubClient, collect_report  # noqa: E402


FIXTURES = Path(__file__).parent / "fixtures" / "github_evidence"
SHA = "a" * 40


def fixture(name: str):
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


class FixtureTransport:
    def __init__(self, *, error_name: str | None = None, empty: bool = False, malformed_branch: bool = False, commits=None):
        self.error_name = error_name
        self.empty = empty
        self.malformed_branch = malformed_branch
        self.commits = commits
        self.commit_queries = []

    def __call__(self, url: str):
        parsed = urlparse(url)
        path = parsed.path
        query = parse_qs(parsed.query)
        if self.error_name and path.endswith("/contributors"):
            return 503, {"Content-Type": "application/json"}, fixture_bytes("error.json")
        if path == "/repos/nirzaf/merconiq":
            return 200, {}, json.dumps(fixture("repository.json")).encode()
        if path.endswith("/branches/master"):
            if self.malformed_branch:
                return 200, {}, json.dumps({"name": "master", "commit": {}}).encode()
            return 200, {}, json.dumps({"name": "master", "commit": {"sha": SHA}}).encode()
        if path.endswith("/contributors"):
            if self.empty:
                return 200, {}, fixture_bytes("empty.json")
            page = query.get("page", ["1"])[0]
            headers = {"Link": f'<{API_ROOT}{path}?page=2>; rel="next"'} if page == "1" else {}
            return 200, headers, fixture_bytes(f"contributors-page-{page}.json")
        if path.endswith("/issues"):
            return 200, {}, fixture_bytes("empty.json" if self.empty else "empty.json")
        if path.endswith("/commits"):
            self.commit_queries.append(query)
            return 200, {}, json.dumps(self.commits or []).encode()
        if path.endswith("/actions/runs"):
            return 200, {}, fixture_bytes("workflow-runs.json" if not self.empty else "empty-runs.json")
        raise AssertionError(f"unhandled fixture URL: {url}")


def fixture_bytes(name: str) -> bytes:
    return (FIXTURES / name).read_bytes()


class GithubEvidenceTests(unittest.TestCase):
    def test_paginates_and_collapses_bots_and_duplicate_identities(self):
        report = collect_report(
            as_of=datetime.datetime.fromisoformat("2026-09-16T00:00:00+00:00"),
            days=30,
            client=GitHubClient(FixtureTransport()),
        )

        self.assertEqual(report["metrics"]["human_contributors"]["value"], 2)
        self.assertEqual(report["metrics"]["bots_excluded"]["value"], 2)
        self.assertEqual(report["metrics"]["duplicate_identities_collapsed"]["value"], 1)
        self.assertEqual(report["contributors"]["identities"][0]["contributions"], 5)
        self.assertEqual(report["ci"]["matching_run_count"]["value"], 1)
        self.assertTrue(any(query["page"] == 2 for query in report["queries"] if query["name"] == "contributors"))

    def test_successful_empty_data_is_observed_zero(self):
        report = collect_report(
            as_of=datetime.datetime.fromisoformat("2026-09-16T00:00:00+00:00"),
            client=GitHubClient(FixtureTransport(empty=True)),
        )

        for name in ("human_contributors", "bots_excluded", "issues_created_in_window", "commits_in_window"):
            self.assertEqual(report["metrics"][name]["status"], "observed")
            self.assertEqual(report["metrics"][name]["value"], 0)
        self.assertEqual(report["ci"]["matching_run_count"]["value"], 0)

    def test_http_error_is_unknown_not_zero(self):
        report = collect_report(client=GitHubClient(FixtureTransport(error_name="contributors")))

        contributors = report["metrics"]["human_contributors"]
        self.assertEqual(contributors["status"], "unknown")
        self.assertIsNone(contributors["value"])
        self.assertIn("HTTP 503", contributors["reason"])
        self.assertTrue(any(item.startswith("metrics.human_contributors:") for item in report["unknowns"]))

    def test_unusable_default_branch_metadata_leaves_exact_sha_unknown(self):
        client = GitHubClient(FixtureTransport(malformed_branch=True))

        report = collect_report(client=client)

        self.assertEqual(report["ci"]["head_sha"]["status"], "unknown")
        branch_query = next(query for query in report["queries"] if query["name"] == "default-branch")
        self.assertEqual(branch_query["status"], "error")
        self.assertIn("unusable default-branch metadata", branch_query["error"])

    def test_commit_window_uses_author_or_committer_date_from_unbounded_query(self):
        transport = FixtureTransport(
            commits=[
                {
                    "sha": "c" * 40,
                    "commit": {
                        "author": {"date": "2026-09-15T12:00:00Z"},
                        "committer": {"date": "2020-01-01T12:00:00Z"},
                    },
                },
                {
                    "sha": "d" * 40,
                    "commit": {
                        "author": {},
                        "committer": {"date": "2026-09-14T12:00:00Z"},
                    },
                },
            ]
        )

        report = collect_report(
            as_of=datetime.datetime.fromisoformat("2026-09-16T00:00:00+00:00"),
            days=30,
            client=GitHubClient(transport),
        )

        self.assertEqual(report["metrics"]["commits_in_window"]["value"], 2)
        self.assertNotIn("since", transport.commit_queries[0])
        self.assertNotIn("until", transport.commit_queries[0])


if __name__ == "__main__":
    unittest.main()
