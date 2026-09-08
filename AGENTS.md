# Project Instructions for AI Agents

> Bootstrapped by nohell v9

This file (`AGENTS.md`) is the routing index for any AI agent working in this repo, and the single source of truth for project instructions. `CLAUDE.md` is a thin pointer that imports this file so Claude Code loads it automatically; all real content lives here. Non-Claude agents: read this file in full before doing anything else.

## Session start protocol

A SessionStart hook (`.claude/hooks/session-start.ps1`) injects the project context into every fresh session automatically: all hard rules from `.docs/rules/`, every high-severity learning plus the three most recent, and any `status: in-progress` plan. Trust that injection; do not re-read those files at session start.

At the start of a fresh session:

1. Confirm the hook context block (`## Project context (auto-injected...)`) is present. If it is missing, the hook is broken: say so, then fall back to reading `.docs/rules/` in full, the three most recent files in `.docs/learnings/`, and globbing `.docs/plans/*.md` for `status: in-progress` yourself.
2. If the hook surfaced an in-progress plan, apply the Resume protocol below before any new work. Also apply `DEVELOPMENT_BUILD_PLAN.md`'s own resume protocol (section 2 of that file) for anything in its M00-M14 milestone scope - it is a separate, older, and independently authoritative ledger.
3. If the user opened with just a greeting, reply `Ready to work.` (plus the one-line resume summary if an in-progress plan exists). No other ceremony.

## Targeted re-reads (during work)

The hook covers session start. During work, re-read selectively:

- Before touching an area that plausibly has an older learning (auth, payments, a fragile module), Grep `.docs/learnings/` for matching tags and read the hits.
- Before an action a specific rule governs, re-open that one rule file, not the whole directory.
- Do not re-read this file or all of `.docs/rules/` per task. Once per session is the contract.
- Before any work inside the M00-M14 milestone scope, read `.docs/plans/DEVELOPMENT_BUILD_PLAN.md`'s current handoff section (its "Session state" table) - it is the durable progress ledger for that scope and is not injected by the hook.

## Resume protocol (check before starting any new work)

Sessions get interrupted. The session-start hook surfaces any `.docs/plans/` file with `status: in-progress`. When one exists:

1. Surface it to the user with: filename, goal, the next unchecked `- [ ]` task, and the most recent Log entry.
2. Ask the user: resume the in-progress plan, switch to the new request (leaving the old plan in-progress), or abandon it (set `status: abandoned` with a Log entry explaining why).
3. Do not silently start new work while a plan is in-progress.

If the user's request is itself the continuation of an existing plan, jump straight to the implementer with that plan path.

Separately, `.docs/plans/DEVELOPMENT_BUILD_PLAN.md` carries its own always-applicable resume protocol for the M00-M14 milestone scope (see its section 2, "Resume protocol and durable state"): inspect git status and recent commits, read its Session state table, and select one concrete next action before substantial implementation there.

See `.docs/rules/plan-execution.md` for the full `.docs/plans/` format and execution protocol, and its note on how the two plan mechanisms relate.

## Repository layout for AI machinery

```
.claude/
├── settings.json        permissions, hooks, agent registration
├── agents/               subagent definitions (YAML frontmatter)
├── commands/             slash commands
└── hooks/                session-start hook script

.docs/
├── plans/                implementation plans (incl. this project's DEVELOPMENT_BUILD_PLAN.md and IMPLEMENTATION_PLAN.md)
├── learnings/            append-only lessons from past sessions
├── rules/                hard rules, more granular than this file
└── research/             researcher agent's findings and design references
```

Anything markdown that is not user-facing documentation goes in `.docs/`. User-facing docs (README) stay at the root or in `docs/` (no leading dot).

Always start Claude Code from this repo's root, not from a parent folder. Sessions started from a parent directory may register agents and instructions from OTHER projects; agents in the harness list that are not in this repo's `.claude/agents/` are foreign and must not be used for this project's work.

## Available agents

Project agents in `.claude/agents/` register natively: dispatch them by name via the Agent tool.

| Agent | When to call | Output |
|-------|--------------|--------|
| `planner` | Before any non-trivial change outside the M00-M14 ledger scope | `.docs/plans/YYYY-MM-DD-<slug>.md` |
| `implementer` | After a plan exists | Code changes, completion note on the plan |
| `reviewer` | After implementer finishes | Structured review with severity findings |
| `researcher` | When you need codebase or external context | `.docs/research/YYYY-MM-DD-<slug>.md` |
| `debugger` | When something is broken and root cause is unclear | Root cause analysis + proposed fix |
| `learner` | After a meaningful task, OR via `/learn` | New entries in `.docs/learnings/`, edits to agents or AGENTS.md |
| `dotnet-verifier` | After any code-changing task, to run the exact headless verification sequence | Pass/fail per command with real output/counts |
| `provider-adapter-builder` | Adding to or modifying the OpenAI/Codex or Anthropic/Claude Code provider adapters | Adapter code changes plus matching Provider.Tests fixtures |

