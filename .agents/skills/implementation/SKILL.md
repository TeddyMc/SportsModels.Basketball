---
name: implementation
description: Execute an approved SportsModels.Basketball GitHub task in a focused C# branch with tests, review and a pull request.
---
# Implementation

1. Read AGENTS.md, docs/CODING_PRACTICES.md and the GitHub issue, including comments and parent context.
2. **Readiness gate:** Verify the Project Manager recorded **Ready for Implementation** and the specification has scope, inputs/outputs, dependencies, acceptance criteria and testing. If PM marked Ready, do not demand redundant approval. If readiness is missing or the issue is materially underspecified, consult PM/user and stop rather than improvise.
3. Check parallel tasks, shared files, branches and dependencies. Create a dedicated branch named e.g. `task/2-nba-play-by-play-json` from main.
4. Implement only agreed scope, following existing architecture. Choose reasonable low-level implementation details delegated by the issue. Escalate substantial domain/design uncertainty.
5. Add fast NUnit unit tests and run `dotnet build` and `dotnet test` where applicable. Execute an end-to-end smoke test if feasible. Never claim a test ran if it did not; report environmental blockers.
6. Review sequentially for functional correctness/QA, material performance issues, and architecture/readability. Fix issues and rerun tests.
7. Commit, push the task branch and open a **pull request** referencing the issue (e.g. `Closes #2`). Do not merge without user approval.
8. Report a concise summary: PR URL, changed components, acceptance-criteria evidence, commands/results, sample execution, failure counts where applicable, limitations and follow-ups. Update durable docs with important discoveries.

## Safety
Never commit credentials, sensitive data, large downloaded datasets or employer IP. Do not modify unrelated code or perform destructive operations without approval. Keep generated data outside version control.
