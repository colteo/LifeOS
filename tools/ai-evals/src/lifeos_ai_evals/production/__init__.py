"""AI-003: live evaluation of the production LifeOS AI service package (lifeos-ai).

Adapters here call the production prompts, schemas, request bodies and Groq transport
directly; the lab never copies them. Only provider wiring and attempt telemetry live in
this package.
"""
