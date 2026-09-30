# ADR-006: External identity authentication with LifeOS-issued sessions and per-user data ownership

## Status

Accepted

The Android browser/callback transport described below was validated on a
physical device (see "Validation").

## Context

LifeOS was built as a single-user application. It has no authentication, and
Finance data (accounts, categories, transactions) is global. Starter
categories are inserted once, globally, by a migration.

LifeOS now needs:

- authentication for the Android client and the API
- several users, each seeing and changing only their own data
- a first-run onboarding flow that prepares a new user's Finance data

Constraints:

- LifeOS must not depend on sending email. There is no email/password login,
  no email verification, no password reset and no email delivery service.
- Domain and Application must stay independent of provider SDKs and
  ASP.NET Core (ADR-001).
- The mobile app must not hold secrets.
- Financial data is sensitive. Isolation between users must not rely on UI
  filtering alone.
- LifeOS is pre-production. All current data is disposable.

## Decision

### Identity

- **Google is the first external identity provider.** The first successful
  Google sign-in also registers the user. There are no LifeOS passwords.
- A LifeOS **`User`** is separate from how the user signs in. An
  **`ExternalIdentity`** links a user to a provider account.
- The external identity key is **`(provider, subject)`**, where `subject` is
  the provider's stable subject identifier (Google `sub`). The pair is unique.
- **Email is informational only.** It is not a key and not unique, and it is
  never used to link or merge identities. Linking by email would allow account
  takeover across providers.
- Domain and Application are **provider-neutral**. Google-specific
  authentication code lives only in the outer `LifeOS.Api` boundary, which
  maps the provider result to a neutral external login
  (provider, subject, email, display name).

### Authentication flow and sessions

- The Android client signs in through the **system browser**
  (MAUI `WebAuthenticator`). The **API acts as the OAuth client** of Google:
  Google redirects only to the API, and the Google client secret stays on the
  server.
- After resolving or creating the LifeOS user, the API redirects to the app's
  own callback scheme with a **short-lived, single-use LifeOS authorization
  code**. The app exchanges the code for tokens. The code is bound to a
  **PKCE** challenge created by the app. The callback redirect URI is
  validated against an allowlist.
- **LifeOS issues its own session tokens:**
  - a short-lived signed **access token** whose subject is the LifeOS
    `UserId`, never the provider subject;
  - an opaque **refresh token**, stored server-side only as a hash, rotated on
    every use, with reuse detection and revocation on sign-out.
- Google tokens are never accepted as credentials by LifeOS endpoints.
- On the device, the refresh token is kept in platform secure storage and the
  access token in memory only.

### Authorization

- **Authentication** establishes who the caller is, from a valid
  LifeOS-issued access token.
- **Authorization** is configured **endpoint by endpoint.** Once authentication 
  is enabled, endpoints require an authenticated user by default. `AllowAnonymous` 
  is applied only to the
  individual endpoints that genuinely need it (for example the steps of the
  sign-in flow itself and technical health checks). No path prefix is
  anonymous as a whole.
- **Resource-level authorization** is data ownership (below).

### Per-user data ownership

- `Account`, `Category` and `Transaction` are **owned by a user** (`UserId`,
  required). The `UserId` is set from the authenticated identity, **never
  from a request payload**. Request contracts do not contain a `UserId`.
- The authenticated LifeOS `UserId` is passed **explicitly** from the API to
  Application use cases and on to repository ports. Every Finance read and
  write is scoped to that user. There is no ambient current-user service and
  no implicit EF Core query filter as the primary mechanism.
- Application verifies that every referenced account and category belongs to
  the caller. A resource owned by another user is reported the same way as a
  missing one, so its existence is not revealed.
- PostgreSQL enforces ownership as a **backstop**: owned tables carry a
  non-null `user_id`, and references between owned rows use composite foreign
  keys on `(id, user_id)`, so a row can reference only rows of the same user.

### Development-only sign-in

