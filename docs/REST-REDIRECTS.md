# REST client redirect settings

Open **Request → Settings** in the REST client, then choose **REST client defaults**
or **Request redirect settings**. These settings do not change ordinary browsing.

**REST client defaults** apply to requests without an override. Saving **Request
redirect settings** creates a complete override for that request, not just for the
fields you changed. Later default changes do not affect it. **Use defaults** removes
the override and its trust exceptions. Save the request itself to persist this change.

Defaults:

- Follow redirects: on.
- Cross-origin credentials/custom headers: ask. Forwarding is unchecked in the
  approval dialog; following without checking it removes these headers.
- Cross-origin request bodies: ask before sending.
- HTTPS to HTTP: blocked.
- Maximum redirects: 10 (configurable from 0 to 50).

An origin includes scheme, hostname and port. Ordinary same-origin redirects
follow automatically. When a redirect needs approval, a dialog displays both
origins and the next method before any request reaches that destination. Choose
**Stop redirect**, or continue with the permissions you explicitly select.
For a header approval, the button reads **Continue without credentials** until you
select **Include credentials and custom headers**, then **Continue with credentials**.
If body approval is required, select **Send the request body** before continuing.
In a body-only approval, the button reads **Continue with body**.
URL paths/query strings and credential/body values are not displayed in this dialog.

A cross-origin GET without sensitive/custom headers can follow automatically and
show `Followed` in the Redirects tab. A 302 response to POST changes the request to
GET and drops its body, so it does not require body approval; use 307/308 to test
approval for a preserved body.

The credential policy also offers **Remove without asking**. Body policy offers
**Block** and **Allow**. Enabling automatic cross-origin bodies or HTTP downgrades
requires acknowledging the setting's risk. A downgrade override does not itself
authorize credential/body forwarding.

The approval dialog can remember checked permissions for that exact directed
origin pair on the current request. It does not grant permission to a third origin
later in the chain, nor to other requests. Save the request to persist the exception.
To revoke one exception, select its origin pair in **Request redirect settings**,
click **Remove selected exception**, then **Save** in the dialog. Save the request
itself to persist the removal. Subsequent redirects use its normal credential/body
policy again. This removes a remembered permission, not a browser extension.
Alternatively, select **Use defaults**
to remove the override and all its exceptions. Defaults are persisted locally in
`%LocalAppData%\DevBrowser\rest-redirects.json`; per-request settings are stored
with the encrypted request payload. Exports omit redirect overrides and imports
reset them, so a downloaded collection cannot grant forwarding permissions.

The response **Redirects** tab reports followed/stopped hops and removed header
names without their values. Turning off automatic redirects returns the 3xx response
without following it. Stopped redirects are responses, not successful requests to
the destination; inspect the reason in the Redirects tab.

## Implementation rules

- Both REST send paths use `SafeRedirectClient`; underlying automatic redirects
  and implicit cookie storage are disabled. Manually supplied cookies are supported.
- Unknown request headers are considered sensitive. Only Accept, Accept-Encoding,
  Accept-Language and User-Agent are automatically retained across origins.
  Content-Type and Content-Length are retained when an approved body is resent;
  custom content headers require credential permission. Host is recalculated.
- 301/302 convert POST to GET; 303 converts methods other than HEAD to GET;
  307/308 preserve method and body. Other methods on 301/302 are preserved.
  Bodies are dropped when the redirect changes the method to GET.
- Removed headers never reappear later in the chain. Trust is checked separately
  at every hop. A configured body Block can be overridden by an explicit
  request-specific body trust exception.
- Only HTTP(S) targets without URL-embedded username/password are accepted.
  Redirect count and a 100-second total operation timeout bound the chain.
- Standard headers can still contain sensitive user-provided values; this policy
  is not arbitrary secret detection. A server's Location URL may itself contain
  data the server chose to put there.

Windows security checks cover status/method handling, cross-host and cross-port
boundaries, custom headers, manual cookies, body decisions, exact trust pairs,
HTTP downgrade, loops, disabled redirects, cancellation, persisted overrides and
untrusted imports. Two real loopback HTTP servers verify the transport cannot
auto-follow or silently forward cookies behind the policy.

Run: `dotnet run --project tests/DeveloperBrowser.SecurityChecks/DeveloperBrowser.SecurityChecks.csproj --configuration Release`.
