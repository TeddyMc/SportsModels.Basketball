# SportsModels.Basketball — Agent instructions

This repository is the durable source of truth for code, task specifications and implementation decisions. Read this file before making changes.

## Workflow
- Hierarchy: End Goal → Milestone → Epic → Feature → Task.
- GitHub issues track work. Do not implement a task unless the Project Manager has marked it **Ready for Implementation** (or the user has explicitly approved an equivalent complete specification).
- See `.agents/skills/project-manager/SKILL.md` for specification/readiness and `.agents/skills/implementation/SKILL.md` for execution.
- Read `docs/CODING_PRACTICES.md` before writing C#.
- Work on a dedicated branch per task, never directly on main. Commit changes, run relevant tests, and open a PR referencing the issue; user retains merge approval.
- Do not infer that an issue is complete from a PR alone. Provide evidence against acceptance criteria and report limitations.
- Ask before major unapproved domain or architecture choices; simple plumbing decisions may be delegated by the issue.
- No secrets, credentials, employer code/data or bulk downloaded NBA JSON in Git.

## Current planning hierarchy
End goal: NBA in-play modelling and player-prop pricing.
Planned milestone: NBA Data Foundation (native GitHub milestone pending).
Epic: #4 NBA Data Acquisition.
Feature: #5 Official NBA Play-by-Play Ingestion.
Initial approved task: #2 Retrieve and locally store official NBA play-by-play JSON.

The issue specification takes precedence over older planning notes. Keep responses concise and decisions explicit.
