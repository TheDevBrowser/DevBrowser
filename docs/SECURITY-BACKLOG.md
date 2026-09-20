# DevBrowser security update backlog

Prepared 2026-09-17 from a focused source review. No exploitation, penetration test, dependency vulnerability scan, or production configuration audit was performed. Priorities indicate implementation order, not CVSS severity. Implementation status is recorded per item.

Status updated 2026-09-20: **2 of the 4 first-priority updates are implemented**.

| Item | Status |
| --- | --- |
| SEC-01: Encrypt saved REST credentials | Implemented and covered by automated checks |
| SEC-02: Prevent credential leaks through REST redirects | Implemented and covered by automated checks |
| SEC-03: Bind storage edits to the inspected origin | Pending |
| SEC-04: Make exports safe to share by default | Pending |

Implemented means the agreed functionality is present and tested, not independently
security-audited. The redirect approval UI has also been exercised manually; that
does not establish complete manual coverage of every redirect scenario.

## First: credential exposure and origin boundaries

### SEC-01 — Protect saved REST credentials (P1, implemented; see remaining export work in SEC-04)
- Implementation: saved authentication, URLs, parameters, headers and bodies use immutable DPAPI secret-store payloads, with automatic startup migration and regression checks. See [storage and recovery details](PROTECTED-REQUEST-STORAGE.md). Default export sanitization beyond authentication remains SEC-04.
- Original finding: saved request authentication, headers and bodies were written directly into SQLite; only secret environment variables used `DpapiSecretStore`.
- Delivered: protected request payloads, preservation of existing variable references, startup migration with failure recovery, and documented backup/journal limitations.
- Verification: automated checks cover sentinel absence from tested active storage files, migration, failed saves, request round trips and authentication-field redaction in default exports. Broader export sanitization remains SEC-04. DPAPI does not protect against malicious software running as the same Windows user.

### SEC-02 — Prevent credential forwarding across REST redirects (P1, implemented)
- Implementation: both REST transports enforce per-hop origin checks, credential removal/approval, body approval, downgrade blocking and redirect limits. Defaults and encrypted per-request overrides are configurable; trust exceptions are exact directed origin pairs. Automated policy and real loopback-server checks cover these boundaries. See [redirect settings](REST-REDIRECTS.md).
- Original finding: both REST send paths used HttpClient without an explicit redirect policy for custom credential headers.
- Delivered: automatic same-origin redirects, cross-origin credential/body approval or configured removal/blocking, exact origin-pair exceptions, HTTPS downgrade blocking by default, configurable limits, and response redirect history. Ordinary browsing is unaffected.
- Verification: automated checks cover 301/302/303/307/308 method/body behavior, cross-host/port boundaries, header removal, body approval, trust scope, downgrade settings, loops, cancellation and persisted settings. Real local servers confirm the transport does not bypass the policy.
- Limits: users can explicitly authorize forwarding. This is not arbitrary secret detection in URL values or otherwise permitted standard headers. See the redirect guide for exact forwarding rules.
- Basis: .NET auto-redirects clear Authorization but retain other headers, including manually supplied Cookie headers. This is a source-and-documentation finding; no exploit was executed.

### SEC-03 — Bind storage operations to the inspected document (P1, pending; impact needs reproduction)
- Evidence: `BrowserStorageService.SetValueAsync`, `DeleteAsync` and `ClearAsync` act on the current document without checking the origin originally inspected. ReadAsync can combine a script result with cookies requested for the earlier origin. UI refresh versioning helps but is not an operation-level boundary.
- Update: carry tab/document identity and expected origin through reads and edits; invalidate edits on navigation; check origin inside the same script that mutates web storage; validate cookie operations against the inspected scope. Discard reads spanning a navigation.
- Acceptance: navigate or switch tabs while reading, editing or confirming deletion; an old operation must never disclose a value to or modify the new origin.

### SEC-04 — Make exports safe to share by default (P1, pending; confirmed redaction gaps)
- Evidence: `CollectionImportExportService` redacts selected headers and auth fields but retains URL, parameters and body; Cookie is absent from its sensitive header list. `HarCaptureService.SaveAsync` has no sanitization option.
- Update: centralize redaction for HAR and collections; cover cookies, authentication, URLs, query values and structured body secrets, including duplicated/raw HAR fields. Omit bodies by default in share-safe HAR exports. Offer a clearly labeled full-fidelity export and a preview; never promise arbitrary payloads are completely sanitized.
- Acceptance: fixtures containing sentinel secrets across supported fields do not leak them in safe exports; sensitive exports require explicit selection.

## Next: browser hardening and resilience

### SEC-05 — Establish an explicit WebView2 security policy (P2, hardening and verification)
- Evidence: MainWindow initializes WebView2 without an explicit security settings policy. No host-object registration, native web-message receiver, blanket certificate bypass or sandbox-disabling flags were found in the inspected source.
- Update: disable unused host objects and web messaging; preserve sandbox/TLS protections; keep the host non-elevated. Review top-level/frame navigation and external protocol handling without disabling ordinary browser JavaScript or legitimate web navigation.
- Acceptance: hostile pages/frames cannot invoke native operations; invalid certificates remain blocked; external application launches require an understandable user decision. Verify actual runtime behavior, not just settings in source.

