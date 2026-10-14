# QA-001 synthetic dataset

A deterministic, **synthetic-only** 90-day dataset for a dedicated second LifeOS test account, plus an
importer that writes it **only through the existing authenticated API**. Task record:
[QA-001](../../docs/tasks/qa/QA-001.md).

- Default mode is an **offline dry-run**: no network, no credentials, no writes outside `artifacts/`.
- A real import needs `--apply` **and** a separately approved, expiring approval file bound to the exact
  manifest, configuration, target origin and user, **and** an access token in `LIFEOS_QA_TOKEN`.
- **Gym is never imported**: ADR-008 assigns workout instants from the server clock and no public API
  records historical sessions. The 24 workouts are offline fixtures with
  `importStatus: not_importable_via_public_api`. See the proposed [GYM-HIST-001](../../docs/tasks/gym/GYM-HIST-001.md).

Technology: Python standard library only (no packages), run through the `uv`/Python 3.14 toolchain the
repository already uses for `tools/ai-evals`. Nothing here is imported by the application.

## Quickstart (offline)

From the repository root:

```powershell
uv run --no-project python tools/qa-dataset/qa001.py                       # dry-run, default seed 40 / reference 2026-08-30
uv run --no-project python tools/qa-dataset/qa001.py --seed 7 --reference-date 2026-05-01
uv run --no-project python tools/qa-dataset/qa001.py --manifest tools/qa-dataset/fixtures/manifest-v1.json
cd tools/qa-dataset; uv run --no-project python -m unittest; cd ../..
```

The dry-run writes `artifacts/qa001/manifest.json` (git-ignored) and prints a preview: counts per module,
Finance record kinds, monthly budget vs. expenses, analyzed/unanalyzed meals, Journal questions and the Gym
status. The same seed and reference date always produce the same manifest and `sha256`.
`fixtures/manifest-v1.json` is the reference output for the defaults; a test regenerates and compares it.

## What the dataset contains

The dataset covers the 90 days **before** the reference date (defaults: 2026-06-01 to 2026-08-29), with a
fixed synthetic device offset of +02:00. Every record carries a unique key
`qa001-<identity>-<module>-<n>`. Every created resource that has free text embeds `[key]` in it
(transaction/rule/planned-expense `note`, meal `description`, Journal `content`). That marker is how reruns
recognise records that already exist.

| Module | Records (defaults) | Content | Import |
| --- | --- | --- | --- |
| Finance | 151 | 3 monthly budgets, 5 recurring rules, 13 occurrence actions (12 confirmed, 1 skipped, 1 occurrence left **Due**), 6 planned expenses (3 confirmed, 1 cancelled, 2 left **Due**), 120 transactions | API |
| Nutrition | 90 | one meal per day; ~2/3 with nutrition values (set as `UserAdjusted`), the rest deliberately unanalyzed (all trip meals, many takeaway dinners) | API |
| Journal | 52 | entries on 12 topics, 12 known-answer questions (fact, temporal, cross-module, 2 abstentions) with evidence keys and evidence snippets | API |
| Gym | 24 | three program phases (Adattamento, Forza, Ripresa), no sessions during the break | **offline only** |

Cross-module narrative (all fictional), listed in `manifest.scenarios`:

- **Deadline week**: the "Aurora" project deadline brings takeaway dinners, more dining spend and fewer analyzed meals.
- **Car service**: a planned service of 250 € is confirmed at 310 € (brake pads), with a Journal note.
- **Trip**: a 7-day trip to Trieste. August is the only month over budget, and trip meals are unanalyzed.
- **Training break**: two weeks without training (the trip, then a cold). The August gym fee is skipped.
- **Knee discomfort**: the squat load drops in the following workouts.
- **Sunday meal prep**: dedicated groceries and meal-prep lunches.

`manifest.expected` holds machine-readable assertions: Finance record kinds, monthly
`budget`/`expenses`/`income`/`overBudget`, the expected status of every recurring occurrence and planned
expense, analyzed/unanalyzed meal counts, and the Journal and Gym totals. Validation recomputes them from the
records.

### Consistency checks (`dataset.validate`)

The checks run on generation, on `--manifest` and before any import:

- checksum and schema version
- counts within the QA-001 targets
- unique keys
- every date inside the window, with chronological order per module
- amount limits and two decimals
- `occurredAtUtc` in explicit UTC, on the record's local day
- markers present and text within the API length limits
- no `userId`/owner field anywhere: the API derives the owner from the token
- category type compatibility
- each action references an earlier parent and stays inside the rule's month range and schedule
- one budget per month
- whole-minute meal times and complete nutrition values within limits
- every Journal answer verifiable from its evidence snippets
- every scenario's evidence keys resolve
- the Gym status is set and the phases are chronological
- narrative invariants: no workout during the break, only the trip month over budget, squat load lower
  after the knee entry, no nutrition values for trip meals

