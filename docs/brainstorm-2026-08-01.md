# Brainstorm — Sharkable Feature & Improvement Ideas (2026-08-01)

> Status: brainstorming document — nothing here is committed to any release.
> Baseline: v0.7.6 (46 audit findings fixed, NativeAOT fully working).
> Related docs: `docs/code-review-2026-07-31.md`, `docs/quality-improvement-plan/07-feature-suggestions.md`
> (FEAT-01…08/10/11 from that plan are **already shipped** — this document covers what's still open plus new ideas).

## 0. Verified status of prior suggestions (so we don't re-propose)

| Prior idea | Status |
|---|---|
| FEAT-01 security headers | ✅ shipped |
| FEAT-02 metrics (System.Diagnostics.Metrics) | ✅ shipped |
| FEAT-03 IAuditSink | ✅ shipped |
| FEAT-05 per-endpoint rate limit | ✅ shipped (`[SharkRateLimit]` + DSL) |
| FEAT-06 idempotency attributes | ✅ shipped (`[SharkIdempotent]` / `[SharkNoIdempotency]`) |
| FEAT-07 cache profile attribute | ✅ shipped (`[SharkCacheProfile]`) |
| FEAT-08 request-timeout DSL | ✅ shipped (`[SharkRequestTimeout]`) |
| FEAT-10 parallel warmup | ✅ shipped (`WarmupServiceTypes`, BUG-117 hardened) |
| FEAT-11 Sharkable.Testing package | ✅ shipped (v0.7.5) |
| FEAT-09 validation error shape modes | ◐ partial (ProblemDetails serialization done; mode option not exposed) |
| FEAT-12 group/endpoint lifecycle hooks | ◐ partial (`EndpointConvention` exists; `GroupConvention` doesn't) |
| FEAT-13 OpenAPI ergonomics | ◐ partial (transformers exist; simplified registration doesn't) |
| FEAT-14 source generator for discovery | ✗ open (long-term) |
| FEAT-15 IApiKeyValidator per-key principals | ✗ open |
| Roadmap #4 compile-time route conflict analyzer | ✗ open |
| Roadmap #14 OpenAPI example generation | ✗ open |
| Review legacy: ARCH-02 static mutable SharkOption | ✗ open |
| Review legacy: PERF-03 attribute DI scan O(n²) | ✗ open |
| **Sharkable.Tests: 14 pre-existing failures** | ✗ open — **should be fixed before any new feature work** |

---

## 1. Architecture & internal hardening

### ID-01 — Kill the legacy `[SharkEndpoint]` reflection system
Attribute-based endpoints (`IDependencyReflectorFactory`, `Reflector`, `SharkHttpMethod`)
are `[Obsolete]`, not AOT-safe, and still ship. Removing them (or gating behind a
netstandard2.0-era compat package) shrinks the surface, removes the last big
reflection hot spots, and simplifies startup.
**Value** Medium · **Effort** M · **Intrusion** Breaking (planned for a minor bump with migration docs).

### ID-02 — Single source of truth for options (finish ARCH-02)
`SharkOption` is still a static mutable singleton. Explore making `Shark.SharkOption`
an immutable snapshot swapped atomically at `AddShark()`/`UseShark()` time:
- stops runtime mutation races (options read from middleware mid-request)
- enables hot-reload later (see ID-12)
- migration cost: internal reads go through a frozen snapshot; public `Shark.SharkOption`
  keeps working as a facade.
**Value** Medium-High · **Effort** M-L · **Intrusion** Zero externally if done as a facade.

### ID-03 — Fix PERF-03: attribute DI scan
`[ScopedService]`/`[TransientService]`/`[SingletonService]` discovery re-scans and
re-registers on every call — quadratic in assembly count. Cache scan results per
assembly (immutable registration plan), parallelize, or move to the FEAT-14 generator.
**Value** Low-Medium · **Effort** S-M.

### ID-04 — Endpoint discovery diagnostics
A `/_sharkable/routes` admin endpoint listing every mapped route with its filters,
metadata (rate limit, idempotency, authz), and OpenAPI operation id — gold for
support tickets ("which middleware applies to this route?"). API-key gated like
profiler. Could build on the existing profiler endpoint pattern.
**Value** High · **Effort** M.

### ID-05 — Request pipeline telemetry spans
Today tracing exists; add per-middleware Activity spans (auth, rate limit,
idempotency, wrap) so a distributed trace shows exactly where time went.
Zero-dep, opt-in, flows into OpenTelemetry automatically.
**Value** Medium · **Effort** S-M.

### ID-06 — Config snapshot diffing / validation report
`ConfigurationValidator` exists; add a **validation report** (warnings + errors +
resolved values with redaction) printed once at startup under `_sharkable/validate`
or log level Information — catches typos users would otherwise find in prod.
**Value** Medium · **Effort** S.

## 2. Developer experience

### DX-01 — `dotnet new` project templates
`dotnet new sharkable-webapi` (minimal + full sample with auth/rate limit/cron)
and `dotnet new sharkable-plugin` (skeleton for a companion package). Templates
are the #1 adoption driver for frameworks.
**Value** High · **Effort** M (templates + CI test) · **Intrusion** New repo/package.

### DX-02 — OpenAPI example generation (Roadmap #14)
Infer examples from type names + XML docs (`<example>` tags) + `[SharkExample]`
attribute. Ships examples for every schema → Scalar UI demos become one-click.
**Value** Medium · **Effort** M · depends on OpenAPI transformer plumbing.

### DX-03 — Endpoint convention helpers
`opt.ConfigureGroups(c => c.WithTag(...).RequireAuthorization(...))` — a typed
flattening of today's `GroupConvention` gap (FEAT-12) with IntelliSense.
**Value** Medium · **Effort** S.

### DX-04 — Auto-generated API client package
`Sharkable.ClientGen` — source generator or build task turning the OpenAPI doc
into a typed C# client (Newtonsoft-free, AOT-safe). Users get a client for free
after `dotnet run` + one command.
**Value** Medium-High · **Effort** L.

### DX-05 — Error catalog + reference docs page
Every `Sharkable` error code (`idempotency_key_conflict`, `rate_limit_exceeded`, …)
gets a stable ID, and the docs site gets a machine-readable catalog page
(`/docs/error-catalog`). Support teams can look up codes without reading source.
**Value** Medium · **Effort** S-M.

### DX-06 — First-class testing helpers (extend Sharkable.Testing)
`WebApplicationFactory` wiring, in-memory fake stores for idempotency/rate-limit/
cron/saga, unified-result assertion helpers, and a `TestServer` bootstrap that
exercises the full `AddShark → UseShark` path. The Testing package exists; grow it.
**Value** High · **Effort** M.

## 3. Security

### SEC-01 — HMAC request signing helper
`opt.EnableRequestSigning(secretKey)` — optional `X-Signature` header (HMAC-SHA256
over method+path+body, constant-time compare) for server-to-server callers who
can't do OAuth. Middleware validates before auth.
**Value** Medium · **Effort** M.

### SEC-02 — Login brute-force protection preset
A documented recipe + `opt.ConfigureLoginProtection(loginPath, maxAttempts, window)`
that wires per-credential rate limiting + lockout + audit entries for login
endpoints (IP + username keyed). Many users hand-roll this.
**Value** High · **Effort** M.

### SEC-03 — Dependency audit in CI
Enable `NuGetAudit` (already on by default in .NET 8+) in CI + `dotnet list package
--vulnerable` gate so NU1903-class issues are caught at PR time, not release time.
**Value** High · **Effort** S (CI only).

### SEC-04 — Security headers coverage test
A `Sharkable.Tests` suite asserting default/configured header output
(X-Content-Type-Options etc.) — regression-proofs FEAT-01 and future header work.
**Value** Low-Medium · **Effort** S.

### SEC-05 — Per-key principals (FEAT-15)
`IApiKeyValidator` returns claims (tenant, scopes, per-key rate multiplier);
API-key auth becomes a first-class auth scheme instead of a middleware gate.
**Value** Medium · **Effort** M.

## 4. Operations & cluster

### OPS-01 — Distributed cron leader election
`ICronJobStore`-backed lease (or `Sharkable.Cache.Redis` lock) so a cron job runs
on exactly one instance in a cluster. Today every replica fires every job.
**Value** High (multi-instance users) · **Effort** M · **Intrusion** Opt-in per job.

### OPS-02 — Health check caching with debounce
Aggregate `/healthz` results cached for N seconds (configurable) so a flapping
DB check doesn't flap the readiness probe; add `HealthCheckDetailLevel`-aware
cache-busting on state change.
**Value** Medium · **Effort** S.

### OPS-03 — Startup dependency pre-warm
`HealthChecksConfigure` could expose "warm before ready" semantics: run slow checks
(DB ping) once at startup and feed the result into the readiness gate, so k8s
doesn't see 30s of 503 on every rollout.
**Value** Medium · **Effort** S-M.

### OPS-04 — Adaptive limiter tuning surface
AdaptiveLimitMonitor exists; expose its observed metrics (CPU, rejection rate)
via metrics + a status endpoint so operators can see why limits moved.
**Value** Low-Medium · **Effort** S.

### OPS-05 — Request/response audit redaction policies
Audit sink exists; add per-field redaction policies (credit-card-like patterns,
header allowlist) with a `RedactPolicy` builder — prevention against PII leaks in
audit logs. (Redacting formatter exists for logs; extend to audit.)
**Value** High · **Effort** M.

## 5. Data layer / AutoCrud

### DATA-01 — Cursor pagination
`IAutoCrudEntity` gains `CursorPagination` option: keyset pagination (`WHERE id >
lastId ORDER BY id LIMIT n`) for stable lists under writes — the classic
offset-pagination failure mode.
**Value** High · **Effort** M.

### DATA-02 — Field selection & partial update
`PATCH` support with whitelisted columns + `fields` query param for GET list
(projection). Today AutoCrud is full-entity only.
**Value** Medium-High · **Effort** M.

### DATA-03 — Audit fields auto-fill
`CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` filled automatically from the
request (claims/tenant) when the entity has them — zero user code.
**Value** Medium · **Effort** S-M.

### DATA-04 — Tenant column filter for AutoCrud
Multi-tenant row isolation via tenant column (`TenantId`) auto-appended to
AutoCrud queries + write enforcement — the missing piece between AutoCrud and
the multi-tenant middleware.
**Value** High (SaaS users) · **Effort** M.

### DATA-05 — Bulk operations
Bulk insert/update/delete endpoints (bounded batch size, capped by
`SqlSugarOptions`) for import-style workflows.
**Value** Medium · **Effort** S-M.

## 6. AI / LLM era

### AI-01 — MCP server plugin (`Sharkable.Mcp`)
Model Context Protocol server hosting the app's `ISharkEndpoint` surface —
any LLM client can call the API through the same OpenAPI metadata. .NET has
official `ModelContextProtocol` SDK; wrap it as a plugin package.
**Value** High (2026 zeitgeist) · **Effort** M-L · **Intrusion** New package.

### AI-02 — SSE streaming helper
`Results.Sse(...)`-style endpoint DSL for token streaming (with `[SharkNoIdempotency]`
interop, ETag/compression bypass, cancellation) — LLM endpoints are the new hot
path and every framework needs a streaming story.
**Value** High · **Effort** S-M.

### AI-03 — Semantic cache store interface
`ISemanticCacheStore` + `Sharkable.Cache.Redis` implementation for LLM response
caching (hash of prompt + system + model). Opt-in, follows existing factory pattern.
**Value** Medium · **Effort** M.

### AI-04 — Token-aware rate limiting
Rate limit by estimated token consumption (`[SharkRateLimit]` extended with
`costHint`) — practical for AI gateways; middleware stays dumb, policy stays simple.
**Value** Medium · **Effort** S-M.

## 7. .NET 10 ecosystem alignment

### NET-01 — HybridCache integration
Map `SharkCacheProfile`/output-cache onto `HybridCache` (the .NET 9+ L1/L2 cache
API) with a pluggable `IDistributedCache` — better than the current custom ETag
+ output-cache mix for users who already use HybridCache.
**Value** Medium · **Effort** M · **Intrusion** Zero (new opt-in path).

### NET-02 — `CreateSlimBuilder` compatibility test
Ensure `AddShark`/`UseShark` work under `WebApplication.CreateSlimBuilder`
(no `appsettings`, trimmed DI) — a common template users start from.
**Value** Medium · **Effort** S (test + fixes).

### NET-03 — OpenAPI 2.x schema detail pass
With `Microsoft.OpenApi` 2.x, verify: nullable annotations, `additionalProperties`,
`readOnly`/`writeOnly` for AutoCrud, enum descriptions — the AOT schema context
path deserves a dedicated compatibility test matrix.
**Value** Low-Medium · **Effort** M.

### NET-04 — .NET 10 bearer-token auth integration
Use `Authentication.BearerToken` as an optional built-in scheme (username/password
→ token) for `NativeTest`-style apps without Identity — small, high demo value.
**Value** Medium · **Effort** S.

## 8. Quality & release process

### QA-01 — Fix the pre-existing test suite
`Sharkable.Tests` has 14 failing tests at baseline (verified identical on clean
`b68a1b4`). Triage: fix framework bugs they expose, or mark expected-fail with
issue links. **No feature work should start before this.**
**Value** Critical · **Effort** M.

### QA-02 — CI pipeline (GitHub Actions)
Build + test + `Sharkable.NativeTest` AOT publish smoke gate (the pre-publish gate
from AGENTS.md, automated) + NuGet audit + pack verification on every PR. The
repo has no CI today.
**Value** High · **Effort** S-M.

### QA-03 — Benchmark suite
`BenchmarkDotNet` project for hot paths: idempotency fingerprint hashing,
cron next-run computation, wrap filter, ApiKeyValidator — with a
"no regression vs v0.7.x" gate in CI.
**Value** Medium · **Effort** M.

### QA-04 — Contract tests for response shapes
Golden-file tests for every middleware's HTTP contract (status codes, headers,
body shape, OpenAPI sync — e.g. auto-wrap doc vs runtime) so the "docs say X,
code does Y" class of bugs dies permanently.
**Value** High · **Effort** M.

### QA-05 — Docs: AOT quickstart section
With NativeAOT now actually working, document the exact `Program.cs` shape
(assemblies + JsonSerializerContext + internal endpoints) and a "gotchas" list
(no `Task<IResult>` handlers — use `HttpContext` writes or typed results).
**Value** Medium · **Effort** S.

---

## Suggested prioritization

1. **QA-01** (fix test suite) — unblocks everything else; the 14 failures may
   hide real bugs.
2. **QA-02** (CI) — makes every subsequent change reviewable.
3. **OPS-01** (distributed cron lease) — biggest correctness gap for
   multi-instance users today.
4. **DX-01** (templates) — highest adoption multiplier.
5. **AI-02 + AI-01** (SSE + MCP) — the 2026 "must have a story" features.
6. **DATA-04** (tenant column filter) — completes the SaaS story.
7. **ID-01** (kill legacy attribute system) — scheduled breaking cleanup for a
   minor release with migration docs.