### SEC-06 — Control permissions, popups and downloads (P2, policy gap)
- Evidence: MainWindow creates a tab for every NewWindowRequested event without checking IsUserInitiated. No explicit permission/download policy handlers were found.
- Update: block unsolicited popup storms; support user-requested popups and intentional exceptions. Verify WebView2 defaults before replacing permission/download UI. Provide origin-specific permission review/revocation and clear download destination/executable handling; never auto-run downloads.
- Acceptance: test camera, microphone, location, notifications, clipboard, popups and downloads from top-level pages and frames. Absence of a custom permission handler is not evidence of automatic permission grants.

### SEC-07 — Bound capture, response and import resource use (P2, confirmed unbounded paths)
- Evidence: NetworkCaptureService caps visible requests at 500 but evicted entries remain in `_inFlight`; subscriptions retain WebViews without a detach API. REST responses and HAR imports are read fully into memory. HAR live capture already has limits, which do not cover every input path.
- Update: remove evicted mappings, detach closed tabs, bound headers/body bytes and total capture memory, support cancellation and streaming response limits. Validate import size, entry count, nesting and schema before constructing UI models. Keep large-file handling deliberate and visible.
- Acceptance: sustained traffic, repeated tab closure, oversized/deep imports and oversized responses do not cause unbounded host memory growth or UI lockup; show truncation clearly.

### SEC-08 — Apply data minimization to local logs and diagnostics (P2, confirmed raw local exception logging; remote behavior needs verification)
- Evidence: App logs complete exceptions locally. CrashReportingService is opt-in, disables breadcrumbs/tracing/default PII and manually sanitizes reports; the full SDK event envelope has not been inspected.
- Update: use an allowlisted diagnostic format locally and remotely, verify automatic SDK integrations and event payloads, redact exception details before persistence, and provide log deletion/retention controls.
- Acceptance: captured diagnostic envelopes and local logs exclude seeded URLs, credentials, cookies, bodies and personal paths; no diagnostic event is sent before consent or after disabling it.

### SEC-09 — Make engine and framework patching visible (P2, maintenance)
- Evidence: project/CI target .NET 9; release documentation requires Evergreen WebView2. App update checks exist, but no runtime-new-version handling was found.
- Update: show the actual running WebView2 version and offer a restart when a newer runtime is available; test update failure/offline behavior. Plan .NET 10 LTS migration before .NET 9 support ends on November 10, 2026. Keep SDK dependency updates separate from browser runtime updates.
- Acceptance: an installed engine update is adopted after restart; users can identify the running version; both packaged and unpackaged releases receive documented patch coverage.

## Release assurance and ongoing work

### SEC-10 — Harden signing and release automation (P2, confirmed workflow hardening opportunities)
- Evidence: release workflow imports a long-lived self-signed private key before build, exposes signing secrets at job scope, uses action version tags and grants contents:write at workflow scope. Local installer script creates a machine-trusted development root CA. Public release instructions instead use Trusted People; these are distinct mechanisms.
- Update: isolate signing from build, limit secrets/token permissions, pin actions to reviewed commit hashes, verify signatures after signing, protect release tags/environments and document key rotation/revocation. Plan trusted production signing with an installer identity migration strategy. Replace broad local root trust where feasible and provide safe cleanup instructions for development certificates.
- Acceptance: build steps cannot access production signing material; tampered/wrong-publisher packages fail; clean-machine install/update and signer rollover are tested. Check GitHub account/repository protections separately; local files cannot establish those settings.

### SEC-11 — Make security regressions fail CI (P2, partially implemented)
- Progress: the build workflow now runs the REST security checks for protected storage and redirects. Dedicated security scanning, dependency-update automation and comprehensive release gating remain open.
- Update: run existing tests and the regression cases above; add dependency/secret/static analysis with explicit triage gates, dependency inventory and scheduled review. Review restored transitive dependencies, not only direct references.
- Acceptance: seeded failing security tests and a known-vulnerable test dependency block release; exceptions require a recorded rationale and expiry. A clean scan is not a security certification.

### SEC-12 — Publish a security policy and arrange independent review (P2/P3, assurance)
- Evidence: no SECURITY.md or threat-model document was found in the repository inventory.
- Update: document web/native/storage/update trust boundaries, supported versions, private vulnerability reporting and realistic response targets. Obtain an independent review focused first on origin races, REST credential forwarding, exports and release signing. Publish scope and remediation status accurately.
- Acceptance: reports reach a monitored private channel; each finding has an owner, regression test and remediation record; public claims distinguish source review, automated scans and independent testing.

## Existing protections to retain

- DPAPI protection for secret environment variables.
- Opt-in crash reporting with reduced diagnostic payloads.
- JSON viewer uses textContent for values, not HTML interpolation.
- Storage script values are JSON-serialized.
- Signed MSIX distribution and periodic application update checks.
- Existing live HAR capture limits.

These are positive controls, not proof that the full product is secure.

## Primary references checked

- [Microsoft WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
- [WebView2 update and development guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/developer-guide)
- [HttpClient redirect behavior](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-9.0)
- [.NET 8/9 support end date](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
