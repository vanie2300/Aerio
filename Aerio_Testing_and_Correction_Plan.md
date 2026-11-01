# Aerio Browser — Testing & Correction Plan

**Target:** https://github.com/vanie2300/Aerio  
**Target codebase:** Original C# / WPF / WebView2 application, **not** the separate Rust Glass prototype.  
**Audit date:** 2026-11-01  
**Scope:** Credential protection, AI endpoints, site permissions, AI authorization, Windows reliability, and readiness for AI-generated Word/PDF documents.  
**Status:** Correction instructions and proposed regression tests. **No repository edits or Windows builds have been performed as part of this document.**

## Read first: verified status and audit caveat

| ID | Current code-level assessment | Evidence |
| --- | --- | --- |
| SEC-001 | **Reopened: plaintext API-key fallback is still visible** in `EncryptApiKey()` on the public `main` file. An earlier audit reported this fixed; the current inspected implementation still returns plaintext on `CryptographicException`. | [`BrowserDataStore.cs`](https://github.com/vanie2300/Aerio/blob/main/Processes/Services/BrowserDataStore.cs) |
| SEC-002 | **Fix present; integration testing required.** `AiEndpoints.EnsureSecure()` accepts HTTPS and loopback HTTP, rejecting remote HTTP. Confirm every outbound AI path calls it, and reject redirect downgrades. | [`AiEndpoints.cs`](https://github.com/vanie2300/Aerio/blob/main/Processes/AI/AiEndpoints.cs), [`AiService.cs`](https://github.com/vanie2300/Aerio/blob/main/Processes/AI/AiService.cs) |
| SEC-003 | **Confirmed origin-isolation gap.** `SitePermissionPolicy.NormalizeHost()` removes scheme and port; the WPF permission UI has its own hostname-only `NormalizePermissionHost()`. | [`SitePermissionPolicy.cs`](https://github.com/vanie2300/Aerio/blob/main/Processes/Services/SitePermissionPolicy.cs), [`MainWindow.Permissions.cs`](https://github.com/vanie2300/Aerio/blob/main/UI/MainWindow/MainWindow.Permissions.cs) |
| AI-004 | **Testing gap.** `AiAuthorizer` checks ownership and write permissions; the entire tool dispatch chain and future file operations require adversarial tests. | [`AiAuthorizer.cs`](https://github.com/vanie2300/Aerio/blob/main/Processes/AI/AiAuthorizer.cs) |
| RUNTIME-005 | **Testing gap.** A successful CI build cannot certify WebView2 behavior, prompts, recovery, or memory usage. | [GitHub Actions](https://github.com/vanie2300/Aerio/actions) |

> **Safety rule:** Work in a new branch and with disposable browser profiles and dummy API keys. Do not use real credentials or personal documents for failure-injection tests. Keep production users on the stable version until critical issues are resolved and verified.

---

## Phase 0 — Baseline and reproducibility

### Testing
- [ ] Create branch: `git switch -c fix/security-test-gates`.
- [ ] Record commit SHA: `git rev-parse HEAD`.
- [ ] Run existing tests and save the output.
- [ ] Check current dependency vulnerability reports.
- [ ] Identify which permissions are stored in Aerio's JSON settings and which are stored in WebView2's profile.
- [ ] Identify all AI HTTP request entry points, especially streaming, model listing, retries, and custom providers.
- [ ] Record any flaky tests; never silently ignore failures.

### Commands (PowerShell or CMD, in repository root)

```cmd
dotnet restore
dotnet test TabloidBrowser.Tests\TabloidBrowser.Tests.csproj -c Release --no-restore
dotnet build TabloidBrowser.csproj -c Release --no-restore
dotnet package list TabloidBrowser.csproj --vulnerable --include-transitive
```

> For users who do not want to build the GUI locally, run these in the Windows GitHub Actions workflow instead. `dotnet test`/`dotnet build` do **not** require launching Aerio's browser window.

**Exit condition:** Reproducible baseline with build/test logs and commit SHA.

---

## Phase 1 — SEC-001: Fail-closed API key encryption

**Affected source:** `Processes/Services/BrowserDataStore.cs`, particularly `EncryptApiKey`, `EncryptApiKeyForDisk`, `SaveSettings`, and `DecryptApiKey`.

### Confirmed problem

The currently inspected `EncryptApiKey()` catches `CryptographicException`, logs a fallback, and returns the original unencrypted key. Because `SaveSettings()` then serializes that result, a DPAPI failure can persist an API key in cleartext.

### Required correction

1. **Never** return plaintext when encryption fails. Fail the operation.
2. Preserve the previous valid ciphertext or the previous settings file if saving fails.
3. Keep a user's newly entered key in memory only if strictly needed; never write it as plaintext.
4. Remove plaintext or full-key values from exceptions and logs.
5. Treat legacy plaintext credentials as an explicit migration task: encrypt during a successful save or ask for re-entry; do not allow an unprotected value to remain silently in storage.
6. Make DPAPI behavior testable through an injected `ISecretProtector` or equivalent facade, so a test can deterministically simulate failure.

### Minimal code correction (replace the **catch behavior** in `EncryptApiKey`)

```csharp
catch (CryptographicException ex)
{
    Log.Error(ex, "DPAPI protection failed; refusing to persist API key");
    throw new InvalidOperationException(
        "Your API key could not be encrypted. Settings were not saved.", ex);
}
```

The existing `SaveSettings()` catch-and-return-false path can then prevent new settings data from being committed. **Verify its temporary-file cleanup and preserved-ciphertext behavior** after this change. This snippet is a targeted correction proposal, not a compiled patch.

### Required automated tests

| Test | Expected result |
| --- | --- |
| Normal DPAPI encryption + save + reload | Key survives encrypted; raw settings do not contain the known dummy key |
| Inject `CryptographicException` from protector | Save fails; plaintext never appears on disk |
| Failure when old ciphertext already exists | Existing encrypted file remains intact |
| Decrypt failure after Windows profile change | Ciphertext preserved; key unavailable until user restores or re-enters it |
| Legacy plaintext key imported | Secure migration or explicit re-entry; no silent unencrypted rewrite |
| Multi-provider keys | No provider key leaks through the fallback |
| Logging on failure | No complete API key in log contents |

**Exit condition:** No code path persists plaintext API keys and the failure-injection tests pass.

---

## Phase 2 — SEC-002: AI endpoint TLS enforcement

**Affected source:** `Processes/AI/AiEndpoints.cs`, `Processes/AI/AiService.cs`, provider-specific clients.

### Current source finding

`AiEndpoints.IsSecure()` already permits `https://` endpoints and HTTP loopback endpoints, while blocking remote `http://`. Keep this behavior, then prove the policy is applied at every network boundary.

### Corrections and verification

- [ ] Before **every** outbound AI or model-list request, call the same endpoint security validator.
- [ ] Validate final URI after any endpoint/path composition; do not assume the initial base URL is the actual destination.
- [ ] Prevent cross-origin credential forwarding and any HTTPS-to-HTTP downgrade through redirects. Prefer manual redirect handling or disable redirects on authenticated client requests.
- [ ] Ensure `Authorization` headers are not copied onto redirects to another origin.
- [ ] Accept loopback-only HTTP intentionally (`localhost`, `127.0.0.1`, `[::1]`); reject LAN addresses and lookalike hostnames.
- [ ] Keep endpoint errors actionable without exposing tokens or request bodies.

### Extend existing `AiEndpointsTests.cs`

| URL | Expected |
| --- | --- |
| `https://api.example.com/v1` | Allowed |
| `http://api.example.com/v1` | Rejected |
| `http://192.168.1.8:11434` | Rejected |
| `http://localhost:11434` | Allowed |
| `http://127.0.0.1:11434` | Allowed |
| `http://[::1]:11434` | Allowed |
| `http://localhost.evil.example` | Rejected |
| `ftp://api.example.com` | Rejected |
| HTTPS provider redirecting to HTTP | Rejected before credential transmission |
| HTTPS provider redirecting to another host | No credentials forwarded without explicit policy |

**Exit condition:** Every AI request path enforces endpoint policy and redirect tests pass.

---

## Phase 3 — SEC-003: Correct site permissions to use full origins

**Affected source:**

- `Processes/Services/SitePermissionPolicy.cs`
- `UI/MainWindow/MainWindow.Permissions.cs`
- Site-permission persistence in `BrowserDataStore.cs`
- `TabloidBrowser.Tests/SitePermissionPolicyTests.cs`
- Any profile synchronization using `GetNonDefaultPermissionSettingsAsync` or `SetPermissionStateAsync`

### Confirmed problem

Current rules normalize to hostname alone. `https://example.com`, `http://example.com`, and `https://example.com:8443` therefore share a stored permission entry. These are **different origins** and should not implicitly share camera, microphone, location, or file-related permissions.

### Required correction

1. Store and compare **scheme + canonical host + effective port**; do not include path, query, or fragment.
2. Replace hostname-only lookup in the policy **and** `MainWindow.Permissions.cs` (it currently contains duplicate normalization and matching logic).
3. Use `args.Uri` as the permission requester origin, **not** `tab.Address` as a fallback to authorize an unknown origin. If the supplied origin is missing or invalid, deny and log safely.
4. Use WebView2's `PermissionOrigin` when synchronizing saved permissions. Preserve the scheme and port.
5. Continue to set `args.SavesInProfile = false` when Aerio owns persistence, and test that remembered choices do not bypass the Aerio policy.
6. For existing hostname-only **Allow** grants, do not auto-expand them to both HTTP and HTTPS. Back up and retire them or require the user to reapprove each exact origin. Document the migration and make it idempotent.
7. Display the origin in the permission prompt and site-permission UI (e.g., `https://example.com:8443`).
8. Check iframe permission events and ensure a requester cannot inherit a top-level page's permission merely because the tab shows that top-level page.

### Proposed canonicalizer (add to `SitePermissionPolicy`)

```csharp
public static string? NormalizeOrigin(string? input)
{
    if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri))
        return null;

    if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        return null;

    if (!string.IsNullOrEmpty(uri.UserInfo) ||
        uri.HostNameType == UriHostNameType.Unknown)
        return null;

    // Uri canonicalizes normal HTTP(S) default ports and drops path/query.
    return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
}
```

**Integration warning:** Merely adding `NormalizeOrigin` is insufficient. Change the rule keys and all lookups, UI editing, synchronization, and data migration. The existing `SitePermissionPolicyTests.cs` explicitly expects scheme and port stripping and will need to be revised.

### Illustrative xUnit regression tests (add after updating `StateFor` to use origin keys)

```csharp
[Theory]
[InlineData("https://Example.com:443/page", "https://example.com")]
[InlineData("http://example.com:80/a", "http://example.com")]
[InlineData("https://example.com:8443/a", "https://example.com:8443")]
public void NormalizeOrigin_UsesSchemeHostAndPort(string value, string expected)
{
    Assert.Equal(expected, SitePermissionPolicy.NormalizeOrigin(value));
}

[Fact]
public void CameraGrant_DoesNotBleedAcrossOrigins()
{
    var rules = new[]
    {
        new SitePermissionRule("https://example.com", "Camera", "Allow")
    };

    Assert.Equal("Allow", SitePermissionPolicy.StateFor(
        rules, "https://example.com/page", "Camera"));
    Assert.Equal("Default", SitePermissionPolicy.StateFor(
        rules, "http://example.com", "Camera"));
    Assert.Equal("Default", SitePermissionPolicy.StateFor(
        rules, "https://example.com:8443", "Camera"));
    Assert.Equal("Default", SitePermissionPolicy.StateFor(
        rules, "https://sub.example.com", "Camera"));
}
```

### Additional permission tests

- [ ] Same origin, different paths: the permission is shared.
- [ ] Different schemes, ports, subdomains, punycode/Unicode host forms, and IPv6 bracketed addresses: no accidental cross-origin grants.
- [ ] Invalid origin, `file:`, `data:`, `javascript:`, `about:`: never inherit an unrelated HTTP(S) grant.
- [ ] Existing legacy Allow rules: user must reapprove exact origin.
- [ ] Existing Deny rules: migration follows documented conservative policy.
- [ ] Multiple permission kinds: camera Allow does not imply microphone Allow.
- [ ] Private profile: permissions do not leak into normal profile or persist unexpectedly.
- [ ] Allow, Block, Ask transitions and clearing of rules survive restart.
- [ ] Iframe permission requests are attributed to the **requesting origin**.
- [ ] WebView2 internal profile settings do not silently override Aerio's decisions.

**Exit condition:** Origin isolation is unit-tested, migrated safely, and verified in real WebView2 permission prompts.

---

## Phase 4 — AI-004: Agent and tool authorization, especially upcoming Word/PDF writing

**Affected source:** `Processes/AI/AiAuthorizer.cs`, `AiToolDefinitions.cs`, `AiTools.cs`, and `UI/MainWindow/MainWindow.AI.cs`.

### Testing

- [ ] A model instruction embedded inside an arbitrary webpage cannot grant permission to call privileged tools.
- [ ] Read-only tools cannot secretly mutate a tab, settings, or filesystem.
- [ ] An AI-owned tab is clearly distinguished from a user-owned tab; IDs cannot be spoofed by webpage content.
- [ ] A write action against a user-owned tab is denied unless the exact action is explicitly authorized.
- [ ] Switching the active tab mid-operation does not bypass authorization.
- [ ] Tool aliases and command arguments cannot bypass `RequiresWrite` or the target-tab check.
- [ ] Cancellation prevents queued, not-yet-authorized writes from continuing.
- [ ] AI research content is treated as untrusted data, not instructions.
- [ ] Prompt injection against a mock page results in a refusal to access private data.

### Corrections

1. Centralize tool authorization at the **execution boundary**, not only when the AI proposes a tool call.
2. Authorize using immutable target resource IDs and explicit action types, not just user-visible names.
3. Record user approvals per action, target, and scope, with expiration; do not apply blanket permission to unrelated files or tabs.
4. For future `create_document`, `edit_document`, `export_docx`, `export_pdf`, require user-selected document/file scope; preview diffs and request confirmation before overwrite.
5. Constrain filesystem writes to the user-selected file or directory; reject path traversal, symlink escapes, and malicious filenames.
6. Separate webpage content extraction from the agent's trusted instructions; never execute webpage text as a tool command.
7. Add cancellation, size limits, and safe temporary-file handling.

**Exit condition:** Adversarial authorization tests pass; **do not enable AI local file-writing tools before this gate**.

---

## Phase 5 — RUNTIME-005: Windows WebView2, UI, and recovery

### Native tests (run later when a Windows machine/build is available)

- [ ] Clean install and first start on Windows 10 and 11.
- [ ] Open/close 1, 5, 10, 20 tabs; measure WebView2 process and app memory.
- [ ] Navigate while tabs are hibernating or being closed.
- [ ] Force a tab WebView failure and verify recovery without orphaned processes.
- [ ] Verify startup after power interruption/corrupt settings (disposable profile).
- [ ] Test navigation, downloads, history, bookmarks, private mode and updates.
- [ ] Test permission Allow/Block/Ask per origin and per profile.
- [ ] Test text selection and keyboard shortcuts with webpage focus.
- [ ] Check dark/light UI, high-DPI monitors, narrow windows and modal focus.
- [ ] Run multiple windows against the same data directory and check concurrent writes.

**Exit condition:** All release-critical tests pass with logs and reproduction evidence.

---

## Phase 6 — Final verification and merge rules

### Required CI steps

```cmd
dotnet restore
dotnet test TabloidBrowser.Tests\TabloidBrowser.Tests.csproj -c Release --no-restore
dotnet build TabloidBrowser.csproj -c Release --no-restore
dotnet package list TabloidBrowser.csproj --vulnerable --include-transitive
```

### Merge gate

- [ ] SEC-001 fail-closed tests pass; no plaintext credential fallback remains.
- [ ] SEC-002 HTTPS/loopback policy passes for **all** AI provider paths.
- [ ] SEC-003 full-origin permission tests pass and migration is safe.
- [ ] AI-004 browser and future file-tool authorization checks pass.
- [ ] No new high-severity warnings or known critical regressions.
- [ ] Windows runtime test gaps are explicitly marked when not yet executed.
- [ ] Test outputs, changed files, and remaining risks are recorded in `AUDIT.md`.

### Suggested issue-report format

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

## Recommended correction order

1. **Immediately:** SEC-001 plaintext credential failure path.
2. **Next:** SEC-003 full-origin permission isolation and safe migration.
3. **Then:** SEC-002 endpoint integration and redirect regression tests.
4. **Then:** AI-004 authorization before DOCX/PDF file-writing tools.
5. **Finally:** RUNTIME-005 real Windows/WebView2 verification.

## Primary sources

- Aerio codebase: https://github.com/vanie2300/Aerio (accessed Nov 1, 2026).
- Aerio `BrowserDataStore.cs`: https://github.com/vanie2300/Aerio/blob/main/Processes/Services/BrowserDataStore.cs (accessed Nov 1, 2026).
- Aerio `AiEndpoints.cs`: https://github.com/vanie2300/Aerio/blob/main/Processes/AI/AiEndpoints.cs (accessed Nov 1, 2026).
- Aerio `SitePermissionPolicy.cs`: https://github.com/vanie2300/Aerio/blob/main/Processes/Services/SitePermissionPolicy.cs (accessed Nov 1, 2026).
- Aerio `MainWindow.Permissions.cs`: https://github.com/vanie2300/Aerio/blob/main/UI/MainWindow/MainWindow.Permissions.cs (accessed Nov 1, 2026).
- Microsoft WebView2 `PermissionRequestedEventArgs`: https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2permissionrequestedeventargs (official documentation, accessed Nov 1, 2026).
- Microsoft WebView2 `SetPermissionStateAsync`: https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2profile.setpermissionstateasync (official documentation, accessed Nov 1, 2026).

**Scope note:** These are implementation instructions and proposed tests based on the inspected public source, not an assertion that I have compiled the changes or repaired your GitHub repository.
