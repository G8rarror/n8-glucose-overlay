# Gluroo API compatibility

Verified against [Gluroo's official GGC documentation](https://gluroo.com/support/gluroo-global-connect-ggc-nightscout-compatible-api/) on **October 15, 2026** (page updated September 24, 2026), before implementing retrieval.

Documented contract used by this application:

- Exact account origin: `https://<id>.ns.gluroo.com`.
- `GET /api/v1/entries.json?count=1` returns an array of CGM entries. `sgv` is integer mg/dL; `date` is epoch milliseconds.
- Authentication: lowercase SHA-1 of the plain API Secret Token in the `api-secret` header. A copied API Secret Header can be used directly. Both credentials grant write access, although this app sends only GET requests.
- Prefer `direction`; numeric `trend` fallback uses Gluroo's 1–7 mapping. Missing or unrecognized trends show `?`.
- Minimum polling interval: two minutes. The shared daily budget is 5,000 requests per account, resetting at 00:00 UTC. HTTP 429 pauses until the next UTC day.
- GGC is experimental and has no live reading push support.

## Verification boundary

Documentation compatibility is confirmed. Mocked HTTP tests exercise the implemented request, parser, authentication placement, errors, and response size limit. Native Windows checks exercise overlay behavior and encryption. **A live authenticated account request has not been tested because no account credentials were supplied.** On the user's first connection, a successful authenticated GET and valid CGM response verify account access; an empty array confirms endpoint access but awaits readings.

HTTPS certificate validation uses Windows defaults. Redirects are disabled. The application rejects token-bearing URLs and origins outside Gluroo's documented hostname pattern. It never posts data, probes undocumented routes, bypasses certificate validation, or sends a credential in a query string. Error text and response bodies are not logged.

The application persists request cooldowns per origin before sending. Authentication failures pause until the connection credential changes. Network failures back off from two to fifteen minutes. Manual refresh respects these limits. Restarting preserves cooldowns and 429 pauses. Changing display settings does not reset the request budget.
