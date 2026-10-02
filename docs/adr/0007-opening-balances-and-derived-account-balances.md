# ADR-007: Opening balances and derived account balances

## Status

Accepted

## Context

LifeOS records Finance movements as transactions: Income and Expense on one
account, and Transfers between two accounts of the same user (ADR-006). Amounts
are always positive; the transaction type determines the effect.

Users start using LifeOS with money that already exists: a bank balance, cash
in a wallet, a debt on a credit card. LifeOS needs:

- an optional starting balance for an account
- account balances, now and at an arbitrary instant

The starting balance must not distort Finance analytics. Recording it as an
Income or Expense would count money the user already had as money earned or
spent.

## Decision

### Opening balance

- An account may have **at most one opening balance**. It is optional: an
  account without one is valid.
- An opening balance is a **separate entity, never a transaction**. It has:
  - `Id`
  - `UserId`
  - `AccountId`
  - `Amount`
  - `AsOfUtc`
  - `CreatedAtUtc`
- It has **no currency of its own**. It always belongs to exactly one account,
  and the account's currency is authoritative. Storing a copy would create a
  value that the database does not enforce against the account.
- `Amount` is **signed**, and zero is valid. It uses the same range and
  precision as other monetary amounts.
- `AsOfUtc` is the instant at which the amount was true. It **must not be in the
  future**. It may be earlier than the account's creation, to describe a
  historical baseline.
- The owner and the account are taken from the account itself. The client never
  supplies a user id.

### Sign convention

- **Positive** means an asset: money or value the user has.
- **Negative** means a liability: money the user owes. A credit card with an
  outstanding debt has a negative balance.
- The arithmetic is the same for every account type. Showing a debt as a
  positive "amount owed" is a presentation choice.

### Derived balances

Balances are **computed from the opening balance and the transactions**. They are
never stored on the account.

Effect of a transaction on account X:

| Transaction | Effect on X |
|---|---|
| Income on X | `+Amount` |
| Expense on X | `−Amount` |
| Transfer to X | `+Amount` |
| Transfer from X | `−Amount` |
| Anything else | `0` |

`Balance(X, T)` is the balance at instant T, **before** anything that occurs at T.

- With an opening balance `OB`:
  - `T < OB.AsOfUtc`: the balance is **not available**.
  - `T ≥ OB.AsOfUtc`: `OB.Amount` plus the effects of the transactions with
    **`OB.AsOfUtc ≤ OccurredAtUtc < T`**.
- Without an opening balance: the effects of all transactions with
  `OccurredAtUtc < T`.

Consequences of the rule:

- A transaction exactly at `AsOfUtc` counts. A transaction exactly at `T` does
  not.
- Every window is half-open, like the transaction history
  (`fromUtc ≤ OccurredAtUtc < toUtc`), so
  `Balance(X, to) = Balance(X, from) + net effect in [from, to)`.
- The current balance uses `T = now`. Future-dated transactions are not counted
  until they occur.
- Each account is evaluated against its own baseline. A transfer may count for
  one account and be before the baseline of the other.
- Transactions dated before an account's baseline are **allowed** and remain
  visible in lists and analytics. They **do not change** that account's balance,
  because the opening balance already includes them.
- Opening balances never appear in income or expense figures.
- Balances are in the account's currency. There is no currency conversion.

The balance rule is a **pure Domain calculation**. In the first version it runs
over the user's transactions loaded by Application.

### Meaning of AsOfUtc

- `AsOfUtc` is the **exact instant** at which the declared balance is true.
- When a client captures a balance the user observes now, it uses the **current
  instant**. A balance read at 18:00 already includes that day's earlier
  transactions; a baseline at the start of the day would count them twice.
- An earlier instant declares a historical baseline.
- A small allowance for clock differences between client and server applies to
  the "not in the future" rule.

### Creation and changes

- Creating an account may include an optional opening balance. The account and
  the opening balance are persisted **together, in one save**.
- An existing account can receive an opening balance through a dedicated
  endpoint.
- In the first version an opening balance is **create-only**:
  - the same values again are an idempotent success;
  - different values are rejected as a conflict.
- Editing later is possible without data migration, because balances are
  derived. It is deferred until needed.

### Onboarding

- The first-account step may collect an optional opening balance.
- There is **no additional onboarding state**. Onboarding completes with an
  account, with or without an opening balance.

### Persistence

- A table `opening_balances` with a signed `numeric(19,4)` amount.
- A **unique index on `account_id`** allows at most one opening balance per
  account.
- A **composite foreign key `(account_id, user_id) → accounts(id, user_id)`**
  prevents an opening balance on another user's account, as for transactions
  (ADR-006).
- Deletes are restricted, as for other Finance data.

## Rationale

- **Correct analytics:** a starting balance is not money earned or spent.
- **One source of truth:** stored balances can drift from the transactions.
  Derived balances cannot, and a backfilled or corrected transaction is
  reflected automatically.
- **Consistent time windows:** the same half-open rule for balances and history
  keeps period figures additive.
