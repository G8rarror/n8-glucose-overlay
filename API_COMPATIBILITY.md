# Gluroo API compatibility

Verified against [Gluroo's official GGC documentation](https://gluroo.com/support/gluroo-global-connect-ggc-nightscout-compatible-api/) on **October 15, 2026** (page updated September 24, 2026), before implementing retrieval.

Documented contract used by this application:

- Exact account origin: `https://<id>.ns.gluroo.com`.
- `GET /api/v1/entries.json?count=6` returns an array of CGM entries (about 30 minutes at Dexcom's 5-minute cadence, still one request per poll). `sgv` is integer mg/dL; `date` is epoch milliseconds. The extra entries are used only to compute the delta and short estimate and are not stored.
- Authentication: lowercase SHA-1 of the plain API Secret Token in the `api-secret` header. A copied API Secret Header can be used directly. Both credentials grant write access, although this app sends only GET requests.
- Prefer `direction`; numeric `trend` fallback uses Gluroo's 1–7 mapping. Missing or unrecognized trends show `?`.
- Minimum polling interval: two minutes. The shared daily budget is 5,000 requests per account, resetting at 00:00 UTC. HTTP 429 pauses until the next UTC day.
- GGC is experimental and has no live reading push support.

- `GET /api/v1/devicestatus.json?count=1` returns `[{"glurooIob":n,"glurooCob":n}]` (verified live 2026-11-20; no timestamp). Fetched at most every 10 minutes after a successful entries poll. A failure blanks IOB only.
- `GET /api/v1/treatments.json?count=5`, only on PCs without a local tconnectsync log, at the same 10-minute cadence, to estimate pump-data age.
- Entries polls are aligned to the CGM cycle (newest reading + 315 s; late reading retries at max(120 s, PollSeconds); no data or reading over 15 minutes old: 5 minutes; capped at 6 minutes). Typical per PC: about 300-400 entries + 144 devicestatus + up to 144 treatments, roughly 450-700 requests per UTC day; two PCs stay well under 5,000.

## Optional treatments check

`N8GlucoseOverlay.exe --treatments-check` is a one-off, manually started diagnostic. The overlay never runs it on its own.

- It sends one `GET /api/v1/treatments.json?count=100` to the documented treatments endpoint ("Insulin / carb / basal history"). It never posts, edits or deletes treatments.
- It will not run while the overlay is running. It also obeys the overlay's saved cooldown, authentication pause and 429 pause, and saves its request to that budget before sending.
- The response is summarised locally and the raw response is discarded. The report covers record types and counts, record ages, which fields are present, `enteredBy` values (email addresses hidden), and signs of automatic delivery. It never shows dose or carb amounts or notes. Responses over 1 MB are rejected.
- The report is saved as `treatments-check.txt` in the app's local data folder, or in the folder given with `--output`.
- Gluroo documents that GET /treatments omits sensor changes, site changes and several other event types.

## Verification boundary

Documentation compatibility is confirmed. Mocked HTTP tests exercise the implemented request, parser, authentication placement, errors, and response size limit. Native Windows checks exercise overlay behavior and encryption. **A live authenticated account request has not been tested because no account credentials were supplied.** On the user's first connection, a successful authenticated GET and valid CGM response verify account access; an empty array confirms endpoint access but awaits readings.

HTTPS certificate validation uses Windows defaults. Redirects are disabled. The application rejects token-bearing URLs and origins outside Gluroo's documented hostname pattern. It never posts data, probes undocumented routes, bypasses certificate validation, or sends a credential in a query string. Error text and response bodies are not logged.

The application persists request cooldowns per origin before sending. Authentication failures pause until the connection credential changes. Network failures back off from two to fifteen minutes. Manual refresh respects these limits. Restarting preserves cooldowns and 429 pauses. Changing display settings does not reset the request budget.
