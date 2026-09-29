# Claude Code Instructions

Before making any change, read and follow `AGENTS.md`.

`AGENTS.md` is the canonical source for repository-wide engineering,
architecture, workflow, review, testing, security, and AI-agent rules.

Also read the documentation relevant to the current task under:

- `docs/architecture/`
- `docs/adr/`
- `docs/development/`

## Claude Code workflow

When asked to plan before implementation:

1. inspect the existing repository and relevant documentation
2. present a concise implementation plan
3. list the files expected to change
4. identify blockers separately from deferred improvements
5. wait for approval before editing

During implementation:

- follow the approved scope
- do not implement deferred improvements automatically
- do not perform unrelated refactoring
- do not add packages or frameworks unless required
- do not commit unless explicitly requested

After implementation, report:

- implemented changes
- build/test results
- blocking issues
- deferred improvements
- changed files

If any instruction here conflicts with `AGENTS.md`,
`AGENTS.md` takes precedence.