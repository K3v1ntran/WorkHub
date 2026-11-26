# WorkHub Phase 25 proposal: Workspace tree interactions

Status: **proposal, awaiting review** (Revision 2 + R2a). Nothing here is implemented. Baseline: `main` = `9ddbafc` (Phase 24 merged).
Branch: `codex/phase-25-workspace-interactions`. Use the tracked .NET 10 solution; real profiles stay out of scope. D1–D6 are open.

### Revision 1 → Revision 2

| # | Review point | Revision 2 change |
| --- | --- | --- |
| 1 | The drop must hold the write slot for the whole operation | §2.1 step 4 rewritten: `CanWrite()` only reads the state and `BeginWorkspaceWrite()` takes no slot; at the drop the conditions are re-checked and the slot is taken on the UI thread before the first `await`; `BeginWorkspaceWrite()` runs only for an accepted drop; the slot is held through `MoveAsync`, the apply or reconcile, and released in `finally`. DD8 split into DD8a–DD8e with gates (§4.1); risk added (§3). **R2a (follow-up review):** the write outcome of `MoveAsync` is kept apart from the tree update after it: a write that failed before commit → "Nothing was changed" (DD8e); a typed refusal keeps its text even if the reconcile then fails (DD8d); a committed move whose tree update fails → "Moved … The Workspace tree could not be updated…", never "Nothing was changed", no second `MoveAsync` (new DD8f); the slot rules are unchanged. |
| 2 | Define the row area and the background of the tree | New §2.3 hit-test contract: the row area is the item template's `Row` border (full width, including the space right of a short label), not "any ancestor `TreeViewItem`"; expander, scroll bar and background defined. New BG6 / BG7 and GUI GT25-7 (right of a short row in an expanded Folder), GT25-8 (expander), GT25-9 (scroll bar), GT25-10 / GT25-11 (row click while the Workspace Trash is open, also on the selected row). |
| 3 | Hover-expand bound to the Folder and the drag; DD4 with a visible target | §2.1 step 7: the timer carries the drag session and the target Folder **Id**; cancelled on leave, drop, drag cancel, search; re-checked on expiry (D5(a) only). New DH1–DH5 with a controllable timer. DD4 rewritten: the target is the Folder shown at drop time, window B moved it below the source in the database, A's pre-check allows, the store returns `MoveCycle`, nothing is written, and the target is reconciled (§2.1 step 5). |
| 4 | Esc must match D6 | §2.2 step 5 lists both D6 options, recommending to keep Esc unchanged; the required R2 cases are mouse-only; the Esc case is listed separately (BG-E1) for D6(a). D7–D11 are marked as questions for later phases, not conditions for Phase 25. |

## 0. Baseline and acceptance status carried over

- Phase 24 source milestone closed at `9ddbafc` (fast-forward of `codex/phase-24-tagged-projects`; commits `b676e93`, `a86e418`,
  `7a840ce`, `9ddbafc`).
- Phase 24 evidence (reported by the reviewer, on `9ddbafc`): the target group 51/51 passed in Debug and in Release; GUI GT24-1–4
  passed, including both Enter cases of GT24-3 and the right double-click of GT24-4.
- The full suite still has failures and they are recorded as they were measured: full Debug 2,745 passed / 24 failed / 6 skipped;
  full Release 2,746 passed / 23 failed / 6 skipped. 22 of the 23 common failures reproduce in both configurations on the baseline
  `c3d9f04`; `B16` timed out on baseline Debug but passed on baseline Release, so the Release difference is not attributed to
  Phase 24. The extra Debug failure (an icon case) passed when run alone against the executable of the same commit.
- Phase 15–23, D10 (Settings toolbar fix) and P3 remain tracked separately. Nothing is closed by the Phase 24 merge.

## 1. The four requests and what the code does today (at `9ddbafc`)

### R1 — drag and drop Projects and Folders in the tree

