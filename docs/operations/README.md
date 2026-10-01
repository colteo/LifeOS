# Operations

How LifeOS runs in Production and how it is released, backed up and restored.

| Document | Use it for |
|---|---|
| [Production runbook](production-runbook.md) | The first Production release (Part A), every later release (Part B), rollback and stop conditions |
| [Backup and restore](backup-restore.md) | Backups, retention, the restore drill and disaster recovery |

Production shape (v1):

```text
Android APK (signed, sideloaded)  --HTTPS-->  Cloud Run: lifeos-api  --TLS-->  Neon PostgreSQL
```

- One user, one Cloud Run instance at most, no custom domain (`*.run.app`).
- Every release is manual and follows the runbook; there is no CI/CD yet.
- No real credentials, emails or hostnames are ever written into this repository.
  The documents use placeholders such as `<GCP_PROJECT_ID>` and `<NEON_HOST>`.
