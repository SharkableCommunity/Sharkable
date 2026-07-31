# Sharkable Code Review — 2026-07-31

**Commit reviewed:** `b68a1b4` (v0.7.5, main)
**Scope:** `src/Sharkable` (core) + `src/Sharkable.NativeTest` (AOT smoke app)
**Method:** Manual review of all ~100 source files (~11k LOC), 3 parallel sub-agent module reviews, plus **runtime verification** (NativeTest live app + scratch minimal-API apps). Previous audit baseline: `docs/quality-improvement-plan/` (2026-07-19, 71 items) — status of those items is tracked at the end of this document.

---

## 0. Executive summary

The v0.7.x audit rounds fixed the vast majority of the July-19 findings (all 12 BUGs, ~30 PERF/MEM/SEC items — verified against code). This review found **2 new P1 functional bugs in the idempotency feature** (both reproduced live), **1 P1 hang in the cron parser** (reproduced), **1 P1 security hole** (empty `ApiPrefix` disables all security filters), **1 P1 regression** (`[SharkDontWrap]` attribute silently broken, reproduced), plus ~25 P2/P3 items.

**Most urgent (next release):**
1. Idempotent replay always fails with 422 for any request carrying a body (fingerprint asymmetry).
2. Idempotent first-execution responses are sent to the client with an **empty body** (buffer position not reset).
3. `*/0` cron expression → infinite loop at startup → host hangs.
4. Cron expressions with fixed second ≠ 0 stop firing after the first match, and never-matching patterns burn ~2.1M iterations **every second** (measured 59–88 ms per tick).
5. `opt.ApiPrefix = ""` silently bypasses API-key auth, validation, authz-interceptor and auto-wrap for every endpoint.
6. `[SharkDontWrap]` attribute does nothing (only the `.DisableAutoWrap()` DSL works).

---

## 1. P1 — Fix in the next release

### BUG-101 — Idempotent replay always returns 422 for requests with a body
**Verified: reproduced live against NativeTest** (2nd identical POST → `422 idempotency_key_conflict`).
**Location:** `Middleware/Idempotency/SharkIdempotencyMiddleware.cs:98,182-188` + `IdempotencyFingerprint.cs:66-91`
**Problem:** The stored fingerprint is computed **after** `_next(context)` has run (line 184), i.e. after the endpoint consumed the request body — Kestrel's `HttpRequestStream` is not seekable and no `EnableBuffering` exists anywhere, so the hash covers only an empty remainder (plus content-length). On replay, the fingerprint is computed **before** execution from the full intact body (line 98) → mismatch → spurious 422 for virtually every POST/PUT/PATCH. Only body-less requests replay correctly. The idempotency contract — the feature's entire purpose — fails for its primary use case.
**Fix:** Compute the fingerprint from a pre-execution copy of the body: call `Request.EnableBuffering()` (capped at `MaxFingerprintBodySize`) before `_next`, rewind, hash, then let the endpoint re-read. Store that fingerprint; drop the post-execution computation. Add a regression test (same key + same body twice → second call replays, not 422).

### BUG-102 — Idempotent first execution returns an empty response body
**Verified: reproduced live** (1st POST → HTTP 200 with zero-byte body; `Content-Length: 0`).
**Location:** `Middleware/Idempotency/SharkIdempotencyMiddleware.cs:180` (vs. the correct `:198`)
**Problem:** On the cacheable-status path, `await buffer.CopyToAsync(originalBody)` is called without resetting `buffer.Position` — the `MemoryStream` position is at the end after the response was written, so **zero bytes** are forwarded to the client. The non-cacheable path correctly does `buffer.Position = 0` first (line 198). Every cacheable idempotent response is delivered empty to the first caller (the stored record itself is fine, so replays — once BUG-101 is fixed — would return the full body).
**Fix:** `buffer.Position = 0;` before the `CopyToAsync` on line 180. Same test as BUG-101 asserts a non-empty first response.