- There is no drag and drop anywhere in the app (no `AllowDrop` / `DragDrop` in `src`).
- Moving is the **Move to…** dialog: `WorkspaceAreaViewModel.MoveAsync` (`src/WorkHub.Presentation/ViewModels/WorkspaceAreaViewModel.cs:1125–1139`)
  lists destinations (`ListMoveTargetsAsync`), opens `WorkspaceMoveViewModel`, and applies a success with `ApplyMovedAsync`
  (`:3308–3350`); refusals are reconciled (`:1134–1138`).
- The store enforces every rule in one `BEGIN IMMEDIATE` transaction (`src/WorkHub.Infrastructure/Persistence/SqliteWorkspaceStore.cs:226–290+`):
  source exists, not a trashed / archived Project, effectively live; destination is an existing live Folder; no cycle (the node
  itself or a descendant → `WorkspaceMoveCycleException`); same parent → "already there" (`false`, nothing written); then the
  name-clash check. `WorkspaceService.MoveAsync` maps each to a typed `WorkspaceFailure` and a message
  (`src/WorkHub.Core/Workspace/WorkspaceService.cs:151–193`).
- Destinations offered by the dialog already exclude the moving node's subtree and non-live Folders (`SqliteWorkspaceStore.cs:199–224`).

### R2 — click on empty tree space clears the selection; right-click there creates at the top level

- The tree is a `TreeView` bound to `Roots` with one shared context menu `WorkspaceSelectionMenu`
  (`src/WorkHub.App/Views/MainWindow.xaml:80–104`, tree `:1062–1097`). The menu is also the tree's right-click menu on empty space
  (comment at `:97`).
- Right-click on a row selects it first (`MainWindow.xaml.cs:314–327`); right-click on empty space selects nothing, so the menu acts
  on the **previous** selection. Left-click on empty space does nothing either: `:329–344` only handles the Trash panel case (a
  click on a tree row while the Workspace Trash is open closes the Trash, also on the already selected row).
- Both handlers treat a point as "on a row" as soon as a `TreeViewItem` ancestor is found (`:317–326`, `:335–343`). Each item's
  template (`MainWindow.xaml:153–205`) is a `StackPanel` with the row `Border x:Name="Row"` (transparent background, stretched to the
  full width, `HorizontalContentAlignment="Stretch"`, hover highlight on `Row` only) holding the `ToggleButton x:Name="Expander"` and
  the header, followed by the `ItemsPresenter x:Name="ItemsHost"` of the children. So an ancestor `TreeViewItem` is also found for
  points inside a parent's `ItemsHost`, which are not that parent's row.
- New items go "into the selected Folder, beside the selected Project, or at the root" (`CreationParent`,
  `WorkspaceAreaViewModel.cs:946–951`). So today, after selecting a Folder, a right-click on empty space → New Folder creates
  **inside that Folder** — the reported problem.
- The view model already supports "no selection": `SelectedNode = null` is accepted (`:525–561`), and a node's `IsSelected = false`
  clears it (`OnSelectionChanged`, `:860–866`). Info closes when nothing is selected (`:560`).

### R3 — operation messages hide after 5 seconds

- The Workspace banner is `BannerMessage` (`WorkspaceAreaViewModel.cs:642–654`; view `MainWindow.xaml:830–857`), assigned in about 60 places
  in that file: progress ("Moving … to the Trash…"), results ("Moved … to the Trash."), refusals, errors and reconciliation notices.
- **The banner is the only access to Undo** for Move to Trash, Archive, Folder Archive / Trash and Bulk Archive (`OfferUndo`,
  `:2943–2948`; Undo button `MainWindow.xaml:848–850`; there is no Ctrl+Z binding). Replacing or dismissing the message withdraws the
  Undo (`BannerMessage` setter, `:649–650`). The Bulk Details button lives there too (`:851–853`, `IsBulkDetailsAvailable`).
