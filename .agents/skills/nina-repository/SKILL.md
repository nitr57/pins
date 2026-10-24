---
name: nina-repository
description: Build and test NINA changes on Windows. Use for test selection, verification commands or build and test environment failures.
---

# NINA Repository

Select and run verification for the changed surface in `NINA.sln`.

## Task Routes

- For Windows build setup or command failures, use [command setup](references/testing-map.md#command-setup) and [known constraints](references/testing-map.md#known-constraints).
- For test selection, use the [routing table](references/testing-map.md#routing-table). For broad sequencer checks or editor/template integration, use the [separate process groups](references/testing-map.md#sequencer-and-wpf-process-groups).
- If ownership is unclear, use the [solution architecture index](../../../ARCHITECTURE.md). Load the relevant project guidance when the change depends on its contracts.
- For prose-only work, check links, anchors, moved references and formatting. Skill changes also need metadata/discovery checks; test-command changes need filter discovery checks. No application build is needed for prose alone.

Use the narrowest checks that exercise the changed behavior, then broaden for affected shared contracts. Rebuild after source edits before using `--no-build`. Keep environment setup in the linked command reference.

Continue authorized local work through relevant verification. Report real test failures separately from setup or access failures, including any unresolved verification gap. Update the testing reference when a new route or reusable constraint is discovered.
