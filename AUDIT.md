# Aerio Security & Reliability Audit Record

Living record of security/reliability findings and their verification status.
Format for new entries follows the issue-report template at the bottom.
Latest audit: 2026-11-01 (source plan: `Aerio_Testing_and_Correction_Plan.md`).

## Status summary

| ID | Finding | Status | Evidence |
| --- | --- | --- | --- |
| SEC-001 | DPAPI failure could persist API key as plaintext | **FIXED (fail closed)** | `EncryptApiKey` propagates; `EncryptApiKeyForDisk` throws → whole `SaveSettings` fails, previous file (incl. valid ciphertext) untouched; temp file cleaned. Tests: `SaveSettings_NeverWritesPlaintextApiKeyToDisk`, `SaveSettings_WhenDpapiFails_*` (whole-save failure, previous-ciphertext preservation, multi-provider leak). |
| SEC-002 | AI endpoints / credential forwarding | **FIXED + TESTED** | `AiEndpoints.IsSecure/EnsureSecure` (HTTPS, loopback HTTP only) enforced in `GetBaseUrl` (chat) and `FetchModelsAsync`; load-time normalization resets insecure stored endpoints; `CreateSecureHttpClient` disables auto-redirects so 3xx never carries `Authorization` off-origin. Tests: `AiEndpointsTests` (incl. `http://localhost.evil.example` lookalike), redirect-not-followed, insecure-model-list rejection before any request. |
| SEC-003 | Site permissions collapsed origins to hostnames | **FIXED (origin isolation)** | `SitePermissionPolicy.NormalizeOrigin` keys rules by scheme+host+port; `StateFor` exact-origin match (no bleed across schemes, ports, subdomains, lookalikes, or non-HTTP schemes); `PermissionRequested` uses `args.Uri` only (iframe isolation, deny+log on unresolvable origin); WebView2 profile sync preserves `PermissionOrigin`; `ApplySitePermissionProfileStateAsync` writes the exact origin (removed the https+http double-write); prompt and site-permission UI display the full origin. Legacy hostname-only rules migrate to their HTTPS origin only (conservative, idempotent; explicit origin rules win conflicts). Tests: `SitePermissionPolicyTests` (origin canonicalization, cross-origin bleed, non-HTTP schemes, IPv6, migration, merge precedence). |
| AI-004 | Tool authorization / prompt-injection defenses | **TESTED; file-tool gate in place** | Authorization is enforced at the executor dispatch boundary (canonical-name resolution → `RequiresWrite` from definitions → `AiAuthorizer` before any execution). Adversarial tests: mutating tools must declare `RequiresWrite`, aliases can't bypass the write guard, injection text in arguments grants nothing, unknown tools fail closed, cancellation prevents completion, active-tab switches keep user-tab guards. Note: no local file/document tools exist yet — per the correction plan, `create_document`/`export_docx`/`export_pdf` must NOT be enabled before per-action scoped approvals and path-safety gates are implemented. |
| RUNTIME-005 | Windows runtime verification | **PARTIAL (automated subset run)** | 2026-11-01 on .NET 10 / Win10 26300: Release build 0 warnings; 434/434 tests; app smoke (launch, responsive, graceful close, hard-kill relaunch, no log errors); concurrent multi-window session writes (unit-simulated); corrupt session/settings recovery (unit-tested). Remaining operator items: clean-VM install paths, 1/5/10/20-tab memory runs (`scripts/measure-browser.ps1`), permission prompts in real WebView2, high-DPI/multi-window manual pass — see `docs/benchmark-protocol.md` + `docs/release-checklist.md` §3–4. |

## Baseline (Phase 0, 2026-11-01)

- Branch: `fix/security-test-gates`, base SHA `62226708f80e9c24764057bf518a22b08ac91fd8`
- Tests at baseline: 396/396 green; vulnerable packages: none
- AI HTTP entry points mapped: chat (`GetBaseUrl`, native + OpenAI-compatible, streaming and buffered, retry loop), model listing (`FetchModelsAsync`), web search (`WebSearchService`, carries queries but no credentials)
- Permission storage: Aerio JSON settings (`SitePermissions`) + WebView2 profile sync (`GetNonDefaultPermissionSettingsAsync` import / `SetPermissionStateAsync` export)
- Flaky tests found and fixed during the audit: performance smoke tests now measure best-of-3 (machine-load tolerance)

## CI gates

- `ci.yml` / `release.yml` run: restore → **vulnerable-package check** → build → test
- Merge gate: all SEC/AI tests green, no new high-severity warnings, runtime gaps marked as NOT RUN when skipped

## Issue-report template

```markdown
## ISSUE-ID — Short title
- Severity:
- File and method:
- Environment / commit:
- Evidence:
- Reproduction steps:
- Expected behavior:
- Actual behavior:
- Proposed correction:
- Regression test:
- Test result: PASS / FAIL / NOT RUN
- Reviewer and date:
```