- The selected Project's content has its own banner (`ProjectContentViewModel.BannerMessage`, `:261`; view `MainWindow.xaml:1658–1668`) and
  its own Undo strip (`UndoMessage` / `UndoCommand` / `DismissUndoCommand`, `ProjectContentViewModel.cs:137–138, 347–357`;
  view `MainWindow.xaml:1676–1680`). The Repository and Owner areas have their own banners (`MainWindow.xaml:515–528, 706–718`).

### R4 — show or hide each button of the left and right toolbars

- Today there are three whole-toolbar switches: `WorkspaceUiSettings.ShowTreeToolbar / ShowSelectionToolbar / ShowContentToolbar`
  (`src/WorkHub.Core/Settings/AppSettings.cs:237–262`), shown in Settings → Workspace (`src/WorkHub.App/Views/SettingsWindow.xaml:236–246`)
  and by right-click on each toolbar (`WorkspacePreferencesViewModel`, `src/WorkHub.Presentation/ViewModels/WorkspacePreferencesViewModel.cs`).
- Saves already go through the per-member merge: `SettingsEdit.Toolbars` (`ISettingsStore.cs:85–102`) is applied by
  `JsonSettingsStore.Update` to the document **as it is on disk** under the file lock, writing only the named members
  (`src/WorkHub.Infrastructure/Settings/JsonSettingsStore.cs:146–170`); the quick save is `SaveWorkspaceToolbarAsync`
  (`SettingsService.cs:131–132`); the dialog's UI-only save sends only changed members (`SettingsService.cs:114–122`,
  `SettingsViewModel.ChangedToolbars`, `SettingsViewModel.cs:190–195`).
- Left toolbar (tree, `MainWindow.xaml:870–912`): New Folder, New Project, Rename, Move to, Archive project, Move project to Trash,
  Archive folder, Move folder to Trash, Remove empty folder. Right toolbar (content, `:1542–1600`): Up one level, New folder, New link,
  New note, Open, Edit, Move, Copy to project, Move to project, Move to Trash, Check, Check all, Stop checking. (The selection strip,
  `:1390–1470`, is a third toolbar.)

## 2. Proposed scope

**Phase 25 = R1 + R2 (tree interactions).** R3 and R4 become later phases.

Why these two together: both live in the same `TreeView` code-behind (mouse handling, hit testing of rows vs. empty space) and in the
same view-model state (`SelectedNode`, `CreationParent`, `ApplyMovedAsync`). A drag needs the same "row or empty space?" hit test as
the empty-space click, and the root drop zone is the same empty space. Testing them together avoids re-testing the mouse layer twice.
Neither touches the database schema, settings or banners beyond the existing Move messages.

Why not R3 now: it changes a cross-cutting UI rule (four banners, ~60 message sites, Undo and Details) and needs its own decision on
Undo (§5 D7–D9); mixing it with new mouse gestures makes failures hard to attribute. Proposed **Phase 26**.

Why not R4 now: it is a settings-schema extension (new keys, defaults, Settings UI, migration of the three existing switches) with its
own regression risk (concurrent saves, Repository preservation). Proposed **Phase 27** (or 26 if R3 is deferred further).

### 2.1 R1 design: drag and drop = the existing Move, without the dialog

1. **Gesture.** Left-button drag of a Project or Folder **row area** (§2.3; not the expander) after the system drag threshold starts a
   drag session with a new session number. Only live tree rows are draggable (not placeholders, not while search results replace
   the tree, not while `CanWrite()` is false). The drag data carries the node **Id** only.
2. **Targets.** A **Folder row** = move into that Folder. The **root drop zone** = move to the top level: the tree's empty background
   below the rows plus a thin strip shown at the top of the tree during a drag ("Move to top level"), so a long tree still has a root
   target. A **Project row is not a target** (D2).
3. **Feedback before the drop (UI pre-check, never authoritative).** Using the loaded tree only: the source itself, any of its
   loaded descendants, and the current parent show "no drop" (`DragDropEffects.None`). Everything else shows "move".
