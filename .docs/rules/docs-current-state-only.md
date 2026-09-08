# Rule: AGENTS.md documents current state only, never version history

No changelog content in `AGENTS.md` or `CLAUDE.md`: no `### vX.Y:` sections, no "new in vX" / "landed in vX" entries, no dated release blocks. Release notes live in `CHANGELOG.md`. When a feature ships or changes, edit the relevant current-state section in place (Stack, Key paths, How to run); do not append a versioned block.

The **Key paths** table stays lean: path plus a short purpose phrase. Long descriptive cells listing every widget or sub-feature rot exactly like changelogs do. Detail belongs in the code or in a dedicated doc, not in the index.

Note: this rule governs `AGENTS.md`/`CLAUDE.md` specifically. `.docs/plans/DEVELOPMENT_BUILD_PLAN.md` is a deliberate exception: it is this project's durable milestone evidence ledger by design and is expected to grow with dated entries.

## Why
Version-keyed sections and bloated table cells grow monotonically, are never pruned, and bury the facts an agent actually needs. An index that must be read every session has to stay small.

## How to apply
- Reviewer: flag any version-keyed or changelog-style section added to `AGENTS.md`, and any Key-paths cell longer than one line, as a **blocker** finding.
- Learner: if you find one from a past session, move its still-current facts into the matching current-state section and delete the versioned block.
