"""Ask the model about every policy, with a resumable JSONL cache and adaptive concurrency."""
from __future__ import annotations

import asyncio
import json
import os
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from typesafe_sdk import (
    AsyncTypeSafeClient,
    RetryPolicy,
    TypeSafeAPIConnectionError,
    TypeSafeAPIError,
    TypeSafeAPITimeoutError,
    TypeSafeInternalServerError,
    TypeSafeRateLimitError,
)

from .admx import Catalog, Policy
from .modelio import MODEL, build_request, request_sha

BASE_URL = "https://opencode.ai/zen"
CACHE_PATH = Path("build/model_cache.jsonl")
ERRORS_PATH = Path("build/classify_errors.json")
OUTER_ATTEMPTS = 6
COOLDOWN_SECONDS = 15.0
GROWTH_AFTER = 50
# A retry-after beyond this is a usage cap (the free tier answers with ~23 h), not a burst limit.
HALT_AFTER_SECONDS = 600

_TRANSIENT = (TypeSafeInternalServerError, TypeSafeAPIConnectionError, TypeSafeAPITimeoutError)


class Cache:
    def __init__(self, path: Path = CACHE_PATH):
        self.path = path
        self.records: dict[tuple[str, str], dict] = {}
        if path.exists():
            with path.open(encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if line:
                        rec = json.loads(line)
                        self.records[(rec["uid"], rec["request_sha"])] = rec
        path.parent.mkdir(parents=True, exist_ok=True)
        self._fh = path.open("a", encoding="utf-8")

    def get(self, uid: str, sha: str) -> Optional[dict]:
        return self.records.get((uid, sha))

    def put(self, rec: dict) -> None:
        self.records[(rec["uid"], rec["request_sha"])] = rec
        self._fh.write(json.dumps(rec, ensure_ascii=False) + "\n")
        self._fh.flush()

    def close(self) -> None:
        self._fh.close()


class Limiter:
    """Permit gate that halves on rate limiting and grows slowly on sustained success."""

    def __init__(self, start: int, ceiling: int):
        self.permits = max(1, min(start, ceiling))
        self.ceiling = ceiling
        self.active = 0
        self.successes = 0
        self.cooldown_until = 0.0
        self.cond = asyncio.Condition()

    async def acquire(self) -> None:
        while True:
            async with self.cond:
                await self.cond.wait_for(lambda: self.active < self.permits)
                delay = self.cooldown_until - time.monotonic()
                if delay <= 0:
                    self.active += 1
                    return
            await asyncio.sleep(delay)

    async def release(self, ok: bool) -> None:
        async with self.cond:
            self.active -= 1
            if ok:
                self.successes += 1
                if self.successes >= GROWTH_AFTER and self.permits < self.ceiling:
                    self.permits += 1
                    self.successes = 0
            else:
                self.successes = 0
                self.permits = max(1, self.permits // 2)
                self.cooldown_until = time.monotonic() + COOLDOWN_SECONDS
            self.cond.notify_all()


class Halt:
    """Set when the service reports a usage cap; pending work is deferred instead of hammered."""

    def __init__(self):
        self.active = False
        self.retry_after = 0
        self.reason = ""

    def trip(self, retry_after: int, reason: str) -> None:
        if not self.active:
            self.active = True
            self.retry_after = retry_after
            self.reason = reason


def _retry_after_seconds(e: TypeSafeAPIError) -> int:
    value = (e.headers.get("retry-after") or "").strip()
    return int(value) if value.isdigit() else 0


def _is_usage_cap(e: TypeSafeAPIError) -> bool:
    body = e.body if isinstance(e.body, dict) else {}
    return (body.get("error") or {}).get("type") == "FreeUsageLimitError"


@dataclass
class Stats:
    total: int = 0
    cached: int = 0
    done: int = 0
    failed: int = 0
    deferred: int = 0
    input_tokens: int = 0
    started: float = field(default_factory=time.monotonic)

    def line(self, limiter: Limiter) -> str:
        finished = self.cached + self.done + self.failed + self.deferred
        elapsed = max(time.monotonic() - self.started, 1e-6)
        rate = self.done / elapsed
        remaining = self.total - finished
        eta = remaining / rate if rate > 0 else float("inf")
        return (f"{finished}/{self.total} (cached {self.cached}, new {self.done}, failed {self.failed}, deferred {self.deferred}) "
                f"{rate:.2f} req/s, {self.input_tokens} input tokens, permits {limiter.permits}, "
                f"ETA {eta / 60:.1f} min")


def make_client(model: str) -> AsyncTypeSafeClient:
    key = os.environ.get("OPENCODE_API_KEY")
    if not key:
        raise SystemExit("OPENCODE_API_KEY is not set")
    # retry-after is handled here rather than by the SDK, which would otherwise sleep for a whole day on a usage cap.
    return AsyncTypeSafeClient(
        api_key=key, base_url=BASE_URL, model=model, timeout=120.0,
        retry=RetryPolicy(max_retries=4, backoff_initial=1.0, backoff_max=20.0, timeout=120.0, respect_retry_after=False),
    )


async def _classify_one(client, model: str, limiter: Limiter, halt: Halt, pol: Policy, cache: Cache,
                        results: dict, errors: dict, stats: Stats):
    body = build_request(pol)
    sha = request_sha(body)
    last: Optional[BaseException] = None
    for _ in range(OUTER_ATTEMPTS):
        if halt.active:
            stats.deferred += 1
            return
        await limiter.acquire()
        ok = True
        try:
            if halt.active:
                stats.deferred += 1
                return
            resp = await client.system_one(body["state"], body["questions"], model=model)
            data = resp.model_dump(mode="json")
            missing = [q for q in body["questions"] if q not in data.get("answers", {})]
            if missing:
                raise TypeSafeAPIError(200, data, resp.raw_http_response.headers, f"answers missing for {missing}")
            rec = {"uid": pol.uid, "request_sha": sha, "model": data.get("model"), "answers": data["answers"],
                   "usage": data.get("usage"), "ts": time.time()}
            cache.put(rec)
            results[pol.uid] = rec
            stats.done += 1
            stats.input_tokens += (data.get("usage") or {}).get("input_tokens", 0) or 0
            return
        except TypeSafeRateLimitError as e:
            ok = False
            last = e
            wait = _retry_after_seconds(e)
            if wait > HALT_AFTER_SECONDS or _is_usage_cap(e):
                halt.trip(wait, str(e))
                stats.deferred += 1
                return
        except _TRANSIENT as e:
            ok = False
            last = e
        except TypeSafeAPIError as e:
            errors[pol.uid] = {"error": type(e).__name__, "detail": str(e)[:2000]}
            stats.failed += 1
            return
        finally:
            await limiter.release(ok)
    errors[pol.uid] = {"error": type(last).__name__ if last else "unknown", "detail": str(last)[:2000]}
    stats.failed += 1


async def _progress(stats: Stats, limiter: Limiter, stop: asyncio.Event) -> None:
    while not stop.is_set():
        try:
            await asyncio.wait_for(stop.wait(), timeout=15.0)
        except asyncio.TimeoutError:
            print("  " + stats.line(limiter), file=sys.stderr, flush=True)


async def _run(policies: list[Policy], workers: int, cache: Cache, model: str) -> tuple[dict, dict, Halt]:
    results: dict[str, dict] = {}
    errors: dict[str, dict] = {}
    stats = Stats(total=len(policies))
    limiter = Limiter(start=min(8, workers), ceiling=workers)
    halt = Halt()
    pending: list[Policy] = []
    for pol in policies:
        hit = cache.get(pol.uid, request_sha(build_request(pol)))
        if hit:
            results[pol.uid] = hit
            stats.cached += 1
        else:
            pending.append(pol)
    print(f"classify: {len(policies)} policies, {stats.cached} cached, {len(pending)} to request via {model}", file=sys.stderr)
    if pending:
        stop = asyncio.Event()
        reporter = asyncio.create_task(_progress(stats, limiter, stop))
        async with make_client(model) as client:
            await asyncio.gather(*(_classify_one(client, model, limiter, halt, p, cache, results, errors, stats) for p in pending))
        stop.set()
        await reporter
    print("classify: " + stats.line(limiter), file=sys.stderr)
    if halt.active:
        hours = halt.retry_after / 3600
        print(f"classify: HALTED, usage limit reached ({halt.reason}); {stats.deferred} policies deferred, "
              f"retry in about {hours:.1f} h", file=sys.stderr)
    return results, errors, halt


def select_policies(catalog: Catalog, uids: Optional[list[str]] = None, limit: Optional[int] = None) -> list[Policy]:
    policies = [catalog.policies[u] for u in sorted(catalog.policies)]
    if uids:
        wanted = set(uids)
        policies = [p for p in policies if p.uid in wanted]
    if limit:
        policies = policies[:limit]
    return policies


def classify(catalog: Catalog, uids=None, limit=None, workers: int = 16, dry_run: bool = False,
             model: str = MODEL) -> dict[str, dict]:
    policies = select_policies(catalog, uids, limit)
    if dry_run:
        for pol in policies:
            print(json.dumps(build_request(pol), indent=2, ensure_ascii=False))
        return {}
    cache = Cache()
    try:
        results, errors, _ = asyncio.run(_run(policies, workers, cache, model))
    finally:
        cache.close()
    ERRORS_PATH.parent.mkdir(parents=True, exist_ok=True)
    ERRORS_PATH.write_text(json.dumps(errors, indent=2, ensure_ascii=False), encoding="utf-8")
    if errors:
        print(f"classify: {len(errors)} policies failed; see {ERRORS_PATH}", file=sys.stderr)
    return results


def load_results(catalog: Catalog) -> dict[str, dict]:
    """Cached answers for the current question wording, without making any requests."""
    cache = Cache()
    try:
        out = {}
        for pol in catalog.policies.values():
            rec = cache.get(pol.uid, request_sha(build_request(pol)))
            if rec:
                out[pol.uid] = rec
        return out
    finally:
        cache.close()
