---
name: implementation
description: Execute an approved SportsModels.Basketball GitHub task in a focused C# branch with tests, review and a pull request.
---
# Implementation

1. Read AGENTS.md, docs/CODING_PRACTICES.md and the GitHub issue, including comments and parent context.
2. **Readiness gate:** Verify the Project Manager recorded **Ready for Implementation** and the specification has scope, inputs/outputs, dependencies, acceptance criteria and testing. If PM marked Ready, do not demand redundant approval. If readiness is missing or the issue is materially underspecified, consult PM/user and stop rather than improvise.
3. Check parallel tasks, shared files, branches and dependencies. Create a dedicated branch named e.g. `task/2-nba-play-by-play-json` from the repository's actual default branch (currently `master`); do not assume `main` exists.
4. Implement only agreed scope, following existing architecture. Choose reasonable low-level implementation details delegated by the issue. Escalate substantial domain/design uncertainty.
5. Add fast NUnit unit tests and run `dotnet build` and `dotnet test` where applicable. Execute an end-to-end smoke test if feasible. Never claim a test ran if it did not; report environmental blockers.
6. Review sequentially for functional correctness/QA, material performance issues, and architecture/readability. Fix issues and rerun tests.
7. Commit, push the task branch and open a **pull request** referencing the issue (e.g. `Closes #2`), following the PR creation guidance below. Do not merge without user approval.
8. Report a concise summary: PR URL, changed components, acceptance-criteria evidence, commands/results, sample execution, failure counts where applicable, limitations and follow-ups. Update durable docs with important discoveries.

## PR creation — Codex and Claude

These instructions apply to both agents. Use the tools available in the current session; do not assume a tool exists based on its name here.

- When available, prefer the direct GitHub connector operation `github_create_pull_request` (exposed in Codex as `mcp__codex_apps__github_create_pull_request`). Supply `repository_full_name`, `base`, `head`, `title`, `body`, and `draft`. This operation successfully created PR #6 without a separate submission form.
- Avoid choosing `github_mcp_create_pull_request` when the direct operation is available. In this environment it returned `awaiting_user_submission` and claimed to display a form that the user could not see; it did **not** create a PR.
- In Claude or another client where the direct operation is unavailable, use an available GitHub PR creation tool that performs the operation directly, or authenticated GitHub CLI: `gh pr create --repo OWNER/REPO --base BASE --head BRANCH --title TITLE --body-file PATH` (add `--draft` when appropriate). Write the description to a file with real newlines. Respect any actual permission or approval requirements of the client.
- If a tool returns a pending form or `awaiting_user_submission`, do not report success. If the user reports the form is missing, use an authorized direct operation or CLI fallback; do not repeatedly invoke the same invisible form. A genuine approval denial must not be bypassed.
- Before retrying an uncertain creation result, check for an existing PR for the head/base branches to avoid duplicates. Confirm creation from a returned PR URL/number or a GitHub lookup. Report the actual URL and draft/open state; keep the PR unmerged.
- Use a draft when required acceptance evidence is still missing, and explain the limitation in the description. An environment-blocked smoke test must not be presented as a successful live run.

## Safety
Never commit credentials, sensitive data, large downloaded datasets or employer IP. Do not modify unrelated code or perform destructive operations without approval. Keep generated data outside version control.
