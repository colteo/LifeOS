"""AI-009 I1: Quick Capture, the Python side (ADR-015, PROPOSED).

One user message → the router splits it into segments (module + action + verbatim spans) → one
specialist per Finance, Hydration or Meal segment interprets it into the v2 section of its module.
This package only interprets: it knows no LifeOS identifier, reads no database, creates or
confirms nothing and chooses no .NET handler; .NET orchestrates and applies (ADR-015).

Canonical contracts: docs/contracts/quick-capture/{router,finance,hydration,meal}/v1. The
pipeline is disabled by default (`LIFEOS_AI_QUICK_CAPTURE_PROVIDER`, config.py) and enabled in no
environment.
"""
