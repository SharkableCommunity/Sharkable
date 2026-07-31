# Changelog

All notable changes to Sharkable are documented here.

## [Unreleased]

### fix
- Idempotency: fingerprint is now computed from a buffered request body **before** the handler runs, so replaying a body-carrying request returns the cached response instead of a spurious `422 idempotency_key_conflict` (BUG-101).
- Idempotency: first-execution responses were delivered with an empty body (buffer position not reset before forwarding) — now forwarded correctly (BUG-102).
- Idempotency/rate limiting: `MaxEntries` on both memory stores now counts entries (was a byte budget, silently capping at ~39 keys / ~390 keys respectively) (BUG-107/109).
- Idempotency: request path hashing now uses UTF-8 instead of ASCII so non-ASCII paths cannot collide (BUG-108).
- Cron: `*/0` / `N/0` steps throw `FormatException` instead of hanging the host in an infinite loop; range-less steps (`5/10`) parse correctly (BUG-103).
- Cron: `GetNext` re-aligns to the seconds field on every minute boundary — fixed-second patterns (e.g. `30 * * * * *`) now fire on the correct second; never-matching patterns are memoized so the scheduler no longer burns ~2.1M iterations per tick (BUG-104).
- Cron: week field accepts `7` (Sunday) (BUG-144); `TriggerAsync` honors `SkipIfRunning`, observes failures and is cancelled on shutdown (BUG-128); scheduler loop backs off exponentially on persistent store errors (BUG-129); dead no-op loop removed (BUG-143).
- Endpoints: an empty `ApiPrefix` no longer bypasses the shared group filters (auto-wrap, validation, API-key, authorization interceptor) (BUG-105).
- Endpoints: `[SharkDontWrap]` now actually disables auto-wrap via endpoint metadata (nested groups inherit parent filters, so the previous subgroup approach was ineffective) (BUG-106).
- Endpoints: legacy `[SharkEndpoint(ApiPrefix: null)]` omits the prefix as documented; explicit custom prefixes are honored; old-style endpoints now respect `SharkOption.ApiPrefix`; dead `addPrefix`/`baseApiPath` fields removed (BUG-131/145).
- Endpoints: AutoCrud operations are read from the DI-resolved endpoint instance instead of `Activator.CreateInstance` (BUG-132).
- Middleware order: the rate limiter now runs **after** authentication (per-user key partitioning works) and the audit trail runs **before** the rate limiter (throttled requests are audited) (BUG-110/111).
- Audit: `AuditLogBuffer` drains queued entries on shutdown even when cancellation lands inside the wait; sink writes are awaited so async failures are logged; `Dispose` waits for the consumer (BUG-112/138).
- ETag: no longer throws on streamed/flushed responses (`HasStarted` guard), never overwrites an endpoint-set `Cache-Control`, and skips `text/event-stream` responses (BUG-113).
- Cache profile filter: `HasStarted` guard + no `Cache-Control` overwrite (BUG-114).
- Saga: compensation uses a fresh timeout per step (one slow step can no longer starve the remaining rollback) and failed compensation keeps persisted progress instead of deleting crash-recovery state (BUG-115/116).
- Warmup: timeout/failure now cancels sibling warmup tasks before disposing their CTSs and rethrows the inner exception (BUG-117).
- Validation: validators are resolved via a registration-time index instead of `MakeGenericType` on the request path; validation error responses serialize through the source-generated JSON context (AOT-safe) (BUG-118/119).
- AutoCrud: loud startup warning when the companion assembly cannot be loaded instead of silent no-op; `[RequiresDynamicCode]` annotation (BUG-120).
- OpenAPI: `[SharkOpenApiIgnore]` stripping now traverses `JsonTypeInfo` (AOT-safe) and actually removes required entries (dead ternary removed) (BUG-121).
- OpenAPI: auto-wrap document transformer now follows the effective `UseSharkOptions ?? SharkOption` flag, skips `[SharkDontWrap]`/`.DisableAutoWrap()` operations, and wraps `$ref` response schemas (BUG-130).
- Health: JWT authority check skips probing for non-HTTP(S) authorities (self-issued JWTs no longer make `/healthz` permanently unhealthy) and disposes the response (BUG-122/123).
- DI: `IUnifiedResultFactory` is registered with `TryAdd` and `ResolveFactory` consults DI, so user-registered factories win; attribute/marker DI no longer registers framework interfaces (`IDisposable`, `System.*`, `Microsoft.*`) (BUG-124/125).
- Options: removed the second `services.Configure<SharkOption>` registration that re-invoked the user's setup callback on a divergent instance — all framework reads now use the single static options object (BUG-126).
- Multi-tenant: apex domains are no longer treated as tenant subdomains (BUG-127).
- API-key validator reads from the static options instance (single source of truth) with reference-based cache refresh (BUG-126 follow-up).
- Unified results: `AsCreated` without a URI returns 201 (was 200); `AsUnauthorized`/`AsForbidden` emit the unified error envelope; every errors-only helper honors a custom status code (BUG-134/135/146).
- Profiler endpoint reuses the shared `ApiKeyValidator` instead of re-hashing keys per request (BUG-139).
- `SharkBackgroundService` health check no longer exposes full exception stack traces via `/healthz` (BUG-137).
- `AdaptiveLimitMonitor` registered via factory so the container disposes it (BUG-136).
- `AssemblyContext` refreshes its assembly set on repeated `AddShark` calls; added correctly-spelled `Shark.SetAssembly` (BUG-140/141).
- `SharkOption.WarmupTimeout` is now public (BUG-142).
- NativeTest: pinned `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 (fixes high-severity advisory GHSA-2m69-gcr7-jv3q); uses `AppContext.BaseDirectory` instead of `Assembly.Location` (IL3000).
- NativeAOT: the framework's internal endpoints (/healthz, /livez, /_sharkable/jobs, /_sharkable/profiler) no longer return `Task<IResult>`/anonymous types — they write responses directly via `HttpContext` with explicit source-generated `JsonTypeInfo`, so the app now starts and serves under NativeAOT (previously it crashed at startup). `UnifiedResultSourceContext` gained the corresponding `[JsonSerializable]` declarations (AOT follow-up findings).

## [0.7.4] — 2026-07-18

### fix
- `AddAutoCrud()` uses `Assembly.Load()` as fallback when the AutoCrud.SqlSugar assembly is not yet loaded (lazy NuGet loading). Fixes routes not being generated when the package is referenced but not yet JIT-loaded.

## [0.7.3] — 2026-07-18

### fix
- `ISharkEndpoint.AddRoutes` now has a default empty implementation — AutoCrud-only endpoints no longer need to declare an empty `AddRoutes()` method.

## [0.7.2] — 2026-07-18

### fix
- Register `SagaExecutor` as both concrete type AND `ISagaExecutor` interface in DI. Previously only `ISagaExecutor` was registered, causing `RequestDelegateGenerator` to treat `SagaExecutor` route parameters as JSON body (requiring `JsonTypeInfo` metadata). NativeTest now starts successfully.

## [0.7.1] — 2026-07-18

### fix
- `LoggingAuditSink` constructor no longer injects `AuditTrailOptions` from DI — reads from `Shark.SharkOption` instead. Fixes startup crash when audit trail is configured but `AuditTrailOptions` is not a registered DI service.

## [0.7.0] — 2026-07-18

### feat
- Plugin system: `ISharkPlugin` interface with three lifecycle hooks — `ConfigureServices` (AddShark), `ConfigurePipeline` (UseShark), `ConfigureOpenApi`. Three discovery paths: NuGet/assembly auto-scanning, hot-plug folder scanning (`opt.ConfigurePlugins(p => { p.ScanOnStartup = true; })`), and manual `opt.RegisterPlugin()`. Per-folder AssemblyLoadContext isolation. `opt.DisablePlugin("name")` for opt-out. AOT-safe for NuGet/manual paths; folder scanning is JIT-only.

### fix
- P1.1 — Dispose `AuditLogBuffer` after `FlushRemaining()` during shutdown — CTS leak eliminated
- P1.2 — Dispose `CronScheduler` `renewCts` after lock renewal task completes
- P1.3 — Unify `ResponseSizeExceededException` — single shared type instead of two duplicate inner-class definitions in ETag and Idempotency middleware
- P1.4 — Move exception handler to end of pipeline (just before `MapEndpoints`) — catches exceptions from ALL middleware, not just auth and beyond
- P1.5 — Guard `AutoCrudExtension` dynamic assembly loading behind `AotMode` check — prevents reflection-based plugin discovery in AOT

### refactor
- P2.1 — Extract `ISagaExecutor` interface; register via `TryAddSingleton<ISagaExecutor, SagaExecutor>`; add `SagaExecutorFactory` on `SharkOption`
- P2.2 — Expose `CronLockTtl` on `ICronScheduler` interface (was only on concrete `CronScheduler`)
- P2.3 — Normalize `ConfigureJwt` to `ConfigureJwt(Action<JwtOptions>)` pattern (consistent with every other `ConfigureXxx` method). Old `ConfigureJwt(string, string[], Action<JwtBearerOptions>?)` marked `[Obsolete]`.
- P2.4 — Normalize `ConfigureAuthorization` from property to method `ConfigureAuthorization(Action<AuthorizationOptions>)` (consistent with `ConfigureRateLimiter`, `ConfigureOutputCache`, etc.)
- P2.5 — `ICronJobStore.RenewJobLockAsync` has default implementation `=> Task.CompletedTask` (matching `ISagaStore.RenewLockAsync` pattern)
- P2.6 — Seal `SqlSugarOptions`, `Shark`, `AssemblyContext`, `UnifiedResult<T>` — public classes not designed for inheritance
- P2.7 — Remove dead code: `Utils.SetupModules()` (never called), obsolete `.SharkRequestTimeout(int, string?)` DSL overload

### feat
- P3.1 — Health checks enabled by default (`EnableHealthChecks = true`) — every deployed app gets `/healthz` out of the box
- P3.2 — Cron job concurrency default changed to `SkipIfRunning` (was `AllowConcurrent`) — safer default, prevents overlapping runs
- P3.3 — Startup warning when `UnifiedResultFactory` is set without `WrapSchemaFactory` — prevents OpenAPI schema mismatch
- P3.4 — `IUnifiedResult` XML docs cross-reference `UnifiedResult<T>` and `IUnifiedResultFactory` with usage guidance and `WrapSchemaFactory` tip

### feat
- **FEAT-01** — Security headers middleware (`ConfigureSecurityHeaders()`) — opt-in support for `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Content-Security-Policy`, `Permissions-Policy`, `Strict-Transport-Security`, `Cross-Origin-Resource-Policy`, `Cross-Origin-Opener-Policy`, `Cross-Origin-Embedder-Policy`
- **FEAT-02** — Framework metrics via `System.Diagnostics.Metrics` — `ISharkMetrics` interface with `MetricsFactory` on `SharkOption` (consistent with existing `IdempotencyStoreFactory`/`RateLimitStoreFactory` patterns); default `SharkMetrics` uses `Meter("Sharkable")` with counters for requests, rate-limit rejections, idempotency hit/miss/conflict, auth failures, audit drops, cron runs/failures, saga completed/compensated. Configurable via `ConfigureMetrics()`. Zero dependencies, AOT-compatible.
- **FEAT-03** — `IAuditSink` abstraction — pluggable audit sink for shipping entries to Seq/Elastic/Kafka. `AuditSinkFactory` on `SharkOption`, default `LoggingAuditSink` preserves the current structured-logging behavior. `AuditLogEntry` record made public.
- **FEAT-05** — Per-endpoint distributed rate-limit policies — `[SharkRateLimit(limit, windowSeconds)]` attribute on `ISharkEndpoint` classes, `.SharkRateLimit(limit, windowSeconds)` DSL, `SharkRateLimitMetadata` read by `SharkRateLimiterMiddleware` overriding global defaults.
- **FEAT-06** — Per-endpoint idempotency via `[SharkIdempotent(ttlSeconds)]` attribute — opt-in idempotency per endpoint when global is disabled. `[SharkNoIdempotency]` attribute unscoped (already supported per-class). Middleware now checks `SharkIdempotentMetadata`.
- **FEAT-07** — Response cache profile via `[SharkCacheProfile(durationSeconds, varyByHeader, privateOnly)]` attribute — emits `Cache-Control` and optional `Vary` headers per endpoint group via `SharkCacheProfileFilter`. Composes with ETag and Output Cache.
- **FEAT-08** — Request-timeout DSL — `opt.RequestTimeoutsConfigure` + `.SharkRequestTimeout(policyName)` + `.SharkRequestTimeout(ms)` DSL. Thin wrapper over ASP.NET Core `AddRequestTimeouts`. `DefaultRequestTimeoutPolicy` for global fallback.
- **FEAT-09** — Unified validation error shape — `ValidationErrorMode.Messages` (default, joins with `"; "`) and `ValidationErrorMode.ProblemDetails` (RFC 7807 with `errors` object mapping field → messages). Configurable via `SharkOption.ValidationErrorMode`.
- **FEAT-10** — Parallel warmup services — `ConfigureWarmup<T>()` now callable multiple times; all registered `IWarmupService` instances run in parallel with individual timeouts during startup. Aggregate failures propagated via `AggregateException`.
- **FEAT-11** — `Sharkable.Testing` NuGet package — `SharkTestFactory<TEntryPoint>` base `WebApplicationFactory`, `FakeIdempotencyStore`, `FakeRateLimitStore`, `UnifiedResultAssertions` for fluent test assertions. Separate package.
- **FEAT-12** — `SharkOption.GroupConvention` (per-group) + `EndpointConvention` (per-endpoint) — single place for auth policies, filters, `ProducesResponseType` conventions.
- **FEAT-13** — OpenAPI ergonomics — `AddOpenApiOperationTransformer()` / `AddOpenApiSchemaTransformer()` methods on `SharkOption` (no need to touch `OpenApiOptions` directly). `OpenApiExampleFactory` hook for example generation.
- **FEAT-15** — `IApiKeyValidator` interface with `ApiKeyValidationResult` — carries claims/roles + optional per-key `RateLimitMultiplier`. `DefaultApiKeyValidator` wraps the existing static-array behavior. `ApiKeyFilter` now accepts the custom validator via DI.
- **FEAT-14** — Source generator for discovery (tracked as separate RFC; not yet implemented)
- Add `ISingleton`, `IScoped`, `ITransient` marker interfaces — classes implementing these are auto-registered in DI (same as `[SingletonService]` etc.)

### perf
- **PERF-01** — `DefaultUnifiedResultFactory.Instance` singleton; `UnifiedResultFactoryHelper.ResolveFactory()` caches configured factory at 5 hot-path call sites (P1)
- **PERF-02** — Source-generated JSON context defaults to `WriteIndented=false` for compact production output (P1)
- **PERF-04** — Audit middleware caches `_redactQueryParams` HashSet in ctor instead of allocating per request (P2)
- **PERF-05** — Audit middleware checks `_logger.IsEnabled()` before building log strings, avoiding allocations when the category is filtered out (P2)
- **PERF-06** — Shared `ApiKeyValidator` service caches stored-key hashes and invalidates via `IOptionsMonitor.OnChange`, replacing O(keys) SHA-256 per request (P2)
- **PERF-07** — Validation filter caches `ConcurrentDictionary<Type, Func<IServiceProvider, IValidator?>>` per argument type, avoiding `MakeGenericType` + DI lookup per request (P2)
- **PERF-08** — ETag middleware uses `Convert.ToHexString()` instead of per-byte `StringBuilder` + `ToString("x2")` — one allocation instead of 33 (P3)
- **PERF-09** — Idempotency middleware uses `HashSet<string>` for method check and `TryGetBuffer()` to avoid double body copy (P3)
- **PERF-10** — Profiler `TrackMemory` option (default `false`) gates per-request `GC.GetTotalMemory(false)` calls (P3)
- **PERF-11** — `HttpResponseExtension.WriteJsonAsync` caches static `JsonSerializerOptions` instead of allocating per call (P3)

### fix (memory & lifetime)
- **MEM-01** — `MemoryIdempotencyStore` now owns a private `MemoryCache` instead of hijacking the app-wide `IMemoryCache` registration (P1)
- **MEM-02** — `AdaptiveLimitMonitor` registered as singleton in DI with `autoStart:true`; DI handles disposal of the timer + process handle (P2)
- **MEM-03** — `AuditLogBuffer` consumer survives flush failures with try/catch + `Interlocked`-tracked dropped-entry counter (P2)
- **MEM-04** — `MemoryRateLimitStore` and `MemoryIdempotencyStore` implement `IDisposable` so `MemoryCache` expiration-scan timers are properly disposed (P2)
- **MEM-07** — ETag `CountingResponseBody` disposes its internal `MemoryStream` spool; `using var counting` in middleware (P3)

### security
- **SEC-01** — `HealthCheckDetailLevel` enum (`StatusOnly`/`Description`/`Full`); `/healthz` defaults to `StatusOnly` in non-Development, never leaking `Exception.Message` (P1)
- **SEC-02** — `ExceptionHandlerOptions.IncludeExceptionMessage` (default `false`); production error responses return generic `"An error occurred."` (P2)
- **SEC-03** — Startup warning when OpenAPI/Scalar is enabled outside Development (P2)
- **SEC-04** — Admin endpoints (`/_sharkable/jobs`, profiler gate) unified on shared `ApiKeyValidator` service with hot-reload support (P3)
- **SEC-05** — Configuration validator warns when rate limiting is enabled without proxy ForwardedHeaders configuration (P3)
- **SEC-06** — `JwtHealthCheck` caches result for 30 seconds to avoid hammering OIDC discovery endpoint on k8s probes (P3)

### fix
- `UnifiedResultExtension` and `UnifiedResult` static methods now respect `UnifiedResultFactory` — custom factory implementations are used for all `.AsNotFound()`, `UnifiedResult.Ok()`, etc.
- **BUG-01** — Cron shutdown cancellation token wired into `ExecuteJobAsync` so in-flight jobs are cancelled on `app.StopAsync()` (P1)
- **BUG-02** — Exception handler no longer logs client disconnects as errors and avoids writing to already-started responses (P1)
- **BUG-03** — Saga compensation now runs with its own CTS (`CompensationTimeout`, default 60s) — never cancelled by the execution token (P1)
- **BUG-04** — Cron job timeouts are recorded as "Job timed out" with zero retries; retry delay now respects shutdown via linked token (P2)
- **BUG-05** — Graceful shutdown middleware honors configured `ShutdownStatusCode` instead of hard-coded 503 (P2)
- **BUG-06** — Auto-wrap docs fixed; `UseSharkOptions.EnableAutoWrap` is now nullable tri-state overriding `SharkOption.EnableAutoWrap`; `[SharkDontWrap]` evaluated per endpoint class instead of per merged group (P2)
- **BUG-07** — `MemoryRateLimitStore` now supports `MaxEntries = -1` to disable the cap (uncapped `MemoryCache`) (P2)
- **BUG-08** — Route formatting uses `ToLowerInvariant()` / `char.ToLowerInvariant()` instead of culture-sensitive `ToLower()` (P2)
- **BUG-09** — Rate-limit and idempotency `Retry-After` headers formatted with `CultureInfo.InvariantCulture` (P2)
- **BUG-10** — Add `[SharkNoIdempotency]` attribute; idempotency middleware skips response buffering for marked streaming/SSE endpoints (P2)
- **BUG-11** — Cron job state mutations (`IsRunning`, `LastRun`, `LastError`, etc.) now guarded with `_entries` lock (P3)
- **BUG-12** — Legacy attribute mapper copies formatted values to locals instead of mutating runtime-cached attribute instances (P3)

## [0.6.1] — 2026-07-15

### feat
- EnableAutoWrap defaults to true — all plain return values wrapped in UnifiedResult<T> out of the box
- Custom IResult (UnifiedResultResult) eliminates one allocation per wrapped response and syncs HTTP status code
- Per-endpoint opt-out via [SharkDontWrap] attribute (class-level) and .DisableAutoWrap() fluent API (route-level)

### refactor
- UnifiedResultWrapFilter checks endpoint metadata for DisableAutoWrapMetadata before wrapping
- ProblemDetailsResult reuses UnifiedResultResult for consistent error response writing

## [0.6.0] — 2026-07-15

### feat

- Add unified result extension methods and static factories for 20 HTTP status codes (202, 405, 406, 410, 415, 422, 429, 500–504)
- Add generic `AsStatus()` extension and `UnifiedResult.Status<T>()` static factory for custom `HttpStatusCode` — covers any status code not explicitly enumerated
- Add parameterless `ConfigureAuditTrail()` overload — enables audit trail with default options without requiring a callback
- Add `AuditTrailFormat` enum with `Default`, `DotnetLogger`, `JsonStyle`, and `Compact` presets — configurable via `AuditTrailOptions.LogFormat`
- Add `SharkOption.ShowStartupBanner` (default `true`) — allows disabling the startup banner

### feat (Phase 5 — Lifecycle & startup integrity)

- **#25 Startup banner**: Sharkable now prints a formatted banner (version, environment, UTC timestamp) to the console at the end of `UseShark()`
- **#20 Lifecycle hooks**: `SharkOption.ConfigureOnStarted()` / `ConfigureOnStopped()` — register callbacks that fire on `IHostApplicationLifetime.ApplicationStarted` / `ApplicationStopping`
- **#22 Readiness gate**: `InternalShark.StartupCompleted` opens after all wiring completes; `/healthz` returns 503 until then
- **#23 Warmup**: `IWarmupService` interface + `SharkOption.ConfigureWarmup<T>()` — resolves and runs a warmup task synchronously before the readiness gate opens (30s timeout)
- **#27 Eager singleton**: `[SingletonService(Eager = true)]` — eagerly resolves the singleton from DI during `UseShark()`, before the server accepts requests
- **#26 Pipeline injection points**: `UseSharkOptions.AddBeforeAuth()` / `AddAfterAuth()` / `AddAfterEndpoints()` — inject custom middleware at specific pipeline positions
- **#21 Liveness probe**: `/livez` endpoint mapped alongside `/healthz` — returns `{"status":"alive"}` unconditionally, not blocked by readiness/shutdown gates
- **#24 DI validation**: `SharkOption.ValidateOnStart<T>()` — calls `GetRequiredService<T>()` during `UseShark()`, fails fast on missing registrations

### docs

- Document all unified result extension methods and static factories in EN + ZH
- Add service registration guide covering `[ScopedService]`, `[TransientService]`, `[SingletonService]` attributes (EN + ZH)
- Add lifecycle hooks, startup banner, warmup, eager singleton, pipeline injection, DI validation guide (EN + ZH)
- Update health checks guide with readiness gate and liveness probe details (EN + ZH)

## [0.5.7] — 2026-07-11

### feat

- Add `SharkOption.DefaultApiVersion` — sets a default version segment in URL paths for all endpoints without `[SharkVersion]`. E.g., `DefaultApiVersion = "v2"` produces `/api/v2/group/route`. Endpoints with explicit `[SharkVersion("v1")]` retain their declared version.
- Auto-inject `[Authorize]` metadata on all endpoints when `RequireAuthenticatedByDefault` is enabled (replaces FallbackPolicy approach)
- `UseAuthorization()` now runs whenever `EnableAuthorization` is `true`, regardless of JWT configuration

### fix

- Route patterns in `AddRoutes()` now respect `EndpointFormat` (SnakeCase/CamelCase). Parameterized routes (`{id:int}`) are preserved as-is.
- Add OpenAPI operation transformer so `[SharkDeprecated]` correctly sets `deprecated: true` on generated operations

## [0.5.6] — 2026-07-03

### feat

- Upgrade `Scalar.AspNetCore` to 2.16.8

### fix

- Null-coalesce `stepResult.Error` in `SagaExecutor` — fix CS8604 nullable warning in `CompensateAsync` call
- Add XML doc comments to all public API types and members — fix CS1591 warnings across 17 files; enable `GenerateDocumentationFile=true`
- Fix CS1574 unresolved cref references in XML docs across SagaTypes, RedactingLogOptions, IIdempotencyStore, SharkOption, TracingOptions, MemorySagaStore
- Replace anonymous types with concrete records in `HealthCheckEndpoint` — enable AOT-compatible serialization; add public `HealthCheckResponse`/`HealthCheckEntry` types

### chore

- Remove deprecated `Sharkable.Sample` and `Sharkable.AotSample` projects — consolidate AOT sample into `Sharkable.NativeTest`
- Rewrite `Sharkable.NativeTest` as a complete shopping website backend (auth, products, cart, orders/SAGA, admin) with AOT-safe source-generated JSON serialization

## [0.5.5] — 2026-07-03

### security

- Add `MaxFingerprintBodySize` (default 64 KiB) to `SharkIdempotencyOptions` — prevent OOM via attacker-controlled `Content-Length` header in idempotency middleware fingerprinting. Fixed chunked-transfer bypass where all `Transfer-Encoding: chunked` requests hashed to the same fingerprint (now hashes body incrementally with `-1` sentinel for unknown length); setter rejects `<= 0`
- Replace `Thread.Sleep` polling with `await Task.Delay` in graceful shutdown drain — prevent ApplicationStopping thread block (SHARK-SEC-003). Initial fix used `.GetAwaiter().GetResult()` on the drain task which still blocked the callback; corrected to true fire-and-forget so `ApplicationStopping` returns immediately.
- Make `SagaExecutor.LockTtl` configurable; add `LockRenewalInterval` with periodic lock extension — prevent split-brain sagas when step duration exceeds lock TTL (SHARK-SEC-004, cross-repo with `Sharkable.Cache.Redis`). `ISagaStore.RenewLockAsync` converted from a required interface member to a default interface method (DIM) so existing third-party implementations keep compiling unchanged.
- Replace unconditional `KeyDelete` in `RedisSagaStore.ReleaseLockAsync` and `RedisCronJobStore.ReleaseJobLockAsync` with check-and-delete Lua script — prevent split-brain when LockTtl expires mid-work (SHARK-SEC-005, cross-repo with `Sharkable.Cache.Redis`)
- Add `ICronJobStore.RenewJobLockAsync` plus `CronScheduler.CronLockTtl` (default 10 min) and a background renewal task mirroring `SagaExecutor` — prevent split-brain cron jobs when job duration exceeds lock TTL (SHARK-SEC-005 follow-up, cross-repo with `Sharkable.Cache.Redis`)
- **BREAKING**: Add `[CrudAllow]` attribute for explicit field allowlist on AutoCrud insertable/updateable — endpoints with `Create | Update` enabled and zero `[CrudAllow]` properties now throw `InvalidOperationException` at startup. Existing entities must mark every writable field with `[CrudAllow]`. Prevents mass-assignment privilege escalation (SHARK-SEC-006)
- **BREAKING (SHARK-SEC-006 follow-up)**: Exclude the configured soft-delete column (`SqlSugarOptions.SoftDeleteFieldName`, default `"IsDeleted"`) from the `[CrudAllow]` allow-list even when explicitly marked — otherwise an attacker can revive soft-deleted rows by sending the field in a PUT body. Honors `[SugarColumn(ColumnName = "...")]` renames
- **BREAKING (SHARK-SEC-006 follow-up)**: AutoCrud `POST /` and `PUT /{id}` now return the persisted row re-read from the database instead of the user-controlled request body — previous behavior silently hid server-side defaults (timestamps, identity-generated PK, server-set soft-delete state) from the client
- Require API key on profiler endpoint `/_sharkable/profiler` by default; return 404 if no API keys configured (SHARK-SEC-015). Adds `SharkOption.ProfilerRequireApiKey` (default `true`); also caps the `top` slow-requests surface at 50 to bound data exposure
- Require API key on cron admin endpoint `/_sharkable/jobs`; redact `LastError` field to its first 100 characters + `...` to prevent business-logic leakage (SHARK-SEC-016). Adds `SharkOption.CronAdminRequireApiKey` (default `true`); returns 404 if no API keys are configured
- **BREAKING**: Make `CronScheduler.Register` async — eliminate sync-over-async deadlock risk with distributed stores (SHARK-SEC-017). `ICronScheduler.Register` is replaced by `RegisterAsync` returning `Task`; `SharkOption.ConfigureCronJobs` callback type changes from `Action<ICronScheduler>` to `Func<ICronScheduler, Task>` so the hosted service can await it without blocking on startup. Internal `await _store.LoadStateAsync(...)` replaces `.GetAwaiter().GetResult()`.
- Add `SharkOption.RequireAuthenticatedByDefault` opt-in flag — enforce auth on framework endpoints via fallback policy (SHARK-SEC-011, closes both H-4 and H-6)
- Add `ETagOptions.MaxResponseSize` (default 10 MiB) + counting stream + incremental hashing — prevent OOM via huge response bodies (SHARK-SEC-012)
- Add `IMemoryCache` SizeLimit (100k) + periodic eviction sweep to `MemoryRateLimitStore` — prevent slow-loris DoS via unique path explosion (SHARK-SEC-013)
- Add `IMemoryCache` SizeLimit (10k) + entry.Size tracking to `MemoryIdempotencyStore` — prevent TB-DoS via unique idempotency keys (SHARK-SEC-014)
- Add JWT algorithm allowlist + `RequireSignedTokens` + `RequireExpirationTime` + reduce `ClockSkew` to 30s — prevent algorithm confusion attacks (SHARK-SEC-007)
- Use `CryptographicOperations.FixedTimeEquals` for API key comparison — prevent timing oracle (SHARK-SEC-008)
- Gate `ScalarJwtToken` / `ScalarApiKeyValue` to `IHostEnvironment.IsDevelopment()` — prevent token leakage to public `/scalar/v1` UI (SHARK-SEC-009)
- Implement `AuditTrailMiddleware` header redaction per `RedactHeaders` list — credential-bearing headers (`Authorization`, `X-Api-Key`, `Cookie` by default) have their values replaced with `***` in audit log output. Header names preserved so reviewers see which credentials were presented (SHARK-SEC-010)
- Redact RedisHealthCheck description — never expose topology or exception messages on public `/healthz` (SHARK-SEC-018)
- Validate connection string at AddSharkableRedis — null-check + default `abortConnect=false` + optional TLS enforcement (SHARK-SEC-019)
- Replace `JsonSerializer.Serialize<T>` with source-generated `JsonSerializerContext` in `RedisIdempotencyStore` — Cache.Redis is now AOT-compatible (SHARK-SEC-020 follow-up, cross-repo with `Sharkable.Cache.Redis`)
- Remove auto-registration of `RedisHealthCheck` as `IHealthCheck` in `AddSharkableRedis` — `UseSharkableRedisHealthCheck()` is the only way to wire it (SHARK-SEC-021 follow-up, cross-repo with `Sharkable.Cache.Redis`)
- Connection string: only override `abortConnect=false` when the key is absent — respect an explicit `abortConnect=true` (SHARK-SEC-019 follow-up, cross-repo with `Sharkable.Cache.Redis`)
- Replace empty catch in RedisIdempotencyStore deserialization with typed exception handling + tombstone record — prevent silent double-execution on corruption (SHARK-SEC-020)
- Add `UseSharkableRedisHealthCheck()` extension — explicit opt-in to wire health check into `/healthz` (SHARK-SEC-021)
- Set TTL on RedisSagaStore progress records via `RedisStoreOptions.SagaProgressTtl` (default 7d) — prevent unbounded memory growth (SHARK-SEC-022)
- Add `AutoCrudSqlSugar.AutoCrudRequireAuthorization` opt-in flag — auto-attach `.RequireAuthorization()` to generated CRUD endpoints. Default `false` for backward compat; production deployments MUST enable. (SHARK-SEC-023, cross-repo with `Sharkable.AutoCrud.SqlSugar`)
- Defense-in-depth: also exclude `SafeSoftDeleteField` from `[CrudAllow]` allow-list by case-insensitive name match — protects against entities whose C# property name matches the configured soft-delete column but lacks `[SugarColumn]` rename (SHARK-SEC-024, cross-repo with `Sharkable.AutoCrud.SqlSugar`)
- Add `AutoCrudSqlSugar.MaxPageNumber` (default 1M) + overflow check on `(page - 1) * pageSize` — prevent pagination DoS via `page=int.MaxValue` producing a negative OFFSET (SHARK-SEC-025, cross-repo with `Sharkable.AutoCrud.SqlSugar`)
- Redact `SqlSugarHealthCheck` description — never expose `dbType` or `ex.Message` on public `/healthz`; full diagnostic detail logged at `LogWarning` for operators only (SHARK-SEC-026, cross-repo with `Sharkable.AutoCrud.SqlSugar`)
- Escape SQL `LIKE` wildcards + cap filter value length (200) + cap `IN` array size (100) in AutoCrud search — prevent LIKE wildcard DoS and large-IN clause DoS (SHARK-SEC-027, cross-repo with `Sharkable.AutoCrud.SqlSugar`)
- Fix ETag `CountingResponseBody.FlushAsync` duplicate body write on over-cap responses (SHARK-SEC-012 follow-up)
- Fix `MemoryRateLimitStore.IncrementAsync` non-atomic increment allowing concurrent bypass (SHARK-SEC-013 follow-up)
- Replace `AuditTrailMiddleware.CaptureHeaders` `JsonSerializer.Serialize` with hand-rolled formatter — AOT-compatible (SHARK-SEC-010 follow-up)
- Make JWT audience validation mandatory when `ConfigureJwt` is called — reject empty audience list at config time (SHARK-SEC-007 follow-up)
- Strip assembly-qualified type name from `ExceptionHandlerOptions.GetErrorMessage` dev output — prevent fingerprinting of `Sharkable.X.Y, Version=1.0.0.0` over the wire (SHARK-SEC-M003)
- Redact `JwtHealthCheck` description on `/healthz` — replace authority URL + `ex.Message` echoes with generic descriptions; full URL + exception now surface via `ILogger.LogWarning` for operators only (SHARK-SEC-M004)
- Pin `CultureInfo.InvariantCulture` for `string.Format` in `HttpContext.Localize(key, args)` — prevent culture-dependent format-string growth attacks via malicious translations (SHARK-SEC-M005)
- Implement `IDisposable` on `AdaptiveLimitMonitor` + wrap `Adjust()` in try/catch — prevent timer-callback exception tearing the process down + close the `Process` handle leak (SHARK-SEC-M010)
- Cap `CronExpression.GetNext` iteration count at 2.2 M — bound CPU on non-matching patterns (e.g. `0 0 30 2 *`) that previously looped 2.1 M times per hosted-service tick (SHARK-SEC-M011)
- Link `SharkCronHostedService` host stoppingToken to a per-job `CancellationTokenSource` — prevent orphaned long-running cron jobs surviving `app.StopAsync()` and tripping k8s termination grace periods (SHARK-SEC-M012)
- Collapse `CronScheduler` three correlated dictionaries (`_jobs` / `_states` / `_expressions`) into one `Dictionary<string, JobEntry>` under a single lock — eliminate TOCTOU between `Register` and `GetDueJobsAsync` (SHARK-SEC-M013)
- Bound `/healthz` aggregate `HealthCheckService.CheckHealthAsync` with a 10 s `CancellationTokenSource` — prevent a single hung check from keeping the endpoint open and tripping k8s probes (SHARK-SEC-M015)
- `JwtHealthCheck` now accepts `IHttpClientFactory` via DI — reuse a pooled `HttpMessageHandler` instead of opening a new socket per probe (SHARK-SEC-M016)
- Default rate-limit partition key includes the authenticated `User.Identity.Name` when present, falling back to `RemoteIpAddress` — prevent shared-NAT / proxy-IP DoS or IP-rotation bypass (SHARK-SEC-M017)
- Invoke `JwtConfigure` BEFORE applying framework `TokenValidationParameters` defaults — user callbacks that mutate properties in place (the documented contract) now compose correctly; framework safety (algorithm allowlist, 30 s `ClockSkew`, `RequireSignedTokens`, `RequireExpirationTime`) is re-applied on the final instance regardless of user replacement (SHARK-SEC-M019)
- Default `AuditTrailOptions.ForwardCorrelationId` to `false` + validate inbound correlation id against `[A-Za-z0-9._-]{1,128}` — prevent log injection via `X-Correlation-Id: foo\n[CRITICAL] admin login OK` (SHARK-SEC-M020)
- Include the authenticated user identity (`User.Identity.Name` / `sub` claim) in `SharkIdempotencyMiddleware` fingerprint — prevent cross-user replay when two authenticated users share the same `Idempotency-Key` + body. Tests updated for the new signature (SHARK-SEC-M021)
- Replace unbounded `MemoryStream` for the idempotency response body with a `CountingResponseBody` wrapper that throws `ResponseSizeExceededException` at `MaxResponseSize` — peak allocation is now bounded by the cap instead of by the attacker-controlled response size (SHARK-SEC-M008)
- Expand `ConfigurationValidator` to validate header names (`CorrelationIdHeader`, `HeaderPrefix`, `ReplayedHeaderName`, `Idempotency-Key`, `ApiKeyHeaderName`) against `[A-Za-z0-9._-]+` — block CRLF-injection via appsettings.json. Try-compile regex patterns + validate RateLimiter / ETag / AuditTrail / Idempotency numeric/timespan ranges at startup (SHARK-SEC-M001, M002, L017)
- Cap `AuditTrailMiddleware` query-string length at 4 KiB before the redact pass — prevent multi-MB `?x=` strings bloating every audit log line (SHARK-SEC-M009)
- Default `JsonHelper` `HttpResponseExtension.WriteJsonAsync` to `WriteIndented=false` — compact JSON is ~2x smaller on the wire (SHARK-SEC-L001)
- Enforce `SharkIdempotencyOptions.MinKeyLength = 16` (IETF draft) on `Idempotency-Key` — prevent attackers from pre-burning the short-key space (`a`, `b`, `aa`, ...) and filling the in-memory store (SHARK-SEC-L002)
- `ApiKeyFilter` now injects `IOptionsMonitor<SharkOption>` and re-reads `ApiKeys` on every invocation — hot-reload via configuration change now takes effect immediately (SHARK-SEC-L003)
- Add `[SharkOpenApiIgnore]` property attribute + schema transformer — properties carrying the marker are stripped from every generated OpenAPI schema, preventing `Password` / `RefreshToken` / `ApiSecret` from appearing in `/openapi/v1.json` (SHARK-SEC-L009)
- Parse `ETagMiddleware` `If-None-Match` per RFC 9110 §13.1.2 — split comma-separated candidates, honor weak `W/"..."` prefix, and accept `*` wildcard. The previous `Trim('"')` on the whole header only worked for a single strong candidate (SHARK-SEC-L011)
- Narrow the empty `catch {}` in `EndPointExtension` AutoCrud marker lookup to `TypeLoadException` / `TargetInvocationException` / `MissingMethodException` — anything else (OOM, MemberAccess, …) propagates so a real startup failure stays visible. Logs via `ILogger.LogDebug` (SHARK-SEC-L019, L022)
- `SharkBackgroundService.LastError = ex.ToString()` instead of `ex.Message` — preserve inner exception stack so operators see the root cause (SHARK-SEC-L024)
- `RedactingLogger` now redacts structurally by walking the `{OriginalFormat}` template and substituting `{Key}` placeholders — substring-search over-redaction (`password="p4ss"` + unrelated "the password is p4ss" both rewritten) and under-redaction (JSON-escaped values) fixed (SHARK-SEC-M006)
- Cache compiled default-pattern `Regex` (`GroupNameSuffixRegex`, `VersionFormatRegex`) as static fields with `RegexOptions.Compiled` + 100 ms timeout — no re-parse per call (SHARK-SEC-L012)
- Add `TenantOptions.AllowedHosts` + check in `TenantResolver.FromHost` — when set, mismatched inbound `Host` headers cannot be used to spoof the tenant (SHARK-SEC-L007)
- `AuditLogBuffer` consumer logs a debug line on shutdown — distinguish graceful cancellation from a misbehaving loop (SHARK-SEC-L020)
- Pin `Microsoft.OpenApi` to 2.7.5 — suppress NU1903 (CVE-2026-49451, circular schema stack overflow)

### feat

- Add `UnifiedResult<T>` AOT preservation — Source Generator auto-emits `typeof(UnifiedResult<T>)` for all endpoint return types

## [0.5.4] — 2026-06-30

### security

- Add 100ms regex timeout to `FormatAsGroupName` / `GetVersionFormat` — prevent ReDoS via malicious `GroupNameSuffixPattern` / `VersionFormatPattern`
- Add `SafeSoftDeleteField` validation in `AutoCrudGenerator` — reject non-alphanumeric field names to prevent SQL injection

## [0.5.3] — 2026-06-30

### feat

- Add `HttpContext.Localize(string key, params object[] args)` overload — format args in localized strings
- Add `HttpContext.GetCulture()` extension — resolve client culture from `Accept-Language` header
- `ApiKeyFilter` automatically skipped when `AuthorizationInterceptorFactory` is set — interceptor owns auth entirely
- Add `SqlSugarOptions.MaxPageSize` / `DefaultPageSize` — configurable AutoCrud pagination limits (defaults: 100 / 20)
- Add `SqlSugarOptions.SoftDeleteFieldName` — configurable soft delete field (default: `"IsDeleted"`)

### security

- Add 100ms regex timeout to `FormatAsGroupName` / `GetVersionFormat` — prevent ReDoS via malicious `GroupNameSuffixPattern` / `VersionFormatPattern`
- Add `SafeSoftDeleteField` validation in `AutoCrudGenerator` — reject non-alphanumeric field names to prevent SQL injection

## [0.5.2] — 2026-06-30

### feat

- Add `SharkOption.ETagOptions.CacheableMethods` / `CacheControlHeader` / `ShouldSkipStatus` — ETag method set, cache header, and cacheable status logic are now configurable
- Add `SharkOption.ApiKeyHeaderName` — configurable API key request header
- Add `GracefulShutdownOptions.ShutdownStatusCode` / `DrainPollingInterval` — configurable 503 status code and drain polling interval
- Add `SharkOption.ProblemDetailsTypeFactory` / `ProblemDetailsTitleFactory` — delegates for customizing RFC 7807 `type` URI and `title`
- Add `SharkOption.DefaultCulture` — configurable default locale for error localizer
- Add `SharkRateLimiterOptions.AdaptiveGcHighThreshold` / `AdaptiveGcLowThreshold` / `AdaptiveReductionDivisor` — GC pressure thresholds and reduction factor for adaptive rate limiting
- Add `ProfilerOptions.MaxEntries` — configurable profiler ring buffer size
- Add `SharkIdempotencyOptions.RetryAfterSeconds` / `ShouldCacheStatus` — configurable Retry-After and cacheable status predicate
- Add `SharkOption.HealthCheckPath` — configurable health check endpoint path
- Add `TracingOptions.ActivitySourceName` — configurable `ActivitySource` name (separate from `ServiceName`)
- Add `SharkOption.GroupNameSuffixPattern` / `VersionFormatPattern` / `VersionFormatReplacement` — customizable endpoint URL naming regexes
- Remove hardcoded `"Sharkable"` ActivitySourceName in `TracingMiddleware` — now uses `TracingOptions.ActivitySourceName`
- Add `HttpContext.Localize(string key)` extension method — easy localization in user endpoints
- Add `LocalizationExtensions` — public scaffolding for endpoint-level error translation

### docs

- Add complete error localization guide with `IErrorLocalizer` implementation example, `HttpContext.Localize()` usage, and middleware integration pattern (EN + ZH)

## [0.5.1] — 2026-06-29

### fix

- Fix `InvalidOperationException` when `UseAuthorization` is called without `AddAuthorization` — register `AddAuthorization` by default (configurable via `EnableAuthorization`), with factory support via `ConfigureAuthorization` delegate
- Fix `UnifiedResultWrapFilter` always wrapping with status 200 — now uses actual `HttpContext.Response.StatusCode`
- Fix `AuditTrailMiddleware` response header hardcoded to `"X-Correlation-Id"` — now respects `AuditTrailOptions.CorrelationIdHeader`

### feat

- Add `SharkOption.EnableAuthorization` (default `true`) — allows users to opt out of authorization service registration
- Add `SharkOption.ConfigureAuthorization` — delegate for customizing `AuthorizationOptions` (policies, fallback, etc.)
- Add `SharkOption.ScalarJwtToken` / `SharkOption.ScalarApiKeyValue` — configure pre-filled credentials in Scalar UI

## [0.3.0] — 2026-06-27

### feat

- Add `RedactingFormatter` — replaces `ILogger<T>` with a structured-log wrapper that redacts configured sensitive fields (password, secret, token, etc.)
- Add multi-tenant support — `ITenant` scoped service, `TenantResolutionMiddleware`, `TenantResolver` helpers (`FromHost` / `FromClaim` / delegate)
- Add `WrapSchemaFactory` to `SharkOption` — lets custom `IUnifiedResultFactory` users match the OpenAPI schema to their actual response shape
- Add `ConfigureScalar()` to `SharkOption` — custom title, theme, layout, and auto-detected Bearer/API Key auth in Scalar UI
- Add `SharkDescriptionAttribute` — class-level OpenAPI summary/description for `ISharkEndpoint` endpoints
- Add `SharkResponseTypeAttribute` — class-level response metadata for `ISharkEndpoint` endpoints
- Add `SharkDeprecatedAttribute` — marks `ISharkEndpoint` endpoints as deprecated via `ObsoleteAttribute` on endpoint metadata
- Add `[RequiresDynamicCode]` to `SharkEndpointAttribute` — AOT compile-time warning when using old-style endpoints

### test

- Add tests for MultiTenant, ValidationFilter, RedactingLogger, and the new metadata attributes

### fix

- Adapt to `Microsoft.OpenApi` v2 breaking changes — flat namespace, `JsonSchemaType` enum, `IOpenApiSchema` dictionary value type
- Fix `SharkOption` static field leak — `OpenApiConfigure` / `SqlSugarOptionsConfigure` changed from `static` to instance properties
- Fix `TenantResolutionMiddleware` silently failing for custom `ITenant` implementations — `ITenant.TenantId` changed to `{ get; set; }`
- Fix `CreateSharkEndpoint` AOT risk — use `new SharkEndpoint()` instead of `Activator.CreateInstance(nonPublic: true)`
- Fix `AsUnauthorized()` pointless null check — simplified to direct return
- Fix typo in `.csproj` description: `🌈owerful` → `🌈Powerful`

### refactor

- Rename `Utils/AssembliyUtil.cs` → `Utils/AssemblyUtil.cs`
- `SqlSugarOptions.InitKeyType` changed from public field to auto-property
- `AssemblyContext()` constructor changed from `public` to `internal`
- Replace `throw new Exception` with `throw new InvalidOperationException` in two places
- Remove dead code `UrlToDictionary()` from `StringExtension.cs`

### feat

- Make `IIdempotencyStore` fully async (`TryReserveAsync`, `GetAsync`, `StoreAsync`, `ReleaseAsync`) — enables distributed store plugins (Redis, PostgreSQL, etc.) without sync-over-async deadlock
- Add SHA-256 fingerprint helper
- Add `IIdempotencyStore` interface and record types
- Add `MemoryIdempotencyStore`
- Add `SharkIdempotencyMiddleware` state machine
- Route error responses through `UnifiedResult` factory
- Add `EnableIdempotency` flag and `ConfigureIdempotency`
- Register store and options in `AddCommon`
- Wire middleware into pipeline

### test

- Add null-path test and `<returns>` doc to fingerprint
- Add integration tests for state machine
- Add oversize response scenario
- Add scenario 10 (`EnableIdempotency=false`)
- Add `MapGet` to test endpoint and strengthen `GetWithHeader` test

### refactor

- Revert `UnifiedResult.Code`; embed code in `errorMessage`

### docs

- Add Scalar configuration docs (EN + ZH)
- Add `WrapSchemaFactory` usage docs to unified-result (EN + ZH)
- Add idempotency middleware design spec
- Add idempotency middleware implementation plan
- Add README section

### chore

- Enable idempotency middleware for end-to-end AOT verification
- Doc fixes and roadmap

## [0.5.0] — 2026-06-29

### feat

- Add `IAuthorizationInterceptor` — pluggable authorization hook (claim-based RBAC, tenant-scoped access, custom API-key logic) via endpoint filter
- Fix JWT events chaining — user `OnTokenValidated` / custom handlers now chain with Sharkable's default `OnChallenge` / `OnForbidden` instead of being overwritten
- Add pagination to AutoCrud `List` — `GET /{group}?page=1&pageSize=20` returns `{ items, total, page, pageSize, totalPages }`
- Add `CrudOperations.ListAll` — optionally expose full-table dump via dedicated `GET /all` route, intentionally excluded from `All` for safety
- Add AutoCrud search/filtering — `filter[field][op]=value` convention with 10 operators (eq/ne/gt/gte/lt/lte/like/in/nin/null), sort support, field-level validation
- Add AutoCrud AOT zero rd.xml — Source Generator preserves entity types at compile time, no manual configuration
- Add ProblemDetails (RFC 7807) support — `SharkOption.UseProblemDetails` flag, all error responses output standard format with `type`/`title`/`status`/`detail`/`instance`/`traceId`
- Add response compression — `SharkOption.EnableResponseCompression`, uses ASP.NET Core built-in middleware
- Add soft delete — `ISoftDeletable` marker, AutoCrud auto-filters `IsDeleted = 0` on reads + soft-deletes on `CrudOperations.Delete`
- Add `SharkBackgroundService` — enhanced `BackgroundService` with health reporting, retry policy, execution tracing
- Add distributed transactions (SAGA) — `ISagaStep` + `ISagaStore` + `SagaExecutor` + `MemorySagaStore`, crash-recovery, distributed lock, `RedisSagaStore` in Cache.Redis
- Add distributed cron job scheduler — 6-field cron expression parser, `ICronJobStore` with retry/timeout/concurrency control, admin endpoint `/_sharkable/jobs`, `RedisCronJobStore` in Cache.Redis

## [0.4.1] — 2026-06-28

### feat

- Add route conflict Roslyn Analyzer (`SHARK001`) — compile-time detection of duplicate `ISharkEndpoint` route registrations, ships with NuGet package automatically
- Add distributed tracing (`TracingMiddleware`) — W3C `traceparent` via `ActivitySource`, `ITracingExporter` interface for OpenTelemetry plugins, zero external dependencies
- Add built-in profiler (`ProfilerMiddleware` + `/_sharkable/profiler`) — request counts, average latency, top-N slowest recent requests, memory delta tracking
- Add extensible health checks — structured JSON `/healthz` via ASP.NET Core `HealthCheckService`, `HealthChecksConfigure` callback for custom checks, auto JWT authority reachability check, uptime + version in response
- Add multi-tenant data source isolation — `ITenantDataSource` scoped service, `TenantOptions.ConfigureDataSource()` for per-tenant connection string routing
- Add adaptive rate limiting — `SharkRateLimiterOptions.EnableAdaptive`, dynamic CPU/GC-based permit adjustment via `AdaptiveLimitMonitor`
- Add automatic ETag generation (304 Not Modified) — `SharkOption.EnableETag`, SHA256 content hashing, `ETagMiddleware`
- Add error message localization — `IErrorLocalizer` interface + `ErrorLocalizerFactory`, `Accept-Language`-aware translation for middleware errors
- Add AutoCrud abstractions — `IAutoCrudEntity<T>` + `CrudOperations` + `IAutoCrudGenerator` for pluggable CRUD route generation

## [0.4.0] — 2026-06-28

### feat

- Add startup configuration self-check — `ConfigurationValidator` validates JWT, multi-tenant, and API key settings at startup, throwing `SharkConfigurationException` with all errors
- Add graceful shutdown — `GracefulShutdownMiddleware` + `GracefulShutdownOptions`, health check returns 503 during drain, configurable `DrainTimeout`
- Add audit trail async/batch write — `AuditLogBuffer` (Channel-based, configurable `BatchSize`/`FlushInterval`/`AsyncWrite`), `AuditTrailMiddleware` supports async fire-and-forget logging
- Add `IDistributedRateLimitStore` — pluggable distributed counter store interface for rate limiting (Redis, PostgreSQL, etc.), with `MemoryRateLimitStore` as default
- Add `SharkRateLimiterMiddleware` — fixed-window rate limiting middleware backed by `IDistributedRateLimitStore`, configured via `SharkOption.ConfigureRateLimiting()`
- Add `SharkRateLimiterOptions` — per-endpoint or global rate limit configuration
- Add `IdempotencyStoreFactory` and `RateLimitStoreFactory` to `SharkOption` — factory delegates for plugging in custom stores inside the `AddShark()` callback

### refactor

- `IIdempotencyStore` registration inside `AddCommon()` now uses `TryAddSingleton` — NuGet plugin packages can register a custom store before `AddShark()` and it will take precedence
- Deprecate `[SharkEndpoint]` / `[SharkMethod]` / `SharkHttpMethod` and related reflection infrastructure (`IDependencyReflectorFactory`, `DependencyReflectorFactory`, `Reflector`, `ReflectorExtension`) — all marked `[Obsolete]` with guidance to migrate to `ISharkEndpoint`

### fix

- Fix 5 pre-existing nullability warnings (`CS8766`, `CS8601`, `CS8602`) in `SharkResponseMetadata.cs`, `AssemblyUtil.cs`, `SwaggerExtension.cs`

## [0.3.2] — 2026-06-27

### feat

- Add NuGet package icon (`sharkable.jpg`)
- Sync `Sharkable.DbType` enum with SqlSugar 5.1.4.215 — add `TDSQLForPGODBC`, `TDSQL`, `HANA`, `DB2`, `GaussDBNative`, `DuckDB`, `MongoDb`
- Update `Sharkable.AutoCrud.SqlSugar` to v0.3.2 with SqlSugarCore 5.1.4.215

## [0.3.1] — 2026-06-27

### feat

- Add exception handler logging — `SharkExceptionHandlerMiddleware` now logs unhandled exceptions with HTTP method, request path, and full exception details via `ILogger`

## [0.2.0] — 2026-06-08

- feat: add built-in middleware (E) and security (F) features
- feat: add `[SharkVersion]` attribute for API versioning
- feat: add `EnableAutoWrap` to `SharkOption`, double-wrap guard, AOT types, and tests
- feat: add test project + NativeTest demo improvements
- docs: comprehensive README.md update with all features
- docs: add linear history requirement for Rebase and merge
- fix: correct license expression, add README.md and packaging items

## [0.1.1] — 2026-06-08

- feat: add endpoint grouping, OpenAPI tags, and auto OperationId
- fix: use `Add()` convention for AOT-compatible tags + OperationId

## [0.1.0] — 2026-06-08

- feat: upgrade to .NET 10, replace Swagger with Scalar
- feat: add FluentValidation integration with auto-validate filter
- feat: add global exception handler + auto unified result wrap
- refactor: pluggable `UnifiedResult` with `IUnifiedResultFactory`
- fix: audit XML comments, fix bugs, remove dead code
- fix: invoke method as nullable
- docs: add XML doc comments to validation code + coding standard to AGENTS.md
- update AGENTS.md: add branch sync & rebase workflow

## [0.0.25] — 2024-10-03

- feat/fix: add more unified result extensions (`AsBadRequest`)
- fix: where results like BadRequest should only contain error message
- fix: where generic type of return delegate might be null

## [0.0.24] — 2024-10-02

- add `GlobalSuppressions` to suppress warnings
- will not proceed if `SqlSugarOptionsConfigure` is null

## [0.0.23] — 2024-09-27

- fix: where CRUD options not invoked properly
- fix: where auto CRUD options might be null
- fix: a bug where `SharkOption` might be null

## [0.0.22] — 2024-09-27

- add SqlSugar options and enums
- add Swagger UI options and fix options invoke method
- move files to suitable folders
- rename `DependencyInjection.cs` to `SharkableExtension.cs`
- add summary for `Shark.cs`

## [0.0.21] — 2024-09-25

- add SqlSugar options and enums
- make class sealed

## [0.0.20] — 2024-09-25

- update project home URL
- add CRUD example for AOT project

## [0.0.19] — 2024-09-24

- add auto CRUD for API generation using SqlSugar client
- fix: where SharkEndpoint wiring only maps one while using `TryAddSingleton`

## [0.0.17] — 2024-09-23

- add `AotMode` option
- add editor config to suppress warnings
- fix: where AOT mode detection not properly displayed
- move Swagger configuration to OpenAPI
- rename `CommonExtension` to `SharkExtension`

## [0.0.16] — 2024-09-21

- add Swagger options
- add `UseShark` options for Swagger and other upcoming settings
- use `TryAddSingleton` for `WireSharkEndpoint`

## [0.0.15] — 2024-09-19

- fix dependency for packing

## [0.0.14] — 2024-09-19

- add Swagger UI and OpenAPI doc
- add `IEndpointRouteBuilder` extension
- add `GetCaseFormat` static method
- set `RegexInlineRouteConstraint` in service
- code refactor `StringExtension`

## [0.0.13] — 2024-09-19

- fix: `FormatAsGroupName` version format may display disorderly
- fix: SharkEndpoint AOT compatibility

## [0.0.12] — 2024-09-18

- add version format for endpoint class
- setup service provider
- add `SharkOption` statics
- code refactor

## [0.0.10] — 2024-09-18

- add SnakeCase format support
- add `SharkEndpoint` create instance factory
- fix CamelCase format display

## [0.0.9] — 2024-09-17

- add format for CamelCase and ToLower

## [0.0.7] — 2024-09-16

- add assembly context and Shark essentials
- WIP `SharkEndpoint`

## [0.0.5] — 2024-09-12

- add `SharkMethod` string syntax
- WIP `ISharkEndpoint`

## [0.0.4] — 2024-09-11

- WIP endpoint mapper initial support
- Initial commit