### BUG-103 — Cron parser infinite loop on `step = 0` — host hangs at startup
**Verified: reproduced** (parse of `*/0 * * * * *` never returns).
**Location:** `Cron/CronExpression.cs:95-109` (also `ParseRange` 127-133)
**Problem:** `*/0` or `5-10/0` parses step 0 → `for (var v = min; v <= max; v += step)` never advances → infinite loop. `Parse` runs at job registration inside `SharkCronHostedService.ExecuteAsync`, so a single typo in a config file hangs the whole app forever (the `RegisterAsync` catch in `CronScheduler.cs:53-58` never executes). Related parser defects in the same method: `"5/10"` (range-less step, valid cron) hits `ParseRange` with `dash == -1` → `range[..-1]` → `ArgumentOutOfRangeException`; steps larger than the field max silently yield only the first value.
**Fix:** Validate `step >= 1` (throw `FormatException`) before every loop; treat a dash-less range as `(value, fieldMax)`; reject out-of-range steps. Add parser tests.

### BUG-104 — Never-matching / fixed-second cron expressions burn ~2.1M iterations every second
**Verified: reproduced** (`30 * * * * *` from 12:00:31 → `null`; Feb-30 pattern → `null` after 59–88 ms).
**Location:** `Cron/CronExpression.cs:56-72` (caller `CronScheduler.cs:98`, tick loop `SharkCronHostedService.cs:71`)
**Problem:** (a) After the first iteration `GetNext` zeroes the seconds component (lines 67-69: `AddMinutes(1)` then reconstruct with `second: 0`). Any pattern whose matched second ≠ tick-second+1 (e.g. `30 * * * * *`) **never matches again** → the job stops firing and every `GetNext` call scans the full 4-year window (~2.1M iterations, measured 59–88 ms). (b) Structurally impossible patterns (`0 0 30 2 * ?` — Feb 30) also return `null` after the full scan. Because the scheduler calls `GetNext` for **every job on every 1-second tick**, a single bad pattern permanently drains ~6-9% of a CPU core and stalls the shared scheduler loop.
**Fix:** Fix the seconds handling (don't zero seconds mid-search; step by second when the second field is constrained, or restructure the search). Memoize the negative result — once a 4-year horizon is exhausted, return `null` immediately until the horizon passes. Ideally detect impossible day/month combinations at parse time.

### BUG-105 — Empty `ApiPrefix` bypasses ALL group filters and conventions (security)
**Location:** `SharkEndpoint/Extensions/EndPointExtension.cs:92-96`
**Problem:** When `SharkOption.ApiPrefix` is empty/whitespace (a plausible "no prefix" configuration), `BuildAction` is invoked directly on `app` and the entire group pipeline is skipped: auto-wrap, **validation filter, API-key filter, authorization-interceptor filter**, cache/rate-limit profiles, tags, `RequireAuthenticatedByDefault`, and route formatting. A misconfiguration silently disables the security middleware for every route.
**Fix:** Always create the `MapGroup` (with `""` base path) so filters/conventions apply uniformly, or reject empty `ApiPrefix` in `ConfigurationValidator`.

### BUG-106 — `[SharkDontWrap]` attribute is silently broken
**Verified: reproduced live** (attribute class still returns wrapped envelope; `.DisableAutoWrap()` DSL returns raw).
**Location:** `SharkEndpoint/Extensions/EndPointExtension.cs:234-238`; `ExceptionHandler/UnifiedResultWrapFilter.cs:17`
**Problem:** The BUG-06 fix routes `[SharkDontWrap]` classes through a nested `group.MapGroup("")` assuming the group-level `UnifiedResultWrapFilter` won't apply. ASP.NET Core group filters **are inherited by nested groups** (empirically confirmed on .NET 10.0.107). The nested group also never receives `DisableAutoWrapMetadata` — the only thing the wrap filter checks — so responses from `[SharkDontWrap]` classes are still wrapped. No tests cover it.
**Fix:** When `hasDontWrap`, add `targetGroup.WithMetadata(new DisableAutoWrapMetadata())` directly and drop the nested-group trick. Add a regression test.

---

## 2. P2 — Scheduled fixes

### Idempotency
- **BUG-107 — `MaxEntries` is a byte budget, not an entry count → dedup silently breaks under load.** `MemoryIdempotencyStore.cs:31,46,74`: `SizeLimit = MaxEntries` (default 10,000) vs per-entry sizes of 256 (marker) / `Body.Length+256` (record). Only ~39 in-flight keys can coexist; any completed response body > ~9.7 KB can never be admitted when the cache is full. Under real concurrency, in-flight markers are LRU-evicted within seconds → `TryReserveAsync` returns true again → duplicate executions. Fix: `entry.Size = 1` per entry (entry count semantics, matching the docs), keeping the byte cap via `MaxResponseSize`.
- **BUG-108 — Fingerprint hashes the path with ASCII → non-ASCII paths collide.** `IdempotencyFingerprint.cs:36,77`: `Encoding.ASCII.GetBytes(pathValue)` turns `/café` and `/cafè` into identical fingerprints → with same user+key+body, the middleware replays the *other resource's* cached response. Fix: `Encoding.UTF8.GetBytes`.

### Rate limiting
- **BUG-109 — `MemoryRateLimitStore` same byte-unit flaw → ~390 keys, rate-limit bypass vector.** `MemoryRateLimitStore.cs:21,52`: `MaxEntries=100,000` byte budget / 256 per key → only ~390 distinct keys before LRU eviction (docs promise 100,000). A client generating unique paths/IPs can deliberately evict other clients' counters, resetting their windows. Fix: `Size = 1` accounting or rename the option (`MaxCacheBytes`) with a sane default.
- **BUG-110 — Rate limiter runs before authentication → per-user key branch is dead code.** `SharkableExtension.cs:136` (limiter) vs `:170` (`UseAuthentication`): `DefaultKeyGenerator`'s `"u:" + User.Identity.Name` branch never fires because `context.User` is not authenticated yet. All traffic is keyed `ip:{RemoteIpAddress}:{path}` — behind a proxy/CDN one client exhausts the shared bucket for everyone. Fix: move the rate-limiter registration after `UseAuthentication`/`UseAuthorization`, or document the ordering constraint.
- **BUG-111 — 429 (rate-limited) requests bypass the audit trail entirely.** Audit middleware (`SharkableExtension.cs:183-184`) is registered *inside* the rate limiter; the reject path returns without `_next`, so the security-relevant events (who was throttled, how often) never reach the audit sink. Fix: register audit before the limiter, or emit the audit entry from the reject path.

### Audit
- **BUG-112 — `AuditLogBuffer.FlushRemaining()` drops all queued entries on shutdown.** `AuditLogBuffer.cs:67-104`: cancellation lands while the consumer is blocked in `WaitToReadAsync(ct)` → `TaskCanceledException` escapes the loop (the `catch ... when (ex is not OperationCanceledException)` filter doesn't catch it) → the drain block is skipped. With the default `BatchSize=1` there is no timer window, so `EnsureFlushOnShutdown` (default true) fails exactly when it matters. Fix: move the drain into a `finally` around the loop (or `catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }`), then drain; `Dispose()` should await the consumer with a timeout.

### ETag / caching
- **BUG-113 — ETag middleware throws on streamed/flushed responses and overwrites endpoint `Cache-Control`.** `ETag/ETagMiddleware.cs:71-79`: after `_next`, headers/status are mutated unconditionally — if the endpoint flushed (SSE, chunked), `Response.HasStarted` is true and `Headers["ETag"] = ...` throws → 500 on a successful stream. It also unconditionally overwrites an endpoint-set `Cache-Control` (e.g. `no-store` on sensitive GETs). Additionally the counting stream spools **all** bytes (even past `MaxResponseSize`) so SSE/streaming responses under ETag grow memory unboundedly. Fix: `if (context.Response.HasStarted) { await counting.FlushAsync(...); return; }`; only set `Cache-Control` if the endpoint didn't; skip buffering for `text/event-stream`.
- **BUG-114 — `SharkCacheProfileFilter` same `HasStarted` + `Cache-Control` overwrite issues.** `SharkCacheProfileFilter.cs:7-26`: guard with `HasStarted` and only set `Cache-Control` when absent, else sensitive endpoints can get cached with `public, max-age=...`.

### Saga
- **BUG-115 — Compensation shares one timeout CTS across all steps.** `DistributedTx/SagaExecutor.cs:227-241`: if step N consumes the `CompensationTimeout` budget, every subsequent step receives a cancelled token → throws immediately → remaining rollback steps never run (silent partial rollback). Fix: fresh CTS per compensation step.
- **BUG-116 — Failed compensation still deletes crash-recovery progress.** `SagaExecutor.cs:243`: `DeleteAsync` runs unconditionally even when compensation steps threw, destroying the persisted step index — a later retry restarts from step 0 and re-runs forward steps of a saga whose rollback never completed. Fix: only `DeleteAsync` when all compensations succeeded; keep progress and surface a "compensation pending" state.

### Warmup
- **BUG-117 — Warmup timeout disposes CTSs without cancelling; failures abandon sibling tasks.** `SharkableExtension.cs:243-263`: `CancellationTokenSource.Dispose()` does not cancel — the `CancelAfter` timer virtually never fires before dispose, so on timeout or any single warmup failure the remaining warmup tasks keep running indefinitely, unobserved; later `token.Register(...)` in a warmup service throws `ObjectDisposedException`. `catch (AggregateException) { throw; }` also rethrows the wrapper instead of the root cause. Fix: `cts.Cancel()` before `Dispose()` in `finally`; on failure cancel all, await with a bounded grace, then rethrow the inner exception.

### AOT / NativeAOT hazards
- **BUG-118 — `ValidationFilter` performs `MakeGenericType` in the per-request path.** `Validation/ValidationFilter.cs:26-30`: `typeof(IValidator<>).MakeGenericType(t)` inside `InvokeAsync` — an AOT violation (AGENTS.md). Under NativeAOT the closed `IValidator<T>` instantiation is not statically reachable → runtime failure on first validation. Fix: resolve via `IEnumerable<IValidator>` metadata once and cache.
- **BUG-119 — Runtime-`Type` JSON serialization breaks in NativeAOT.** `Validation/ValidationFilter.cs:71,81`, `UnifiedReults/UnifiedResultResult.cs:13`, `SharkIdempotencyMiddleware.cs:266`: `WriteAsJsonAsync(value, value.GetType())` requires reflection-based serialization; `ValidationProblemDetails` / arbitrary wrapped entities are not in the source-gen context → `NotSupportedException` at runtime in AOT. Fix: add `[JsonSerializable(typeof(ValidationProblemDetails))]` to `UnifiedResultSourceContext` and use context options; document the user-context requirement for wrapped entity types.
- **BUG-120 — `AddAutoCrud` uses unannotated reflection and silently vanishes in AOT.** `AutoCrud/SqlSugar/Extensions/AutoCrudExtension.cs:15-46`: `Assembly.Load` + `GetType` + `MethodInfo.Invoke` with no `[RequiresDynamicCode]` (leaks IL3050 to consumers); in AOT the load fails and AutoCrud is silently disabled (`Utils.WriteDebug` only). Fix: annotate; when `AotMode && SqlSugarOptionsConfigure != null`, emit a clear startup warning or fail fast.
- **BUG-121 — `[SharkOpenApiIgnore]` schema stripping uses unguarded reflection and contains dead code.** `OpenApi/SwaggerExtension.cs:106-135`: `Type.GetMembers(...)` + `GetCustomAttribute` without `[RequiresUnreferencedCode]` — under trimming the attribute silently stops working and sensitive DTO properties reappear in `/openapi/v1.json`. Additionally line 127-131 has dead code: `schema.Required?.Count > 0 ? null : null` always yields `null` → a `[SharkOpenApiIgnore]` property listed in `required` is never removed, producing an invalid schema. Fix: traverse `JsonTypeInfo` metadata instead; fix the `Required.Remove(...)` logic.

### Health checks
- **BUG-122 — JWT health check permanently unhealthy for self-issued JWTs.**
  **Verified live** (`/healthz` → 503 "JWT authority unreachable", `InvalidOperationException: An invalid request URI was provided`).
  `Middleware/HealthChecks.cs:83-85`: `ConfigureJwt` accepts a non-URL authority (issuer name — the documented self-issued pattern, see NativeTest), then `GetAsync($"{authority}/.well-known/openid-configuration")` throws → `Unhealthy` forever → k8s readiness fails permanently. Fix: only probe when the authority is an absolute http(s) URI; otherwise skip the check (or mark it informational).
- **BUG-123 — `JwtHealthCheck` never disposes the `HttpResponseMessage`** (`HealthChecks.cs:83`): pooled connection not returned until GC; the fallback `new HttpClient` path leaks a socket per probe. Fix: `using var response = ...`.

### DI / architecture
- **BUG-124 — `IUnifiedResultFactory` DI registration is dead and misleading (ARCH-01 still open).** `DependencyInjection/Extensions/DependencyInjectionExtension.cs:11` registers it, but all 5 call sites use `UnifiedResultFactoryHelper.ResolveFactory()` which only reads the static `Shark.SharkOption.UnifiedResultFactory` — a user's DI-registered custom factory is silently ignored. Fix: make `ResolveFactory()` DI-aware or remove the registration.
- **BUG-125 — Attribute DI registers ALL interfaces, including `IDisposable`/`IAsyncDisposable`.** `DependencyInjection/Extensions/AttributeServiceExtension.cs:124-132,195-208`: a `[SingletonService]` class implementing `IDisposable` gets registered as the DI implementation of `IDisposable`; two classes sharing a business interface silently "last-wins". Fix: filter to interfaces in scanned/user assemblies, at minimum exclude `IDisposable`/`IAsyncDisposable`.
- **BUG-126 — `Sharkable` appsettings section is never bound.** `Shark/Options/SharkOption.cs:13` documents `Default = "Sharkable"` config binding, but no `GetSection`/`Bind` exists anywhere; `services.Configure<SharkOption>` only registers a post-config callback. Config-file options do nothing. Also `MapSharkEndpoints` reads `Format`/`ApiPrefix`/`RequireAuthenticatedByDefault` from the DI copy while all middleware reads the static copy — two divergent option instances; and the user's `setupOptions` callback is invoked twice on two instances (side effects run twice). Fix: bind the section explicitly, or document that options are code-only; funnel all reads through one holder.

### Multi-tenant
- **BUG-127 — Apex domain is treated as a tenant.** `MultiTenant/TenantResolver.cs:43-44`: `myapp.com` → tenant `"myapp"` (fabricated tenant ID routed through tenant-scoped services). Fix: require ≥2 dots or a configured base-domain suffix before extracting the subdomain label.

### Cron (misc)
- **BUG-128 — `CronScheduler.TriggerAsync` is fire-and-forget with `CancellationToken.None`.** `Cron/CronScheduler.cs:221`: manual triggers survive shutdown, store failures become unobserved exceptions, and the `SkipIfRunning`/`IsRunning` check is bypassed → concurrent execution with a scheduled run. Fix: pass a linked shutdown token, honor `Concurrency`, observe faults.
- **BUG-129 — Cron hosted service always runs (even with zero jobs) and log-spams every second when the store is down.** `SharkExtension.cs:284` + `SharkCronHostedService.cs:66-69`: register the hosted service only when `ConfigureCronJobs != null`, or exit the loop when no jobs exist; add error backoff.

### OpenAPI
- **BUG-130 — Auto-wrap OpenAPI transformer desyncs from effective runtime semantics.** `OpenApi/SwaggerExtension.cs:61-91`: gated on `SharkOption.EnableAutoWrap` only — ignores the `UseSharkOptions.EnableAutoWrap` tri-state override, `[SharkDontWrap]` classes and `.DisableAutoWrap()` routes → generated document advertises wrapped schemas for endpoints that actually return raw payloads (and vice versa). Fix: evaluate the effective flag and read `DisableAutoWrapMetadata`/`SharkDontWrap` from endpoint metadata in the transformer.

### Endpoint mapping
- **BUG-131 — `addPrefix`/`baseApiPath` are dead fields; `[SharkEndpoint(ApiPrefix = null)]` "omit prefix" contract is broken.** `EndPointExtension.cs:398-435,69-70`, `SharkEndpoint.cs:13-14`: `addPrefix` is written in three places and read nowhere; `CreateSharkEndpoint` hardcodes `apiPrefix = "api"` (old-style endpoints always land under `api/…`, ignoring `SharkOption.ApiPrefix`). Fix: honor `addPrefix` or delete the dead fields and fix the docs.
- **BUG-132 — AutoCrud probe instantiates every endpoint class via `Activator.CreateInstance` at startup.** `EndPointExtension.cs:257`: constructor side effects run (bypassing DI), AOT-trim risk; the DI-resolved `ISharkEndpoint` singleton already implements `IAutoCrudEntityMarker` — use `(e as IAutoCrudEntityMarker)?.GetOperations() ?? CrudOperations.All`.

### Dependency security
- **BUG-133 — `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 has a known high-severity advisory (GHSA-2m69-gcr7-jv3q).** Build warning NU1903 via `SqlSugarCore 5.1.4.216` in `Sharkable.NativeTest`. Fix: bump SqlSugarCore once a version referencing a patched `e_sqlite3` is available (verify with `dotnet list package --vulnerable`).

---

## 3. P3 — Polish / opportunistic

- **BUG-134 — `AsCreated` returns 200 OK instead of 201 when `uri` is null.** `UnifiedReults/Extensions/UnifiedResultExtension.cs:90`: `Results.Ok(result)` on the null-uri path (`AsAccepted` correctly uses `Results.Json(result, statusCode: 202)`).
- **BUG-135 — `AsUnauthorized`/`AsForbidden` silently drop the error message and ignore `statusCode`.** `UnifiedResultExtension.cs:49-54,63-68`: return bare `Results.Unauthorized()`/`Results.Forbid()`; the family is non-uniform (all other helpers emit the unified envelope).
- **BUG-136 — `AdaptiveLimitMonitor` never disposed (instance registration).** `SharkExtension.cs:81`: MS.DI only disposes container-created singletons; the Timer/Process handle leak per host build. Fix: `AddSingleton(sp => new AdaptiveLimitMonitor(...))`.
- **BUG-137 — `SharkBackgroundService` health check leaks full exception stack traces via public `/healthz`** at `HealthCheckDetailLevel.Description/Full` (`SharkBackgroundService.cs:83,113`) — contradicts the SHARK-SEC-M004 principle applied to `JwtHealthCheck`. Fix: generic public description + log the stack.
- **BUG-138 — Fire-and-forget audit sink writes swallow async exceptions.** `AuditTrailMiddleware.cs:86`, `AuditLogBuffer.cs:112`: `_ = _auditSink.WriteBatchAsync(...)` inside try/catch — the catch only intercepts synchronous throws; async faults become unobserved. Fix: `await` in the consumer loop with catch, or attach a fault observer.
- **BUG-139 — Profiler gate re-hashes configured API keys on every request** (`Profiler/SharkProfilerEndpoint.cs:70-77`) instead of reusing the cached `ApiKeyValidator`.
- **BUG-140 — `AssemblyContext.Instance` is never reset** (`AssemlyContext/AssemblyContext.cs:15-32`): a second `AddShark`/host in the same process keeps the first app's assemblies (`Instance ??= ...` ignores the new argument); `SetAssembly` is dead code.
- **BUG-141 — Public API typo `Shark.SetAssebly(...)`** (`Shark/Shark.cs:54`) — public, so renaming is breaking; document or add an alias. Also internal `GetSharkEndpint` typo, unused `Shark.condition` lock, `GetServiceProvider(Type?)` ignores its parameter.
- **BUG-142 — `WarmupTimeout` is `internal`** (`SharkOption.cs:644`) — users cannot configure the warmup deadline; `WarmupServiceType` (first-of-list) vs the parallel list API is confusing.
- **BUG-143 — `SharkCronHostedService.cs:28` dead no-op loop** (`foreach (var job in _scheduler.Jobs.ToList()) { }`) with a misleading comment; remove.
- **BUG-144 — Cron week field rejects `7` (Sunday)** (`CronExpression.cs:41` — range `(0,6)`; many cron dialects accept 7).
- **BUG-145 — Old-style endpoint URL prefix hardcodes `"api"`** (`EndPointExtension.cs:398,430`), ignoring `SharkOption.ApiPrefix`.
- **BUG-146 — `AsStatus`-family helpers ignore a custom `statusCode` when `errors` is null** (e.g. `AsBadRequest(null, HttpStatusCode.Conflict)` returns plain 400).

---

## 4. Previously reported items (quality-improvement-plan) — status at b68a1b4

| Area | Status | Notes |
|---|---|---|
| BUG-01 cron shutdown token | ✅ FIXED | `SharkCronHostedService.cs:44-49` + `CronScheduler.cs:145-146` |
| BUG-02 exception handler | ✅ FIXED | `SharkExceptionHandlerMiddleware.cs:23-32` |
| BUG-03 saga compensation token | ✅ FIXED (residual: BUG-115/116) | `SagaExecutor.cs:227-228` |
| BUG-04 cron timeout/retry | ✅ FIXED | `CronScheduler.cs:150-161` |
| BUG-05 graceful shutdown status | ✅ FIXED | `GracefulShutdownMiddleware.cs:20` |
| BUG-06 auto-wrap semantics | ⚠️ FIXED with regression | tri-state + docs fixed; `[SharkDontWrap]` broken → BUG-106 |
| BUG-07 `MaxEntries=-1` | ✅ FIXED at store level (semantics still wrong → BUG-107/109) | `MemoryRateLimitStore.cs:42-49` |
| BUG-08 culture ToLower | ✅ FIXED | `StringExtension.cs:103,169` |
| BUG-10/11/12 | ✅ FIXED | |
| PERF-01 factory allocation | ✅ FIXED | static `Instance` + helper, all 5 sites |
| PERF-02 indented JSON | ✅ FIXED | `WriteIndented = false` |
| PERF-03 quadratic DI scan | ❌ STILL OPEN | → BUG-125-related; unguarded `GetTypes()` |
| PERF-04 audit HashSet | ✅ FIXED | ctor-cached |
| MEM-01..07 | ✅ FIXED (residual: BUG-112, BUG-136) | |
| SEC-01..06 | ✅ FIXED (residual: BUG-122, BUG-137) | |
| AOT-01..05 | ⚠️ FIXED for legacy path only | new gaps: BUG-118..121 |
| ARCH-01 IUnifiedResultFactory | ❌ STILL OPEN | → BUG-124 |
| ARCH-02 static options | ❌ STILL OPEN | → BUG-126 |
| ARCH-03..12 | ⚠️ Partially addressed in v0.7.0 | verify individually before Phase 4 |

---

## 5. Verification appendix (how each headline claim was tested)

All runtime checks were performed against `Sharkable.NativeTest` (v0.7.5, port 5245) or minimal scratch apps referencing the built library; scratch projects were created under `/tmp` and removed afterwards — no repo files were modified.

| # | Experiment | Result |
|---|---|---|
| E1 | POST `/api/product` with `Idempotency-Key`, then identical POST again | 1st: 200 **empty body** (BUG-102); 2nd: **422 idempotency_key_conflict** (BUG-101) |
| E2 | `CronExpression.Parse("*/0 * * * * *")` | **infinite loop** (hung 8 s subprocess) — BUG-103 |
| E3 | `GetNext` for `"30 * * * * *"` from 12:00:31 | `null` after 88 ms (job never fires again) — BUG-104 |
| E4 | `GetNext` for `"0 0 30 2 * ?"` (Feb 30) | `null` after 59 ms — repeated every tick = permanent CPU drain — BUG-104 |
| E5 | `[SharkDontWrap]` class endpoint vs `.DisableAutoWrap()` endpoint | attribute → still wrapped; DSL → raw — BUG-106 |
| E6 | `GET /healthz` with self-issued JWT (authority = issuer name) | 503 "JWT authority unreachable" — BUG-122 |
| E7 | `dotnet build` | 0 errors; 2× NU1903 high-severity advisory — BUG-133 |
| E8 | Parent-group filter vs `MapGroup("")` subgroup (scratch app) | filter **inherited** by subgroup — confirms BUG-106 mechanism |