- **Simple, uniform arithmetic:** signed amounts make credit cards and overdrafts
  work without type-specific rules.
- **No duplicated data:** the account's currency is the only currency.

## Consequences

Positive consequences:

- account balances at any instant, from one deterministic rule
- history can be backfilled without breaking current balances
- analytics remain based on transactions only
- ownership and uniqueness are enforced by PostgreSQL

Trade-offs:

- computing balances reads transactions on every request. This is acceptable at
  personal-app volumes; a database aggregation can replace it behind the same
  port.
- a balance before an account's baseline cannot be answered
- opening balances cannot be corrected in the first version
- an opening balance is only as accurate as its instant: clients must send the
  exact instant the balance was true, in UTC

Deferred:

- editing or deleting an opening balance, with an audit trail
- balance adjustments and reconciliation
- currency conversion
- database-side balance aggregation

## Amendment: account management (v1)

Accounts can now be renamed, have their type changed, and be deleted. This
refines the rules above; everything else stands.

- The **currency of an account is immutable**: existing transactions carry it.
- An existing **opening balance stays immutable** (create-only). An account
  without one can still receive it through the dedicated endpoint.
- An account that **any transaction references** (as its account, or as the
  source or destination of a transfer) **cannot be deleted**.
- An account without transactions can be deleted. Its opening balance has no
  meaning without the account, so both are deleted **together, in one database
  transaction**: either both are gone or neither is.
- The foreign keys stay `RESTRICT` and remain the final backstop. A delete that
  loses a race with a new transaction is rejected ("has transactions"); one that
  loses a race with a new opening balance is rejected as a retryable conflict.
  Nothing is cascaded, and no transaction is ever modified.
- Deleting the last account is allowed; the onboarding state does not change.

## Amendment: transaction management (v1)

- A transaction's **type is immutable** after creation. Changing it means
  deleting the transaction and creating another one.
- Editing or deleting a transaction changes the authoritative transaction
  history. Account balances and monthly analytics stay **derived** values, so
  they reflect the current set of stored transactions with no recomputation
  table and no migration.

## Amendment: current account reconciliation (FIN-003)

An opening balance stays immutable: it describes the original baseline. Correcting
it would rewrite every historical derived balance. A current reconciliation instead
adds an immutable `AccountBalanceAdjustment`, a separate movement that is never an
Income, Expense or Transfer transaction. It has no category or currency copy; the
account owns its currency. Signed amount and observed balance use numeric(19,4).
Notes are trimmed and blank notes become null.

Application captures a server `TimeProvider` instant (UTC, whole microseconds) and
computes `observed - calculated` from an owned account and one consistent database
snapshot. Inputs contain only the observed balance and optional note; account and
owner come from the route/authentication. There is no historical reconciliation,
edit or delete endpoint. An unavailable current balance returns a conflict, never
an invented baseline. A zero difference succeeds without creating an adjustment.

Existing transaction rules are unchanged: transactions use `OccurredAtUtc < T` and
an inclusive opening baseline. Adjustment effects are included at their effective
instant (`EffectiveAtUtc <= T`), like the opening baseline, so a just-completed
reconciliation is observable immediately, including at the same microsecond.
Adjustments before an account's opening baseline, after T, or belonging to another
account/owner do not affect its balance. The result is opening amount + transaction
effects + adjustment effects; later transaction edits/backfills remain authoritative
and may change the derived result again.

Adjustments and reconciliation receipts are outside transaction history, monthly
analytics and budget spending. Those features keep querying transactions only.

A UUID `Idempotency-Key` header identifies a submission. Every success writes an
immutable `account_reconciliations` receipt, including no-op requests, atomically
with its optional adjustment. The receipt preserves the original result and
normalized payload; retries with the same key/payload return that result even
after intervening spending. A changed payload under a used key returns 409.
Failure does not consume a key. Keeping zero receipts prevents a retried no-op from
later undoing new spending. Previous derived totals use unconstrained PostgreSQL
numeric, because summed balances may exceed a single monetary movement's range.

Infrastructure implements a focused Application transaction port: SERIALIZABLE
snapshot, owned account `FOR UPDATE`, and bounded retries for serialization,
deadlock or the exact request-key uniqueness collision. Domain/Application own
all arithmetic. Concurrent reconciliation commands are recalculated in database
serial order rather than applying two stale deltas. Independent transaction edits
can serialize before or after reconciliation; reconciling does not freeze history.
A server clock earlier than an existing adjustment causes a retryable conflict
instead of writing an adjustment that precedes already committed corrections.

Accounts with adjustment or reconciliation audit records, including no-op receipts,
cannot be deleted. Composite ownership FKs use RESTRICT, preserve the records and
protect the account-delete race. The API returns a clear 409. The existing atomic
account/opening-balance deletion remains available for accounts without history.

The Accounts action panel provides signed observed input and a preview, disables
duplicate submissions, and retains a submission key for transport retries. On
success it closes and reloads balances using server time, so a device clock behind
the server does not temporarily hide the new correction. FIN-002 remains limited
to Home/Portfolio presentation; reconciliation does not broaden privacy mode.