4. **Drop and the write slot.** Facts at `9ddbafc`: `CanWrite()` only reads `IsActivated && !IsLifecycleBusy`
   (`WorkspaceAreaViewModel.cs:954`); `BeginWorkspaceWrite()` only cancels a reveal and withdraws the offered Undo
   (`:975–980`) and takes **no** slot; the slot is `IsLifecycleBusy` (`:432–442`), taken today by the lifecycle commands and by
   `TryBeginTagWrite` (`:1026–1032`). (Move to… itself does not take the slot; that is unchanged here.) The drop runs as one
   operation in a new `MoveByDropAsync(sourceId, targetId)`:
   1. **Synchronously on the UI thread, before any `await`:** resolve source and target by Id through `_nodes`; re-check
      `CanWrite()`, the pre-check of step 3, that the tree is not replaced by search results and that the session is the current
      one. Any failure = **rejected drop**: no store call, no `BeginWorkspaceWrite()`, banner and offered Undo unchanged.
   2. Still before the first `await`: take the slot (`IsLifecycleBusy = true`), then `BeginWorkspaceWrite()` (only now: an
      accepted drop is a Workspace write and withdraws Undo like every other write).
   3. `try`, in two phases whose outcomes are kept apart:
      - **Write phase:** `await WorkspaceService.MoveAsync(sourceId, targetId)`. The store does every check and the update in one
        `BEGIN IMMEDIATE` transaction and commits as its last step, so an exception out of this call means the move was **not
        committed** (rolled back). Three results:
        - *write failed (exception, not committed)* → logged without names; banner **"Could not move “X”. Nothing was changed."**;
          no tree update, no reconcile (DD8e);
        - *typed refusal* (`WorkspaceResult` with a `WorkspaceFailure`) → banner = the service's refusal text; then the
          reconciliation of step 5. If that reconcile itself fails, the refusal text **stays** the banner (it is the true outcome of
          the write; the reconcile failure is only logged, without names) (DD8d);
        - *committed* → `true` (moved) or `false` ("already there": nothing written, no banner, no tree update).
      - **Tree phase (only after a committed `true`):** `await ApplyMovedAsync`, which reports whether the tree was updated. A
        failed destination load (`EnsureChildrenLoadedAsync` returns false or throws) or any exception while applying = **tree
        update failed**. Then: the node is taken out of its stale place (as `ApplyMovedAsync` already does when the destination
        cannot be shown: `ForgetSubtree`, selection cleared); banner **"Moved “X” to “F”. The Workspace tree could not be updated."**
        (top level: **"Moved “X” to the top level. The Workspace tree could not be updated."**); never "Nothing was changed"; `MoveAsync` is **not**
        called again (DD8f). A destination that is simply not loaded (collapsed, never opened) is not a failure: the node is
        forgotten below it as today and the success banner of D3 applies.
   4. `finally`, on every branch above (rejected after the slot was taken, write failed, refusal with or without a failed
      reconcile, committed with or without a failed tree update): release the slot (`IsLifecycleBusy = false`) — only after the
      tree phase or the reconcile has finished.
   The store remains the only authority (a stale tree or another window cannot bypass a rule).
5. **Refusals and reconcile.** As Move to… (`:1134–1138`): `ProjectTrashed` / `ProjectArchived` / `NodeNotFound` /
   `AncestorNotLive` → `ReconcileNodeAsync(source)`; `ParentNotFound` / `DestinationNotLive` →
   `ReconcileNodeAsync(target, "The destination folder no longer exists. Nothing was moved.")`. New for drops: `MoveCycle` →
   banner "A folder cannot be moved into itself or into one of its own subfolders." and `ReconcileNodeAsync(target)`, because a drop
   that passed the pre-check can only meet a cycle when the loaded tree is stale (window B moved the target below the source); the
   fresh read then places the target where it is now (under the source if that branch is loaded, otherwise it leaves the stale
   place). `DuplicateName` → banner only.