## Import design

The import uses only these existing endpoints, all authenticated, in dependency order:

1. `PUT /api/budgets/{y}/{m}/{currency}`
2. `POST /api/recurring`
3. `POST /api/recurring/{id}/{y}/{m}/confirm|skip`
4. `POST /api/planned-expenses`
5. `POST /api/planned-expenses/{id}/confirm|cancel`
6. `POST /api/transactions`
7. `POST /api/nutrition/meals`, then `PUT /api/nutrition/meals/{id}/nutrition`
8. `POST /api/journal`

Reads used for reconciliation: `GET /api/me`, `/api/accounts`, `/api/categories`, `/api/budgets/...`,
`/api/recurring`, `/api/planned-expenses`, `/api/transactions`, `/api/journal` (paged) and
`/api/nutrition/meals?date=`.

Not imported:

- Gym (see above).
- Opening balances, accounts and categories: the operator maps existing ones in the config.
- Nutrition targets.
- AI estimation: no `estimate`, `analyze` or `lazy-close` calls.
- Journal memory sync and embeddings: no live AI calls.

Safety properties:

- **Approval gate before any network call.** The approval file must match the manifest `sha256`, the
  config `sha256`, the origin, the user and whether the target is remote. It also needs
  `approved: true`, `dedicatedTestAccount: true` and a future `expiresAtUtc` with an explicit offset.
- **Target guard.** Without `--allow-remote` only loopback origins are accepted. Remote origins must be HTTPS.
  URLs may not carry credentials, paths or queries. The client uses no proxies and follows no redirects,
  so the token never leaves the approved origin.
- **Account guard.** `/api/me` must return the configured `userId`, onboarding `Completed` and default
  currency EUR. The account must be owned and in EUR. Each category must be owned and of the right type.
  The dataset must end before today.
- **Reconcile everything before the first write.** Any conflict aborts with zero writes:
  - a duplicate marker
  - a marked record whose fields differ
  - an existing budget with another amount
  - an occurrence or planned expense already processed differently

  Nothing is ever updated or overwritten.
- **Durable resume state** (`--state`, default `artifacts/qa001/import-state.json`):
  - The key is written as `uncertain` before each write and as `done` with the remote id after it.
  - A lock file prevents concurrent runs.
  - The state is bound to the manifest, config, origin and user.
  - Losing the state is safe: markers and remote statuses rebuild it.
- **Uncertain writes.**
  - *Idempotent steps* are retried on the next run: budget PUT, occurrence confirm/skip, planned
    confirm/cancel and nutrition PUT. The server treats each logical month or item as processed once.
  - *Creates* (`POST` of a new transaction, rule, planned expense, meal or Journal entry) are **never
    re-posted automatically**. If an uncertain create appears remotely, the next run adopts it.
    Otherwise the run stops (see below).
  - A 400/401/403/404/409/422/429 response proves the create was not applied, so that key stays pending.
- **Report** (`--report`, default `artifacts/qa001/import-report.json`): steps, already present, created
  and pending per module, warnings, the Gym status, and on failure the key, kind, HTTP status and guidance.
  It is also written on a partial import. Server response bodies, the token and the email address are
  never printed or stored.

### Error paths

| Situation | Behaviour | What to do |
| --- | --- | --- |
| 401 | stops; key not marked as attempted | access tokens last 15 minutes: get a new token and rerun |
| 403 | stops | check the token belongs to the test account |
| 404 | stops | a mapped account or category id is wrong for this user |
| 400 / 422 | stops | fix the config (category types, currency) or report a contract change |
| 409 | stops; nothing overwritten | inspect that record in the app |
| 429 | stops | wait, then rerun; already imported records are skipped |
| network failure / timeout | stops; the key stays `uncertain` | rerun: idempotent steps retry; creates are adopted if present, otherwise see below |
| reconciliation conflict | stops before any write | inspect the named key; never edit the manifest to force it |
| lock exists | stops | make sure no importer runs, then delete `import-state.json.lock` |

**Uncertain records.** The run may stop with "Previously attempted create … is absent remotely". This happens
when a create timed out and the record does not exist on the server. To resolve it:

1. Confirm in the app that the record is absent. Search for the key.
2. Remove only that key from `import-state.json`.
3. Rerun.

The importer will not decide this on its own, because a duplicate is worse than a stop.

## Using the second test account later (post-review procedure)

