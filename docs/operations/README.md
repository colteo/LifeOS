# Operations

How LifeOS runs in Production and how it is released, backed up and restored.

| Document | Use it for |
|---|---|
| [Production runbook](production-runbook.md) | The first Production release (Part A), every later release (Part B), rollback and stop conditions; the AI service (Part D), push (Part E), the Weekly Review automation tick (Part F), Finance reminders (Part G), LifeOS Assistant v1 Finance-only (Part H) and the **blocked** AI-008 Assistant release (Part I) |
| [Finance reminder diagnostics](finance-reminder-diagnostics.md) | Read-only checks when a Finance reminder notification is missing (runbook G4) |
| [Backup and restore](backup-restore.md) | Backups, retention, the restore drill and disaster recovery |

Production shape (as designed by the runbook; OpenAI serves Journal Memory when its key is configured and
LifeOS Assistant once Part H is executed):

```text
Android APK (signed, sideloaded)  --HTTPS-->  Render Free (account A): lifeos-api  --TLS-->  Neon Free PostgreSQL
                                                     |
                                                     +--HTTPS + service key-->  Render Free (account B): lifeos-ai  -->  Groq, OpenAI
```

> **⛔ AI-008 is not released.** `main` contains AI-008 (`733ac2f`). Do not deploy a commit that contains
> it until the gates of [runbook Part I](production-runbook.md#part-i--lifeos-assistant-multimodule-ai-008-blocked)
> are met (stop condition S26).

What is actually live in Production (which runbook Parts were executed, which migrations are applied, which
APK version is installed) is **not recorded in this repository**. Check the Render and Neon consoles and
`__EFMigrationsHistory` before any release.

- One user; two Render Free services in two Render accounts, one instance each (each sleeps after
  15 idle minutes; an external keepalive pings `/health/live` during waking hours); no custom domain
  (`*.onrender.com`).
- No payment method on Render or Neon; the Google Cloud project is used for Google OAuth and, once runbook
  Part E is executed, for Firebase Cloud Messaging.
- Every release is manual and follows the runbook; there is no CI/CD yet.
- No real credentials, emails or hostnames are ever written into this repository.
  The documents use placeholders such as `<GCP_PROJECT_ID>` and `<NEON_HOST>`.