6. **No confirmation dialog, no Undo** (D3, D4): Move to… has neither today.
7. **Hover-expand (only if D5(a) is approved).** One pending hover at a time, keyed by **(drag session, target Folder Id)**, never by a
   container (the tree recycles containers):
   - started when the pointer enters the row area of a collapsed, live, loaded Folder that is a valid target;
   - cancelled when the pointer leaves that Folder's row area or enters another target, on drop, on drag cancel or end, when search
     replaces the tree, and when the session ends;
   - on expiry (~0.8 s) it expands the Folder only if the session is still current, the pointer is still over the row area of the
     same Folder **Id**, the node is still in `_nodes`, still a live collapsed Folder and `CanWrite()` holds; otherwise nothing.
   The timer is injectable (a test clock) so expiry can be driven by the tests. The drop itself uses `ApplyMovedAsync`, which already
   loads and expands the destination.

### 2.2 R2 design: background click and top-level creation

1. **Left-click on the tree background** (§2.3) clears the selection: `SelectedNode = null` (Info closes, details clear, as today when
   nothing is selected). A left-click on a row area, an expander or the scroll bar never clears it.
2. **Right-click on the background** first clears the selection the same way, then opens the menu. So New Folder… / New Project…
   create at the top level (`CreationParent` returns null), and selection-bound items (Rename, Move, Archive, Trash, Info) are
   disabled. Right-click on a row keeps today's behaviour (select it, then the menu acts on it), so a Folder row is still the target.
3. The tree toolbar's New Folder / New Project keep the existing rule (selected Folder → inside; selected Project → beside; none → top
   level). The explicit "top level" action is: click the background, then create.
4. **The Workspace Trash case is unchanged:** a left-click on a row area while the Trash is open closes the Trash and shows the
   tree selection, also when the clicked row is already selected (`MainWindow.xaml.cs:329–344`); after Revision 2 it uses the §2.3
   row-area test instead of "any ancestor `TreeViewItem`". A background click while the Trash is open clears the selection and keeps
   the Trash open (nothing to show).
5. **Esc (D6, open).** (a) Esc in the tree clears the selection like a background click; (b) **recommended:** keep Esc as it is today
   (no new binding). The required R2 acceptance cases are mouse-only; the Esc case BG-E1 (§4.1) applies only if D6(a) is approved.

### 2.3 Hit-test contract: row area, expander, scroll bar, background

One helper in the `MainWindow` code-behind classifies a mouse point from `e.OriginalSource`, walking up the visual tree (logical
parent for non-visuals) and stopping at the **first** of these:

| First element met | Class | Node |
| --- | --- | --- |
| a `ScrollBar` (or a part of it) | **scroll bar** | — |
| the `ToggleButton` named `Expander` in a `TreeViewItem` template | **expander** | that item's node |
| the `Border` named `Row` whose templated parent is a `TreeViewItem` | **row area** | that item's node |
| the `WorkspaceTree` itself, without meeting any of the above | **background** | — |

Consequences: the row area is the full-width `Row` border, so the space **to the right of a short label** is still that row (it is
also where the hover highlight shows). A point inside a parent's `ItemsHost` that is not on a child row (none exists with the current
template, since `StackPanel`s without background are not hit-testable, but the rule must not depend on that) is **not** the
parent's row: an ancestor `TreeViewItem` met without first meeting its `Row` does not count. The area below the last row and any
space not covered by a row is background. Placeholder rows ("Loading…") count as row areas but select nothing (as today).
Used by: R2 left/right clicks, the Trash case, the drag start (row area only) and the drop target (Folder row area / root zone).

### 2.4 Out of scope (Phase 25)

Drag and drop of content items, of Archived / Trash panel rows, or between windows; dropping onto Projects; multi-select; drag from or
to search results; Undo for Move; taking the slot in the Move to… dialog; reordering (the tree stays sorted by `WorkspaceNodeOrder`);
keyboard moves; any schema, package, settings or version change; R3 and R4.

