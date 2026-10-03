# scripts/board — kept from the old live board

The old local board was removed in ADR-0002 phase 0 (its last release is the tag `archive/board-final`). These two
files are kept at the maintainer's request (03/10/2026): the session nudge (T-23, commit 79454ee on `test` and
`prod`) and its tests, unchanged.

They do not run here: they import modules of the old board (`board_config`, `board_store`, `board_hook`) that no
longer exist in this branch, and the test sits outside `scripts/tests`, so the gate does not collect it. The live
old board on the maintainer's machine still runs them from `prod` until the cut-over (ADR-0002 phase 5).