### Routing heuristics

- "Build me X" / "let's add feature X" of any non-trivial size, outside the M00-M14 ledger: `planner` -> `implementer` -> `reviewer` -> `learner`. The planner writes a milestone+checkbox plan to `.docs/plans/`; the implementer ticks boxes live as it goes.
- Work inside the M00-M14 milestone scope: follow `DEVELOPMENT_BUILD_PLAN.md`'s own resume protocol directly; do not route through `planner`.
- "Continue / resume / pick up where we left off": check `DEVELOPMENT_BUILD_PLAN.md`'s Session state table first (it is the primary ledger); also check `.docs/plans/` for a `status: in-progress` file and hand it to `implementer`.
- "Fix this bug": `debugger` -> `implementer` (to apply the fix) -> `learner`.
- "Where is X / how does Y work": `researcher`.
- "Verify this builds and passes" / after any code change: `dotnet-verifier`.
- Touching `src/UseNotch.Providers.OpenAI` or `src/UseNotch.Providers.Anthropic`: `provider-adapter-builder`.
- "I just corrected you / that detour was painful / we discovered a constraint": invoke `learner` immediately, or run `/learn`.

If the user says any of "learn from that", "remember this", "don't make that mistake again", "save this lesson" - invoke the `learner` immediately. The slash command `/learn` does the same thing.

## Self-improvement loop (this is core, do not skip it)

After completing any non-trivial task, invoke the `learner` subagent. Non-trivial means at least one of:
- Involved a bug fix
- Made an architecture or design decision
- Surfaced a constraint that was not previously documented
- Cost time on a wrong turn
- Was corrected by the user

The learner has permission to edit `.claude/agents/**`, `.docs/**`, and `AGENTS.md` without asking. Let it.

If you finish a task and decide it does not warrant invoking the learner, that is fine, but the default is to invoke it.

## Hard conventions

- Plans live in `.docs/plans/`. Filename format: `YYYY-MM-DD-<short-slug>.md`. Format and execution protocol defined in `.docs/rules/plan-execution.md`. Plans carry `status:` and `base:` frontmatter, milestone+checkbox bodies, and an append-only Log. `DEVELOPMENT_BUILD_PLAN.md` and `IMPLEMENTATION_PLAN.md`, also under `.docs/plans/`, predate this format and keep their own structure; do not reformat them to match.
- Learnings live in `.docs/learnings/`. Filename format: `YYYY-MM-DD-<short-slug>.md`. Frontmatter required (`date`, `tags`, `severity`, `applies-to`).
- Rules live in `.docs/rules/`. One concept per file. Short, imperative. The session-start hook injects every rule into every session, so rules carry a permanent context cost: add one only when it truly is non-negotiable.
- Research notes live in `.docs/research/`. Filename format: `YYYY-MM-DD-<short-slug>.md`.
- Never modify `.docs/rules/` casually. Rules are promoted from learnings or added by the user.
- Never delete from `.docs/learnings/`. The learner can supersede an old learning by writing a newer one and editing the old one to add a `superseded-by:` line in frontmatter.
- This file documents current state only, never version history. See `.docs/rules/docs-current-state-only.md`: no changelog sections, and the Key paths table stays lean. `DEVELOPMENT_BUILD_PLAN.md`'s dated milestone ledger is a deliberate, documented exception to that rule.

### Agent docs must stay in sync (non-negotiable)

If you add, remove, rename, or change the behavior of any file in `.claude/agents/`, you MUST update in the same commit/turn:

1. The **Available agents** table above (add/remove/edit the row).
2. The **Routing heuristics** subsection above (add/remove/edit the line that mentions the agent).

This file (`AGENTS.md`) is the single source of truth; `CLAUDE.md` is only a pointer and needs no update. A change to an agent file without a corresponding doc update is an incomplete change. Reviewer agent: flag this as a **blocker** finding if you ever see it. Learner agent: if you find them out of sync from a past session, fix it as your first action.

This rule applies to any agent that edits `.claude/agents/` (including the learner editing itself).

### Commit and PR hygiene (non-negotiable)

- **Never co-author commits as an AI model.** Do not add `Co-Authored-By: Claude`, `Co-Authored-By: AI`, `Co-Authored-By: GPT`, or any similar trailer to commit messages. Do not add equivalent attributions in PR descriptions or release notes. The user is the sole author. This default is permanent unless the user explicitly says "credit Claude as co-author on this commit" or similar for a specific instance.
- **Never include "Generated with Claude Code" or equivalent footers** in commits, PR bodies, issue comments, or any other written artifact unless the user explicitly asks for it.
- **No emojis in commit messages.** Stick to plain text.
- **No em dashes in commit messages, PR bodies, or any prose this project produces.** Use periods, commas, parentheses, or colons instead.