- A sign-in endpoint that bypasses Google may exist for local development and
  testing. It uses the same provider-neutral sign-in use case.
- It is available only when **both** conditions hold: the environment is
  Development **and** an explicit configuration flag enables it.
- The API **refuses to start** if the flag is enabled outside Development.

### Onboarding

- A user is created on first sign-in with onboarding incomplete.
- Implemented onboarding states:
  1. `PendingFinanceProfile`: the user chooses a default currency, and the
     user's starter categories are created.
  2. `PendingFirstAccount`: the user creates a first account.
  3. `Completed`.
- The server enforces the state transitions. The client drives the flow.
- An opening-balance step is expected in the future. It is not part of this
  decision. Opening balances are the next domain decision after this epic.

### Starter categories

- The global starter-category seed is removed.
- Each user receives **their own persisted copies** of the starter categories
  during onboarding. The starter set is an Application-level default catalog.
- Starter categories are created by an idempotent use case, not by EF Core
  `HasData` or migrations.

### Migrations

- Because all existing data is disposable, the current development migration
  chain is replaced by a single baseline migration for the multi-user schema,
  and local databases are recreated. The procedure is operational and is
  documented in the development guide, not in this ADR.

## Validation

The browser and callback transport was validated on a physical Android
device with a temporary spike, since removed:

- MAUI `WebAuthenticator` opened an Android Custom Tab on the local API,
  reached through `adb reverse tcp:5050 tcp:5050`.
- The API was addressed as `http://localhost:5050`, not `127.0.0.1`. ASP.NET
  Core builds the Google `redirect_uri` and scopes the correlation cookie from
  the request host, so the start URL and the registered redirect URI must use
  the same host.
- Google redirected to `http://localhost:5050/signin-google`, the API received
  the Google identity, and the `lifeos://` callback returned to the app.
- The default ASP.NET Core correlation-cookie settings (`Secure`,
  `SameSite=None`) worked. No Development-only cookie or security relaxation
  was required.

Production cookie and security settings must still not be weakened for local
development. Any future Development-only adjustment must be proposed and
approved explicitly.

## Rationale

- **No email dependency:** an external identity provider removes passwords,
  verification and reset flows.
- **Stable identity:** the provider subject does not change when the user
  changes email address. Email-based matching would.
- **Framework independence:** keeping provider code at the outer boundary
  keeps Domain and Application testable and lets further providers be added
  without changing the core.
- **No secrets on the device:** the API is the confidential OAuth client, and
  PKCE protects the hand-off to the app.
- **Control over sessions:** LifeOS-issued tokens can be revoked and rotated,
  and they carry the LifeOS `UserId`, not a provider identifier.
- **Compile-time scoping:** an explicit `UserId` in use cases and repository
  ports makes an unscoped query a visible code change rather than a forgotten
  filter.
- **Defense in depth:** Application ownership checks are backed by database
  constraints, so a bug in one layer does not expose another user's data.
- **Owned starter data:** per-user copies can be renamed or deleted by each
  user without affecting others.

## Consequences

Positive consequences:

- LifeOS supports multiple users with isolated Finance data
- no email infrastructure is required
- Domain and Application remain free of authentication frameworks and SDKs
- ownership is enforced in Application and in PostgreSQL

Trade-offs:

- the API must implement token issuance, refresh-token storage, rotation and
  revocation
- every Finance use case, repository method and test gains a `UserId`
- the sign-in flow depends on the system browser and a custom callback scheme;
  a custom scheme can be claimed by other apps, which PKCE mitigates
- local development of the Google flow needs extra setup (Google OAuth client,
  User Secrets, `adb reverse`) and may need an approved Development-only
  adjustment for cookies over plain HTTP
- the migration history is reset once, and existing local databases must be
  recreated

Deferred:

- further identity providers and linking several providers to one user
- Android App Links (verified HTTPS callbacks) instead of a custom scheme
- asymmetric signing keys and key rotation
- shared storage for authorization codes when the API runs as several instances
- account deletion and data export
- opening balances
