# SportsModels.Basketball — Agent instructions

This repository holds code and documentation of its current behavior. GitHub issues are the source of truth for task specifications and design decisions. Read this file before making changes.

## Workflow
- Hierarchy: End Goal → Milestone → Epic → Feature → Task.
- GitHub issues track work. Do not implement a task unless the Project Manager has marked it **Ready for Implementation** (or the user has explicitly approved an equivalent complete specification).
- See `.agents/skills/project-manager/SKILL.md` for specification/readiness and `.agents/skills/implementation/SKILL.md` for execution.
- Read `docs/CODING_PRACTICES.md` before writing C#.
- Work on a dedicated branch per task, never directly on main. Commit changes, run relevant tests, and open a PR referencing the issue; user retains merge approval.
- Do not infer that an issue is complete from a PR alone. Provide evidence against acceptance criteria and report limitations.
- Ask before major unapproved domain or architecture choices; simple plumbing decisions may be delegated by the issue.
- No secrets, credentials, employer code/data or bulk downloaded NBA JSON in Git.

## Documentation ownership and maintenance
- GitHub issues own scope, design decisions and their rationale, unresolved questions, dependencies and readiness/approval. Keep the issue specification current; link to decision history instead of copying it into repository documents.
- Pull requests own implementation summaries, acceptance-criteria evidence, build/test results and delivery limitations. Do not create per-task evidence files or session handoff/context documents by default.
- Repository Markdown owns setup, usage, runnable examples and constraints of the implemented code. Maintain a small set of topic-based guides; link to issues for specification and decision history.
- Update the relevant guide in the same PR as a behavior/API change. Remove obsolete guidance, and consolidate overlapping documents rather than adding a new file for each task or session.
- Existing evidence files are historical records, not current specifications. Do not extend that pattern; migrate useful material when working on the relevant area.

## Current planning hierarchy
End goal: NBA in-play modelling and player-prop pricing.
Planned milestone: NBA Data Foundation (native GitHub milestone pending).
Epic: #4 NBA Data Acquisition.
Feature: #5 Official NBA Play-by-Play Ingestion.
Initial approved task: #2 Retrieve and locally store official NBA play-by-play JSON.

The issue specification takes precedence over older planning notes. Keep responses concise and decisions explicit.