### Image generation

For any image generation or editing task, use the `cc-nano-banana` skill. Default output location for this project's generated images is `docs/brand/` (this project's existing convention for the app mark and generated PNGs; see `README.md`'s Brand section and `scripts/New-AppIcon.py`). Source originals are saved per the user's global config; do not hardcode a path for them here.

## Project-specific section

### What this project is

UseNotch is a non-elevated Windows 11 x64 tray utility (.NET 8, Avalonia 11) that shows OpenAI Codex and Anthropic Claude Code usage quotas in a screen-edge overlay, read-only against each tool's own local credentials. See `.docs/research/2026-09-08-bootstrap-context.md` for the full context-gathering note.

### Stack

- .NET 8 SDK, pinned to `8.0.424` in `global.json`. .NET 8 support ends 2026-11-10; migration to a supported LTS is a tracked release gate.
- Avalonia 11.3.20 (FluentTheme) + CommunityToolkit.Mvvm 8.4.2, centrally pinned in `Directory.Packages.props`.
- Microsoft.Data.Sqlite 10.0.11 (Codex activity monitor, read-only WAL queries) and Microsoft.Win32.SystemEvents / System.Security.Cryptography.ProtectedData for session-lock notifications and current-user DPAPI.
- WiX v5 (pinned as a local tool via `dotnet tool restore`) for the per-user MSI installer.

### How to run

```powershell
dotnet restore UseNotch.sln --locked-mode
dotnet build UseNotch.sln -c Release --no-restore
dotnet run --project src/UseNotch.App -c Release --no-build --no-restore
```

Add `--software-render` for the supported software-rendering fallback. If the pinned SDK is not installed system-wide, use the isolated copy under the ignored `.tmp/dotnet` (`.\.tmp\dotnet\dotnet.exe`) instead of `dotnet`.

### Verification

See `.docs/rules/verification.md` for the exact headless command sequence (locked restore, Release build, full test run, format check, whitespace check); the `dotnet-verifier` agent runs it. Interactive/native checks (`scripts/Test-*.ps1`, `installer/Test-Installer.ps1`) need a real non-elevated Windows desktop session and are separate; hosted CI success is not evidence any of them passed.

### Key paths

| Path | Purpose |
|---|---|
| `src/UseNotch.Domain` | Immutable usage models, no dependencies |
| `src/UseNotch.Application` | Use cases, polling, activity, state, cache, settings contracts |
| `src/UseNotch.Providers.OpenAI` | Codex discovery, credentials, usage/activity adapters |
| `src/UseNotch.Providers.Anthropic` | Claude Code discovery, credentials, usage/activity adapters |
| `src/UseNotch.Infrastructure` | JSON settings, application paths, sanitized diagnostics |
| `src/UseNotch.Platform.Windows` | Win32 overlay, monitors, single instance, startup, DPAPI |
| `src/UseNotch.App` | Avalonia composition root, views, view models |
| `tests/` | One test project per source layer, plus `OverlayInputHarness` (independent-process input probe) |
| `scripts/` | Interactive/native PowerShell checks (overlay behavior, placement, lifecycle, live provider slice, resource measurement, version agreement) |
| `installer/` | WiX v5 MSI authoring, build script, structural validator |
| `.docs/plans/DEVELOPMENT_BUILD_PLAN.md` | This project's own durable milestone ledger (M00-M14): resume protocol, session state, evidence log. Read before any milestone work |
| `.docs/plans/IMPLEMENTATION_PLAN.md` | Original design document |
| `.docs/research/CODENOTCH_WINDOWS_PORT_BRIEF.md` | Reference brief informing the design |

### Project hard rules (pre-existing, non-negotiable)

- Monitor only OpenAI/Codex and Anthropic/Claude Code. Keep framework/native/provider concerns behind the documented project boundaries (enforced by tests).
- Never commit credentials or raw account data. Do not read real provider credentials for foundation tests; use deterministic network-free fixtures.
- Git initialization and remote configuration belong to the user. Do not push, tag, or publish without authorization.
- Work on the requested milestone only. Do not mark a `DEVELOPMENT_BUILD_PLAN.md` task complete before verification, and do not advance to another milestone without applicable task scope.
- Update `DEVELOPMENT_BUILD_PLAN.md`'s ledger with actual commands, results, limitations, and an exact next action before stopping any session that touched its milestone scope. Commit each completed milestone using its prescribed ID.
- `M01` has a temporary normal window; tray lifetime begins in M02; native overlay behavior begins in M03. Do not infer those features from a passing startup check.