## 3. Risks

- **WPF drag vs. click/selection.** `TreeView` selects on mouse down; starting a drag must not change the selection twice or start a
  details load per hover. Mitigation: start the drag only after the threshold; keep the source row selected; hover never selects.
- **Virtualized tree (`VirtualizingPanel.IsVirtualizing="True"`, recycling).** Containers are reused; the drag data must carry the node
  **Id**, never a container. The drop resolves the target by Id through `_nodes`.
- **Stale tree / another window.** The UI pre-check uses loaded data only; the store decides. Refusals must leave the tree consistent
  (existing reconciliation).
- **Accidental moves.** No confirmation and no Undo: mitigate with the drag threshold, clear "no drop" feedback and a result banner
  naming the source and destination (D3/D4 may change this).
- **Background click hit testing** at the edges (expander, row padding, horizontal area right of a short row): covered by GUI cases.
- **Overlapping writes during a drop.** Without the slot, a second drop or a lifecycle command could start while the first
  `MoveAsync` or its apply is pending. Mitigation: §2.1 step 4 (slot taken before the first `await`, released in `finally`), DD8a–f.
- **A misleading result after a committed move.** If the tree update failed and the banner said "Nothing was changed", the user
  could repeat the move or distrust the data. Mitigation: §2.1 step 4.3 keeps the write outcome apart from the tree outcome; DD8f.
- **Undo withdrawn by a rejected gesture.** Calling `BeginWorkspaceWrite()` before the drop is accepted would clear an offered Undo
  for a drop that writes nothing. Mitigation: it runs only after acceptance (DD8b).
- **Wrong row from hit testing.** "Any ancestor `TreeViewItem`" mis-classifies points in a parent's children area. Mitigation:
  §2.3 contract, BG6–BG7, GT25-7–11.
- **Late hover timer.** A timer that outlives its target or session (recycled containers, fast pointer moves) could expand an
  unrelated Folder. Mitigation: Id + session keys, re-check on expiry, DH1–DH5 with a controllable clock.
- **Automation.** Real drag and drop is GUI-only; logic tests cover the view-model path and the hit-test helper's decision table.

## 4. Acceptance tests (design level; fake data; gates, no sleeps)

### 4.1 View model (`WorkspaceDragDropTests`, `WorkspaceBackgroundSelectionTests`)

