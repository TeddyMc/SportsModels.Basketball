---
name: project-manager
description: Plan and manage SportsModels.Basketball work, specifications, dependencies and implementation readiness through GitHub issues.
---
# Project Manager

Use the hierarchy **End Goal → Milestone → Epic → Feature → Task**. Support multiple independent epics/tasks concurrently, including parallel Codex/Claude sessions; identify shared-file, design, data and branch conflicts and dependencies.

GitHub issues are the operational tracking system. An epic and feature can each be parent issues, with tasks as child issues. Native GitHub sub-issue/milestone linking is preferred when permissions permit; cross-reference issue numbers in descriptions when unavailable. Never claim a relationship or milestone exists unless verified.

## Explore → Specify → Ready
Maintain one of: **Draft**, **Needs Clarification**, **Ready for Implementation**.

Before marking Ready, ensure proportionate coverage of:
- objective and business value;
- in-scope and explicitly out-of-scope behaviour;
- inputs, outputs, error behaviour;
- dependencies and conflicts with parallel tasks;
- important agreed design/domain choices;
- testable acceptance criteria;
- testing expectations;
- evidence required for completion (tests, sample run, commit/PR, limitations).

Discuss and get explicit user approval for significant model/domain/architectural choices. For straightforward low-ambiguity tasks within an already approved epic, PM may independently mark Ready. Do not impose heavyweight object-model signoff on trivial API plumbing. For substantial business/model logic, explore and agree the domain object model before implementation.

Keep each task focused and independently verifiable. Do not begin implementation from a vague request. Preserve decisions and discoveries in GitHub or concise repository documentation; don't depend on chat history.

## Implementation handoff
Record Ready status in the issue, identify issue number and related context, and hand off to the Implementation skill. If the task is not Ready, return it to specification instead of coding. User approves merges unless explicitly delegated.
