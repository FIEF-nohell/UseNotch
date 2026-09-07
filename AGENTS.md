# UseNotch implementation instructions

- Read `IMPLEMENTATION_PLAN.md` and `DEVELOPMENT_BUILD_PLAN.md` before implementation. The build plan is the durable progress ledger.
- Work on the requested milestone. Do not mark a task complete before verification or advance to another milestone without applicable task scope.
- Update the ledger with actual commands, results, limitations, and an exact next action before stopping. Commit each completed milestone using its prescribed ID.
- Preserve existing user changes. Git initialization and remote configuration belong to the user. Do not push, tag, or publish without authorization.
- Monitor only OpenAI / Codex and Anthropic / Claude Code. Keep framework/native/provider concerns behind the documented project boundaries.
- Never commit credentials or raw account data. Do not read real provider credentials for foundation tests.
- No em dashes, AI attribution, generated-by footers, or co-author trailers in code, documentation, or commits.
- Build using the SDK in `global.json`. Dependencies are centralized in `Directory.Packages.props`; commit updated lock files when dependency changes are intentional.
- Validate with locked restore, Release build, test discovery/execution, and formatting verification. The README contains exact commands.
- Interactive Windows checks are separate from headless tests. Do not label skipped manual/native checks as passed.
- M01 has a temporary normal window. Tray lifetime begins in M02; native overlay behavior begins in M03. Do not infer those features from a passing startup check.