> **Irreversible-data warning.** An apply sends about 360 write requests (budgets, rules, actions, transactions, meals, nutrition, Journal entries) to the target account. There is no
> automated cleanup. Other features will then act on this data:
>
> - The app's lazy daily close sends **unanalyzed meals to the nutrition AI provider** when Home opens.
> - Asking the Journal sends synthetic entries to the embedding/answer provider.
> - Finance reminders and the Weekly Review may notify about the Due items.
>
> All content is synthetic, but run the import only into the dedicated test account, never the daily-use account.

### Safety checklist (all must be true)

- [ ] This branch has been reviewed and merged, or the owner explicitly approved running it.
- [ ] The target is the **dedicated test account**, which is separate from the real daily-use account.
      It is onboarded, its default currency is EUR and it has an EUR account.
- [ ] For a production origin: the owner explicitly authorised this specific run (QA-001 itself never imports).
- [ ] The token is only in the `LIFEOS_QA_TOKEN` environment variable of the current shell. It is never in
      a file, a command line or shell history.
- [ ] `config.json`, `approval.json`, state and report live under `artifacts/qa001/`, which git ignores.
- [ ] The dry-run preview (counts, budgets) was read, and `--whoami` shows the expected user.

### Steps

Rehearse first against a **local** API (Development sign-in, ADR-006), then repeat with the real test account:

```powershell
# 1. Local API: docker compose up -d postgres; run LifeOS.Api with Authentication:DevelopmentSignIn:Enabled=true
#    (see docs/development/local-development.md). Get a token for a synthetic dev identity:
$r = Invoke-RestMethod -Method Post http://localhost:5050/api/auth/dev/sign-in -ContentType application/json `
     -Body '{"subject":"qa001-dev","email":"qa001@example.invalid","displayName":"QA001"}'
$env:LIFEOS_QA_TOKEN = $r.accessToken
#    Onboard that user in the app or via the onboarding/accounts/categories endpoints (EUR).

# 2. Fill the config from the read-only identity check.
Copy-Item tools/qa-dataset/config.example.json artifacts/qa001/config.json   # edit the ids
uv run --no-project python tools/qa-dataset/qa001.py --whoami --config artifacts/qa001/config.json

# 3. Dry-run plus an UNAPPROVED approval template; review both files.
uv run --no-project python tools/qa-dataset/qa001.py --config artifacts/qa001/config.json `
    --approval-template artifacts/qa001/approval.json

# 4. A human edits approval.json: "approved": true, "dedicatedTestAccount": true,
#    "expiresAtUtc": "<a time within the next hour, e.g. 2026-10-20T18:00:00+02:00>".

# 5. Apply. If it stops, act on the printed guidance and rerun the same command.
uv run --no-project python tools/qa-dataset/qa001.py --apply --config artifacts/qa001/config.json `
    --approval artifacts/qa001/approval.json

# 6. Remove the token from the shell.
Remove-Item Env:LIFEOS_QA_TOKEN
```

For a remote origin, add `--allow-remote` to steps 2, 3 and 5. The approval records `"remote": true`.

**Token for the real test account (open point).** The app's Google sign-in only completes through the
app callback `lifeos://auth` (PKCE), and no supported CLI flow can hand an access token to this tool.
QA-001 deliberately adds none: no new endpoint, no redirect URI, no service credential. Until the owner
approves a mechanism (see *Deferred* in QA-001), the remote apply cannot be run as written. Do not extract
tokens from the device or reuse production service keys.

### Verifying the import and account isolation

- With the test token:
  - Rerun the apply command. It must report every step as `alreadyPresent` with zero `created`.
  - In the app, check the monthly budget for each month in `manifest.expected.finance.months`
    (`Spent` ≈ `expenses`).
  - Check the recurring occurrence and planned expense statuses in `expected.finance`.
  - Check the analyzed/unanalyzed meal counts and the Journal entries.
- With the **daily-use** account (normal app sign-in):
  - Search the transactions, Journal and meals for `qa001-`. There must be no results.
  - Budgets for the dataset months must be unchanged.
- Ownership comes only from the token. No request carries a `userId` (tests enforce this), and every read
  is scoped to the authenticated user.

### Clean-up (human-reviewed only)

There is no automated deletion. To remove the dataset:

1. Use `import-state.json` (`remoteId` per key) and the markers to build a list.
2. Review the list.
3. Delete the records in the test account through the app or the existing `DELETE` endpoints. Work in reverse
   dependency order: Journal entries, meals, transactions, planned expenses, recurring rules, budgets.

Recurring and planned confirmations created their own transactions, which carry the action's marker in
their note. Journal memory entries for deleted Journal entries follow the app's normal rules. Deleting the
test account itself is out of scope.
