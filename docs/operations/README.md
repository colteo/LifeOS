# Operations

How LifeOS runs in Production and how it is released, backed up and restored.

| Document | Use it for |
|---|---|
| [Production runbook](production-runbook.md) | The first Production release (Part A), every later release (Part B), rollback and stop conditions; the AI service (Part D), push (Part E) and the Weekly Review automation tick (Part F) |
| [Backup and restore](backup-restore.md) | Backups, retention, the restore drill and disaster recovery |

Production shape (v1):

```text
Android APK (signed, sideloaded)  --HTTPS-->  Render Free (account A): lifeos-api  --TLS-->  Neon Free PostgreSQL
                                                     |
                                                     +--HTTPS + service key-->  Render Free (account B): lifeos-ai  -->  Groq
```

- One user; two Render Free services in two Render accounts, one instance each (each sleeps after
  15 idle minutes; an external keepalive pings `/health/live` during waking hours); no custom domain
  (`*.onrender.com`).
- No payment method on Render or Neon; the Google Cloud project is used only for Google OAuth.
- Every release is manual and follows the runbook; there is no CI/CD yet.
- No real credentials, emails or hostnames are ever written into this repository.
  The documents use placeholders such as `<GCP_PROJECT_ID>` and `<NEON_HOST>`.
