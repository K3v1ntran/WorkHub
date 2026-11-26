# WorkHub Phase 25 proposal: Workspace tree interactions

Status: **proposal, awaiting review** (Revision 1). Nothing here is implemented. Baseline: `main` = `9ddbafc` (Phase 24 merged).
Branch: `codex/phase-25-workspace-interactions`. Use the tracked .NET 10 solution; real profiles stay out of scope.

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
  on the **previous** selection. Left-click on empty space does nothing either (`:329–344` only handles the Trash panel case).
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

1. **Gesture.** Left-button drag of a Project or Folder row (after the system drag threshold) starts a drag. Only live tree rows are
   draggable (not placeholders, not while search results replace the tree, not while a lifecycle write holds the slot,
   `CanWrite()`).
2. **Targets.** A **Folder row** = move into that Folder. The **root drop zone** = move to the top level: the tree's empty background
   below the rows plus a thin strip shown at the top of the tree during a drag ("Move to top level"), so a long tree still has a root
   target. A **Project row is not a target** (D2).
3. **Feedback before the drop (UI pre-check, never authoritative).** Using the loaded tree only: the source itself, any of its
   loaded descendants, and the current parent show "no drop" (`DragDropEffects.None`). Everything else shows "move".
4. **Drop = `WorkspaceService.MoveAsync(nodeId, targetId)` directly**, through a new `MoveByDropAsync(node, targetId)` in the area
   view model that shares the post-move path with the dialog: `BeginWorkspaceWrite()`; call the service; success → `ApplyMovedAsync`;
   `false` ("already there") → nothing; refusal → banner text from the service (`MoveCycle`, `DuplicateName`, `ProjectArchived`,
   `ProjectTrashed`, `DestinationNotLive`, …) and the same reconciliation as `MoveAsync` (`:1134–1138`). The store remains the only
   authority (a stale tree or another window cannot bypass a rule).
5. **No confirmation dialog, no Undo** (D3, D4): Move to… has neither today.
6. **Expanded folders.** Hovering a collapsed Folder for ~0.8 s during a drag expands it (D5); the drop itself uses `ApplyMovedAsync`,
   which already loads and expands the destination.

### 2.2 R2 design: background click and top-level creation

1. **Left-click on the tree background** (not on a row, its expander or the scroll bar) clears the selection: `SelectedNode = null`
   (Info closes, details clear, as today when nothing is selected). Esc in the tree does the same (D6).
2. **Right-click on the background** first clears the selection the same way, then opens the menu. So New Folder… / New Project…
   create at the top level (`CreationParent` returns null), and selection-bound items (Rename, Move, Archive, Trash, Info) are
   disabled. Right-click on a row keeps today's behaviour (select it, then the menu acts on it), so a Folder row is still the target.
3. The tree toolbar's New Folder / New Project keep the existing rule (selected Folder → inside; selected Project → beside; none → top
   level). The explicit "top level" action is: click the background, then create.
4. **Hit test.** One helper in the code-behind decides row / expander / background / scroll bar from `e.OriginalSource` (walk up to a
   `TreeViewItem`, or to the `ScrollBar`), used by R1 and R2.

### 2.3 Out of scope (Phase 25)

Drag and drop of content items, of Archived / Trash panel rows, or between windows; dropping onto Projects; multi-select; drag from or
to search results; Undo for Move; reordering (the tree stays sorted by `WorkspaceNodeOrder`); any schema, package, settings or version
change; R3 and R4.

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
- **Automation.** Real drag and drop is GUI-only; logic tests cover the view-model path.

## 4. Acceptance tests (design level; fake data; gates, no sleeps)

### 4.1 View model (`WorkspaceDragDropTests`, `WorkspaceBackgroundSelectionTests`)

| Case | Action | Expected |
| --- | --- | --- |
| DD1 | drop Project P (top level) on Folder F | one `MoveAsync(P, F)`; P under F, F expanded, P selected; banner none or result per D3 |
| DD2 | drop Folder A on the root zone (A in B) | `MoveAsync(A, null)`; A at the top level in tree order |
| DD3 | drop A on itself / on a loaded descendant | pre-check refuses; **no** store call |
| DD4 | drop A on a descendant the tree has not loaded (or after window B moved things) | store refuses with `MoveCycle`; banner "A folder cannot be moved into itself…"; tree unchanged |
| DD5 | drop P on its current parent | pre-check refuses (or service `false`): no write, no banner |
| DD6 | name clash at the destination | `DuplicateName` banner; nothing moved |
| DD7 | archived Project / Folder in an archived or trashed Folder as source; archived/trashed target (window B changed it) | typed refusal banner and the same reconciliation as Move to… |
| DD8 | drop while a lifecycle write holds the slot | refused before the store; no call |
| DD9 | drop while the tree is replaced by search results | not draggable |
| BG1 | select Folder F, then background left-click | `SelectedNode` null; Info closed; no details load started |
| BG2 | select F, background right-click → New Folder… | the editor opens with parent = top level; the created folder is a root |
| BG3 | select F, right-click on F → New Folder… | parent = F (unchanged rule) |
| BG4 | select Project P (in F), background right-click → New Project… | parent = top level |
| BG5 | background right-click | Rename / Move / Archive / Trash / Info disabled; New Folder / New Project / Manage tags enabled |

### 4.2 Contract (`Phase25ContractTests`)

No migration (LatestVersion 10), packages unchanged; the tree has `AllowDrop` and the four drag handlers; the drop path calls
`WorkspaceService.MoveAsync` and never a new store member; `IWorkspaceStore` unchanged; Move to… unchanged.

### 4.3 GUI (test profile only; isolated launcher, `--data-root`)

| Case | Action | Expected |
| --- | --- | --- |
| GT25-1 | drag P onto F; drag A onto the root strip | moved; tree and selection as DD1/DD2 |
| GT25-2 | drag A onto itself and onto its child | "no drop" cursor; nothing moved |
| GT25-3 | drag over a collapsed folder and wait | it expands (D5) |
| GT25-4 | select F, left-click below the rows | selection cleared, Info closed |
| GT25-5 | select F, right-click below the rows → New Folder… | created at the top level |
| GT25-6 | right-click on F → New Folder… | created inside F |

## 5. Decisions awaiting approval

- **D1 scope**: Phase 25 = R1 + R2; R3 → Phase 26; R4 → Phase 27. (Alternative: R2 alone first, R1 in 26.)
- **D2 dropping on a Project row**: (a) not a target (recommended); (b) treated as its parent Folder.
- **D3 confirmation / result message**: (a) no confirmation, result banner "Moved “X” to “F”." (recommended, consistent with
  Phase 26 auto-hide later); (b) no banner on success; (c) confirmation dialog.
- **D4 Undo for a drop**: (a) none, like Move to… (recommended for 25); (b) add Move Undo (new event model; would be its own phase).
- **D5 hover-expand** during a drag: (a) yes, ~0.8 s (recommended); (b) no.
- **D6 Esc clears the selection** in the tree: (a) yes; (b) no (recommended to keep 25 minimal: no).
- For Phase 26 (R3), to settle now or then: **D7** which banners auto-hide (proposal: results and progress-free success notices in
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
