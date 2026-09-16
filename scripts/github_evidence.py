#!/usr/bin/env python3
"""Export read-only, public GitHub maintenance and visibility evidence.

The exporter deliberately uses unauthenticated GET requests only.  It records
query provenance and represents a failed measurement as ``unknown`` instead of
turning it into a misleading zero.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


API_ROOT = "https://api.github.com"
SHA_PATTERN = re.compile(r"^[0-9a-fA-F]{40}$")


class GitHubRequestError(Exception):
    """A public API request could not produce a usable response."""

    def __init__(self, message: str, status: int | None = None) -> None:
        super().__init__(message)
        self.status = status


def observed(value: Any, definition: str) -> dict[str, Any]:
    return {"status": "observed", "value": value, "definition": definition}


def unknown(reason: str, definition: str) -> dict[str, Any]:
    return {"status": "unknown", "value": None, "reason": reason, "definition": definition}


def parse_timestamp(value: str) -> datetime:
    """Parse an ISO date or timestamp into an aware UTC datetime."""

    if len(value) == 10:
        return datetime.fromisoformat(value).replace(tzinfo=timezone.utc)
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    return parsed.astimezone(timezone.utc)


def format_timestamp(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def in_window(value: str | None, start: datetime, end: datetime) -> bool:
    if not value:
        return False
    try:
        timestamp = parse_timestamp(value)
    except ValueError:
        return False
    return start <= timestamp < end


def _header(headers: dict[str, str], name: str) -> str | None:
    wanted = name.lower()
    return next((value for key, value in headers.items() if key.lower() == wanted), None)


def _next_link(link_header: str | None) -> str | None:
    if not link_header:
        return None
    for part in link_header.split(","):
        match = re.search(r"<([^>]+)>\s*;\s*rel=\"?next\"?", part)
        if match:
            return match.group(1)
    return None


class GitHubClient:
    """Small GitHub REST client with injectable transport for local fixtures."""

    def __init__(
        self,
        transport: Callable[[str], tuple[int, dict[str, str], bytes]] | None = None,
        timeout: int = 20,
    ) -> None:
        self._transport = transport or self._request
        self.timeout = timeout
        self.queries: list[dict[str, Any]] = []

    def _request(self, url: str) -> tuple[int, dict[str, str], bytes]:
        request = Request(
            url,
            method="GET",
            headers={
                "Accept": "application/vnd.github+json",
                "User-Agent": "stockpile-public-evidence-exporter",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )
        try:
            with urlopen(request, timeout=self.timeout) as response:
                return response.status, dict(response.headers.items()), response.read()
        except HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            raise GitHubRequestError(f"HTTP {error.code}: {body[:200]}", error.code) from error
        except URLError as error:
            raise GitHubRequestError(f"network error: {error.reason}") from error

    def get(self, name: str, url: str) -> Any:
        page = len([query for query in self.queries if query["name"] == name]) + 1
        try:
            status, headers, body = self._transport(url)
            if status < 200 or status >= 300:
                raise GitHubRequestError(f"HTTP {status}", status)
            payload = json.loads(body.decode("utf-8"))
            self.queries.append(
                {"name": name, "method": "GET", "url": url, "page": page, "status": "ok", "http_status": status}
            )
            return payload, headers
        except GitHubRequestError as error:
            self.queries.append(
                {
                    "name": name,
                    "method": "GET",
                    "url": url,
                    "page": page,
                    "status": "error",
                    "http_status": error.status,
                    "error": str(error),
                }
            )
            raise
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            self.queries.append(
                {
                    "name": name,
                    "method": "GET",
                    "url": url,
                    "page": page,
                    "status": "error",
                    "http_status": status,
                    "error": f"invalid JSON response: {error}",
                }
            )
            raise GitHubRequestError("invalid JSON response", status) from error

    def paginate(self, name: str, path: str, params: dict[str, str] | None = None) -> tuple[list[Any], str | None]:
        url = f"{API_ROOT}{path}"
        if params:
            url = f"{url}?{urlencode(params)}"
        items: list[Any] = []
        try:
            while url:
                payload, headers = self.get(name, url)
                if isinstance(payload, list):
                    page_items = payload
                elif name == "workflow-runs" and isinstance(payload, dict) and isinstance(payload.get("workflow_runs"), list):
                    # The Actions API wraps runs in a workflow_runs envelope;
                    # other collection endpoints return a bare JSON array.
                    page_items = payload["workflow_runs"]
                else:
                    raise GitHubRequestError("expected a JSON array")
                items.extend(page_items)
                url = _next_link(_header(headers, "Link"))
            return items, None
        except GitHubRequestError as error:
            if self.queries and self.queries[-1]["name"] == name and self.queries[-1]["status"] == "ok":
                self.queries[-1].update({"status": "error", "error": str(error)})
            return [], str(error)


def _repo_name(repo: str) -> tuple[str, str]:
    parts = repo.strip().split("/")
    if len(parts) != 2 or not all(parts):
        raise ValueError("repository must be in OWNER/NAME form")
    return parts[0], parts[1]


def _metric_unknowns(report: dict[str, Any]) -> list[str]:
    unknowns: list[str] = []

    def visit(value: Any, path: str = "") -> None:
        if isinstance(value, dict):
            if value.get("status") == "unknown":
                unknowns.append(f"{path}: {value.get('reason', 'not recorded')}")
            for key, child in value.items():
                visit(child, f"{path}.{key}" if path else key)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                visit(child, f"{path}[{index}]")

    visit(report.get("metrics", {}), "metrics")
    visit(report.get("ci", {}), "ci")
    return unknowns


def collect_report(
    repo: str = "nirzaf/stockpile",
    *,
    as_of: datetime | None = None,
    days: int = 30,
    head_sha: str | None = None,
    client: GitHubClient | None = None,
    clock: Callable[[], datetime] | None = None,
) -> dict[str, Any]:
    """Collect a report without mutating GitHub or the repository."""

    owner, name = _repo_name(repo)
    if days < 0:
        raise ValueError("days must be zero or greater")
    if head_sha and not SHA_PATTERN.fullmatch(head_sha):
        raise ValueError("head-sha must be a full 40-character SHA")

    now = (clock or (lambda: datetime.now(timezone.utc)))().astimezone(timezone.utc)
    end = (as_of or now).astimezone(timezone.utc)
    start = end - timedelta(days=days)
    client = client or GitHubClient()
    repo_path = f"/repos/{owner}/{name}"

    try:
        repository, _ = client.get("repository", f"{API_ROOT}{repo_path}")
        repository_error = None
    except GitHubRequestError as error:
        repository = {}
        repository_error = str(error)

    default_branch = repository.get("default_branch") if isinstance(repository, dict) else None
    resolved_sha = head_sha
    sha_source = "explicit command-line argument" if head_sha else None
    if not resolved_sha and default_branch:
        try:
            branch, _ = client.get("default-branch", f"{API_ROOT}{repo_path}/branches/{default_branch}")
            resolved_sha = branch.get("commit", {}).get("sha")
            sha_source = "public default-branch metadata"
        except GitHubRequestError:
            resolved_sha = None

    contributors, contributors_error = client.paginate("contributors", f"{repo_path}/contributors")
    issues, issues_error = client.paginate(
        "issues",
        f"{repo_path}/issues",
        {"state": "all", "since": format_timestamp(start), "per_page": "100"},
    )
    commits, commits_error = client.paginate(
        "commits",
        f"{repo_path}/commits",
        {"since": format_timestamp(start), "until": format_timestamp(end), "per_page": "100"},
    )

    action_runs: list[dict[str, Any]] = []
    actions_error: str | None = None
    if resolved_sha:
        action_runs, actions_error = client.paginate(
            "workflow-runs",
            f"{repo_path}/actions/runs",
            {"head_sha": resolved_sha, "per_page": "100"},
        )

    # GitHub normally emits one contributor record per identity, but fixture
    # and mirror data can repeat an identity.  Merge by stable numeric ID and
    # fall back to case-insensitive login; never use an email address.
    identities: dict[str, dict[str, Any]] = {}
    bots = 0
    unidentifiable = 0
    duplicate_count = 0
    for contributor in contributors:
        login = contributor.get("login") if isinstance(contributor, dict) else None
        contributor_type = contributor.get("type") if isinstance(contributor, dict) else None
        is_bot = contributor_type == "Bot" or (isinstance(login, str) and login.lower().endswith("[bot]"))
        if is_bot:
            bots += 1
            continue
        stable_id = contributor.get("id") if isinstance(contributor, dict) else None
        key = f"id:{stable_id}" if stable_id is not None else f"login:{str(login).lower()}" if login else None
        if key is None:
            unidentifiable += 1
            continue
        if key in identities:
            duplicate_count += 1
            identities[key]["contributions"] += contributor.get("contributions", 0) or 0
            if login and login not in identities[key]["source_logins"]:
                identities[key]["source_logins"].append(login)
            continue
        identities[key] = {
            "id": stable_id,
            "login": login,
            "contributions": contributor.get("contributions", 0) or 0,
            "source_logins": [login] if login else [],
        }

    issue_items = [item for item in issues if isinstance(item, dict) and not item.get("pull_request")]
    pull_items = [item for item in issues if isinstance(item, dict) and item.get("pull_request")]
    issues_in_window = [item for item in issue_items if in_window(item.get("created_at"), start, end)]
    pulls_in_window = [item for item in pull_items if in_window(item.get("created_at"), start, end)]
    commits_in_window = [
        item
        for item in commits
        if isinstance(item, dict)
        and in_window(
            item.get("commit", {}).get("author", {}).get("date")
            or item.get("commit", {}).get("committer", {}).get("date"),
            start,
            end,
        )
    ]
    exact_runs = [
        run
        for run in action_runs
        if isinstance(run, dict) and run.get("head_sha", "").lower() == (resolved_sha or "").lower()
    ]

    def collection_metric(items: Iterable[Any], error: str | None, definition: str) -> dict[str, Any]:
        return unknown(error or "not recorded", definition) if error else observed(len(list(items)), definition)

    report: dict[str, Any] = {
        "schema_version": 1,
        "generated_at": format_timestamp(now),
        "repository": {
            "requested": repo,
            "full_name": repository.get("full_name") if isinstance(repository, dict) else None,
            "default_branch": default_branch,
            "metadata_status": "observed" if not repository_error else "unknown",
        },
        "observation": {
            "as_of": format_timestamp(end),
            "window": {
                "start": format_timestamp(start),
                "end": format_timestamp(end),
                "boundary": "[start, end)",
                "days": days,
                "timezone": "UTC",
            },
            "date_basis": "created_at for issues and pull requests; commit author date then committer date for commits",
        },
        "definitions": {
            "stars_and_forks": "Public repository visibility signals only; neither is treated as adoption or usage.",
            "contributors": "Public contributors endpoint records with Bot identities excluded from human identity counts.",
            "duplicate_identities": "Repeated contributor records collapsed by numeric GitHub ID, then case-insensitive login.",
            "exact_sha_ci": "Workflow runs whose head_sha exactly equals the selected full commit SHA; branch status is not substituted.",
            "unknown_vs_zero": "A successful empty collection is observed zero; a failed or unusable query is unknown with a null value.",
        },
        "metrics": {
            "stargazers": observed(repository.get("stargazers_count"), "repository stargazers_count; visibility proxy only")
            if not repository_error and "stargazers_count" in repository
            else unknown(repository_error or "field not present", "repository stargazers_count; visibility proxy only"),
            "forks": observed(repository.get("forks_count"), "repository forks_count; visibility proxy only")
            if not repository_error and "forks_count" in repository
            else unknown(repository_error or "field not present", "repository forks_count; visibility proxy only"),
            "human_contributors": collection_metric(
                identities.values(), contributors_error, "contributors endpoint after Bot exclusion and duplicate collapse"
            ),
            "bots_excluded": collection_metric(
                range(bots), contributors_error, "contributors endpoint records classified as Bot or login ending [bot]"
            ),
            "duplicate_identities_collapsed": collection_metric(
                range(duplicate_count), contributors_error, "duplicate contributor records removed during identity merge"
            ),
            "unidentifiable_contributors": collection_metric(
                range(unidentifiable), contributors_error, "contributor records without a stable ID or readable login"
            ),
            "issues_created_in_window": collection_metric(
                issues_in_window, issues_error, "issues endpoint records with created_at in the UTC observation window"
            ),
            "pull_requests_created_in_window": collection_metric(
                pulls_in_window, issues_error, "issues endpoint records with pull_request marker and created_at in the UTC observation window"
            ),
            "commits_in_window": collection_metric(
                commits_in_window, commits_error, "commits endpoint records in the UTC observation window"
            ),
        },
        "contributors": {
            "identities": list(identities.values()) if not contributors_error else None,
            "status": "observed" if not contributors_error else "unknown",
            "error": contributors_error,
        },
        "ci": {
            "head_sha": observed(resolved_sha, sha_source or "selected exact commit SHA")
            if resolved_sha
            else unknown("no explicit SHA and default branch SHA could not be read", "full commit SHA used for exact CI matching"),
            "workflow_runs": observed(exact_runs, "public workflow runs filtered to exact head_sha")
            if resolved_sha and not actions_error
            else unknown(actions_error or "exact SHA is unknown", "public workflow runs filtered to exact head_sha"),
            "matching_run_count": observed(len(exact_runs), "count of workflow runs with exact head_sha")
            if resolved_sha and not actions_error
            else unknown(actions_error or "exact SHA is unknown", "count of workflow runs with exact head_sha"),
        },
        "queries": client.queries,
    }
    if repository_error:
        report["repository"]["error"] = repository_error
    report["unknowns"] = _metric_unknowns(report)
    return report


def render_markdown(report: dict[str, Any]) -> str:
    """Render a compact human-readable report without inventing missing values."""

    def value(path: tuple[str, ...]) -> str:
        item: Any = report
        for key in path:
            item = item.get(key, {}) if isinstance(item, dict) else {}
        if isinstance(item, dict) and item.get("status") == "unknown":
            return "unknown"
        return str(item.get("value", item)) if isinstance(item, dict) else str(item)

    repository = report["repository"].get("full_name") or report["repository"]["requested"]
    window = report["observation"]["window"]
    lines = [
        f"# Public GitHub evidence — {repository}",
        "",
        f"Generated: `{report['generated_at']}`  ",
        f"As of: `{report['observation']['as_of']}`; window: `{window['start']}` to `{window['end']}` ({window['boundary']}, UTC)",
        "",
        "## Measurements",
        "",
        "| Measure | Value |",
        "|---|---:|",
        f"| Stars (visibility proxy) | {value(('metrics', 'stargazers'))} |",
        f"| Forks (visibility proxy) | {value(('metrics', 'forks'))} |",
        f"| Human contributors | {value(('metrics', 'human_contributors'))} |",
        f"| Bots excluded | {value(('metrics', 'bots_excluded'))} |",
        f"| Duplicate identities collapsed | {value(('metrics', 'duplicate_identities_collapsed'))} |",
        f"| Issues created in window | {value(('metrics', 'issues_created_in_window'))} |",
        f"| Pull requests created in window | {value(('metrics', 'pull_requests_created_in_window'))} |",
        f"| Commits in window | {value(('metrics', 'commits_in_window'))} |",
        f"| Exact-SHA CI runs | {value(('ci', 'matching_run_count'))} |",
        "",
        "Stars and forks are recorded as public visibility signals, not adoption. Unknown values indicate a failed or unavailable query; they are not zero.",
        "",
        f"Exact CI SHA: `{value(('ci', 'head_sha'))}`",
        "",
        "## Unknowns",
        "",
    ]
    lines.extend(f"- {item}" for item in report.get("unknowns", []) or ["none recorded"])
    lines.extend(["", "## Query provenance", "", f"- Requests recorded: {len(report.get('queries', []))}"])
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="nirzaf/stockpile", help="public repository in OWNER/NAME form")
    parser.add_argument("--as-of", help="UTC ISO date or timestamp used as the exclusive window end")
    parser.add_argument("--days", type=int, default=30, help="length of the UTC observation window")
    parser.add_argument("--head-sha", help="full commit SHA for exact CI matching; defaults to the public default branch head")
    parser.add_argument("--format", choices=("json", "markdown"), default="json")
    parser.add_argument("--output", type=Path, help="write the report to this path; otherwise print to stdout")
    args = parser.parse_args(argv)

    try:
        as_of = parse_timestamp(args.as_of) if args.as_of else None
        report = collect_report(args.repo, as_of=as_of, days=args.days, head_sha=args.head_sha)
    except ValueError as error:
        parser.error(str(error))

    content = json.dumps(report, indent=2, sort_keys=False) + "\n" if args.format == "json" else render_markdown(report)
    if args.output:
        args.output.write_text(content, encoding="utf-8")
    else:
        sys.stdout.write(content)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