| Case | Action | Expected |
| --- | --- | --- |
| DD1 | drop Project P (top level) on Folder F | one `MoveAsync(P, F)`; P under F, F expanded, P selected; banner none or result per D3 |
| DD2 | drop Folder A on the root zone (A in B) | `MoveAsync(A, null)`; A at the top level in tree order |
| DD3 | drop A on itself / on a loaded descendant | pre-check refuses; **no** store call |
| DD4 | Folders S and T both shown at the top level in window A's loaded tree; window B moves T **below S** in the database; A's tree is not updated; in A, drop S on the T row it still shows | A's pre-check **allows** (T is not a loaded descendant of S in A); exactly one `MoveAsync(S, T)`; the store returns `MoveCycle`; the data snapshot is unchanged (nothing written by the drop); banner "A folder cannot be moved into itself or into one of its own subfolders."; `ReconcileNodeAsync(T)` runs: T leaves A's top level and appears under S if S is loaded and expanded (otherwise it is forgotten below the collapsed S); S stays where it was; slot released |
| DD5 | drop P on its current parent | pre-check refuses (or service `false`): no write, no banner |
| DD6 | name clash at the destination | `DuplicateName` banner; nothing moved |
| DD7 | archived Project / Folder in an archived or trashed Folder as source; archived/trashed target (window B changed it) | typed refusal banner and the same reconciliation as Move to… |
| DD8a | Move to Trash of P2 offered Undo; `HoldLifecycleSlotForTests()`; drop P onto F | rejected: 0 `move` calls; banner text and Undo unchanged (`IsUndoAvailable` still true) |
| DD8b | `Hold("move")`; drop P onto F (held); then drop Q onto F | the store receives **exactly one** `move` call; the second drop is rejected before the store; `IsLifecycleBusy` true while held; the banner is the same before and after the rejected drop |
| DD8c | release DD8b's held move (success) | P under F; slot released (`IsLifecycleBusy` false, `CanWrite()` true) only after `ApplyMovedAsync` completed; a new drop is accepted |
| DD8d | as DD8b, the store refuses (`DuplicateName` / `DestinationNotLive` via window B); variant: the reconcile read that follows is held and failed (`Hold("nodeState")` / the children read, released with an `IOException`) | refusal banner (the service's text) — also in the variant, where the reconcile failure is only logged; nothing written; slot held until the reconcile ended, then released |
| DD8e | **write fails, not committed**: as DD8b, the held move is released with an `IOException` (the store call throws; nothing commits) | data snapshot unchanged (P's parent unchanged); exactly one `move` call; banner "Could not move “P”. Nothing was changed."; no tree update or reconcile; slot released |
| DD8f | **write commits, tree update fails**: F collapsed and not loaded; `Hold("children:F")` armed; drop P onto F (the `move` is **not** held: it commits); `WaitEntered` on the children read of `ApplyMovedAsync` | while held: the database already has P under F, `IsLifecycleBusy` is true (slot still held), no banner yet. Release the gate with an `IOException`: exactly one `move` call (no retry); the database still has P under F; banner "Moved “P” to “F”. The Workspace tree could not be updated." (never "Nothing was changed"); P is not shown at its old place; slot released (`IsLifecycleBusy` false, `CanWrite()` true). Gates only, no `Sleep` |
| DD9 | drop while the tree is replaced by search results | not draggable |
| BG1 | select Folder F, then background left-click | `SelectedNode` null; Info closed; no details load started |
| BG2 | select F, background right-click → New Folder… | the editor opens with parent = top level; the created folder is a root |
| BG3 | select F, right-click on F → New Folder… | parent = F (unchanged rule) |
| BG4 | select Project P (in F), background right-click → New Project… | parent = top level |
| BG5 | background right-click | Rename / Move / Archive / Trash / Info disabled; New Folder / New Project / Manage tags enabled |
| BG6 | hit-test decision table (helper over a realized test tree, or its pure classifier fed with element chains) | `ScrollBar` → scroll bar; `Expander` → expander of its item; `Row` → row of its item, also right of a short label; a parent `TreeViewItem` met without its `Row` → not that row; `WorkspaceTree` only → background |
| BG7 | expander click; scroll bar click | selection unchanged; the expander toggles only; nothing created or cleared |
| DH1 | D5(a): drag session 1 enters collapsed F1; the test clock fires | F1 expands |
| DH2 | enter F1, leave to F2 before expiry; fire F1's timer | F1 stays collapsed (cancelled); only F2's timer may expand F2 |
| DH3 | enter F1; drop / cancel the drag / search starts; fire | nothing expands |
| DH4 | enter F1 in session 1; end it; start session 2 over F2 whose row reuses F1's old container; fire session 1's timer | nothing expands (session and Id mismatch) |
| DH5 | enter F1; window B trashes F1 and A reconciles before expiry; fire | nothing expands (no longer a live Folder in `_nodes`) |
| BG-E1 | only if D6(a): select F, press Esc in the tree | `SelectedNode` null (not required while D6(b) is recommended) |

### 4.2 Contract (`Phase25ContractTests`)

No migration (LatestVersion 10), packages unchanged; the tree has `AllowDrop` and the four drag handlers; the drop path calls
`WorkspaceService.MoveAsync` and never a new store member; in `MoveByDropAsync` the slot is taken (`IsLifecycleBusy = true`) before
the first `await` and released in a `finally`, and `BeginWorkspaceWrite()` appears only after the slot is taken; the text "Nothing was
changed" occurs only on the write-failure branch, never after a committed move; the hit-test helper
names `Row`, `Expander` and `ScrollBar` (not "any ancestor `TreeViewItem`"); `IWorkspaceStore` unchanged; Move to… unchanged.

### 4.3 GUI (test profile only; isolated launcher, `--data-root`)

| Case | Action | Expected |
| --- | --- | --- |
| GT25-1 | drag P onto F; drag A onto the root strip | moved; tree and selection as DD1/DD2 |
| GT25-2 | drag A onto itself and onto its child | "no drop" cursor; nothing moved |
| GT25-3 | D5(a) only: drag over a collapsed folder and wait; then move quickly from one collapsed folder to another | the hovered folder expands; the first one does not |
| GT25-4 | select F, left-click below the rows (background) | selection cleared, Info closed |
| GT25-5 | select F, right-click below the rows → New Folder… | created at the top level |
| GT25-6 | right-click on F → New Folder… | created inside F |
| GT25-7 | expand F (with a short-named child "x"); select another node; left-click in the empty space to the right of "x"'s label, inside the tree | classified as **row area** of "x": "x" is selected (hover highlight covers the full width); the selection is not cleared |
| GT25-8 | select F; click the expander of another folder G | G toggles; F stays selected |
| GT25-9 | with a long tree, select F; click the vertical scroll bar (track and thumb) | the tree scrolls; F stays selected |
| GT25-10 | select F; open the Workspace Trash; left-click another row G | the Trash closes; G is selected |
| GT25-11 | select F; open the Workspace Trash; left-click F (already selected) | the Trash closes; F is shown |

## 5. Decisions awaiting approval (D1–D6 open)

- **D1 scope**: Phase 25 = R1 + R2; R3 → Phase 26; R4 → Phase 27. (Alternative: R2 alone first, R1 in 26.)
- **D2 dropping on a Project row**: (a) not a target (recommended); (b) treated as its parent Folder.
- **D3 confirmation / result message**: (a) no confirmation, result banner "Moved “X” to “F”." (recommended, consistent with
  Phase 26 auto-hide later); (b) no banner on success; (c) confirmation dialog.
- **D4 Undo for a drop**: (a) none, like Move to… (recommended for 25); (b) add Move Undo (new event model; would be its own phase).
- **D5 hover-expand** during a drag: (a) yes, ~0.8 s, keyed by drag session and Folder Id with the §2.1 step 7 rules and DH1–DH5
  (recommended); (b) no hover-expand (DH1–DH5 and GT25-3 drop out).
- **D6 Esc** in the tree: (a) Esc clears the selection (adds BG-E1); (b) keep Esc unchanged (recommended; R2 acceptance is
  mouse-only).

Questions for later phases (**not** conditions for approving Phase 25):

- For Phase 26 (R3): **D7** which banners auto-hide (proposal: results and progress-free success notices in
  the Workspace and content banners; errors / refusals that need reading stay until dismissed); **D8** a new message restarts its own
  5 s timer (each message owns a timer token; an older timer never hides a newer message); **D9** Undo: the banner that carries Undo
  (or Details) does **not** auto-hide while Undo is offered (recommended), or it hides after 5 s and Undo moves to a persistent place
  (Ctrl+Z / a menu item) — the second changes the Undo contract and needs its own tests.
- For Phase 27 (R4): **D10** which toolbars ("left" = tree toolbar, "right" = content toolbar; the selection strip too?);
  **D11** keys `ui.workspace.treeButtons.<id>` / `contentButtons.<id>` (missing = shown, so old files keep today's layout; the
  existing `show*Toolbar` switches stay and win when off); saves through `SettingsEdit` with per-key members so Repository and
  other windows' changes are kept (regression: concurrent window B save, unreadable file, Repository untouched).

## 6. Not changed by this proposal

Schema, migrations, packages, version, Phase 24 behaviour, the .NET 8 legacy variant. No release.
