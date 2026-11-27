# WorkHub Phase 26 proposal: operation messages hide after 5 seconds

Revision 2 (approved). Baseline: `main` = `0f42125` (Phase 25 merged); Revision 1 at `91d8380`, Revision 2 at `ed6cb2c`. Branch
`codex/phase-26-notification-auto-dismiss`. Request R3 of the Phase 25 proposal (§1 R3, §5 D7–D9). D7–D9 and D12–D16 are approved as
recommended (§6). Implemented on this branch after `ed6cb2c`; verification pending (§8).

### Revision 1 → Revision 2

| # | Review point | Change |
| --- | --- | --- |
| 1 | A message turn alone cannot tell an old timer from a re-armed one for the same message | §3.2 separates the **message turn** (one per shown message) from the **timer arm** (one per scheduling, also for the same message: re-shown after a tab switch, after hover / focus leave). Cancelling invalidates the arm. The callback clears only when its arm and turn are both current, the banner is visible and neither the pointer nor keyboard focus is inside it. AD6 and AD16 extended: the old callback is fired on purpose after the new arm exists; AD16b adds pointer out / focus still on Dismiss; GUI GT26-6b. TG26-C3 updated, TG26-C6 added (view hooks); risks added (§4). |
| 2 | Undo success must come from a typed result, not from `notice` text or `placed` | New §3.3: `RestoreCoreAsync` and `ClearFolderCoreAsync` return a typed outcome next to `Notice` (display) and `Placed` (tree / selection); `UndoCoreAsync` / `UndoFolderCoreAsync` choose `AutoDismiss` only for the success outcomes listed in §2. Panel Restore / Unarchive keep their current behaviour. §2 rows made exact (the "restored but archived" success is a Notice and stays). AD15 kept; AD15a–AD15d added (Project Trash Undo and Folder Undo, success and refusal) and AD15e (panels unchanged); GUI GT26-11, GT26-12. TG26-C5 added. |
| 3 | GT26-3 must produce a refusal banner for certain | GT26-3 now uses a drop that passes the UI pre-check but is refused by the store for a duplicate name, with a reproducible fixture; the refusal is created within 3 s of a Move result and checked after the old Move timer would have expired. |

## 0. Baseline and acceptance status carried over

- Phase 25 source milestone closed at `0f42125` (fast-forward of `codex/phase-25-workspace-interactions`; commits `21aee3e`,
  `6a3cec7`, `5ad2ecc`, `6538a40`, `2c952ae`, `3e3719a`, `e4a9d2a`, `0f42125`).
- Phase 25 evidence (Windows local, on `0f42125`):
  - Phase 25 groups (`WorkspaceDragDropTests`, `WorkspaceBackgroundSelectionTests`, `Phase25ContractTests`): 39/39 passed in Debug and
    in Release, including TG25-C2 and TG25-C3.
  - GUI GT25-1–11: 11/11 passed on a test profile (`--data-root`, fake data); the system cursor was driven with `SendInput`.
  - Full Debug: 2,785 passed / 23 failed / 6 skipped. Full Release: 2,784 passed / 24 failed / 6 skipped. The full suite still fails.
  - 23 of the failures, B16 included, reproduce by name in Debug and in Release on the Phase 24 baseline `9ddbafc`.
  - O2 (`RestoreStartupTests.O2_PublishedInitFailure…`) failed in full Release with `UnauthorizedAccessException`; it passed on the
    baseline and in six separate runs on `0f42125`. Cause unknown; no evidence attributes it to Phase 25. The full Release figures stay
    as measured.
- Phase 15–23, D10 (Settings toolbar fix) and P3 remain tracked separately. Nothing is closed by the Phase 25 merge.

## 1. What the code does today (at `0f42125`)

Message surfaces in the main window, their owners and what they carry:

| Surface | Owner (view model) | View | Extra actions in it | Dismiss |
| --- | --- | --- | --- | --- |
| Workspace banner | `WorkspaceAreaViewModel.BannerMessage` (`WorkspaceAreaViewModel.cs:649–660`); 59 assignments there + 4 in `.DragDrop.cs` | `MainWindow.xaml:833–857` (`WorkspaceBanner`) | **Undo** (`UndoCommand`, visible while `IsUndoAvailable`) and **Details** (`ShowBulkArchiveDetailsCommand`, visible while `IsBulkDetailsAvailable`) | "Dismiss workspace message" → `BannerMessage = null` (`:220`) |
| Content banner | `ProjectContentViewModel.BannerMessage` (`:261`); 22 assignments | `MainWindow.xaml:1670–1685` (`ContentBanner`) | none | "Dismiss content message" (`:134`) |
| Content Trash notice (Undo strip) | `ProjectContentViewModel.UndoMessage` / `HasUndo` (`:348–350`) | `MainWindow.xaml:1687–1696` | **Undo move to Trash** | "Dismiss trash notice" → `ClearUndo` (`:138`) |
| Content Destination notice | `ProjectContentViewModel` destination (`SetDestination`, `:1049`) | `MainWindow.xaml:1698–1707` | **Open destination** | "Dismiss destination notice" |
| Owner banner | `OwnerAreaViewModel.ShowBanner` (`:565–572`) | `MainWindow.xaml:515–531` | **Edit** launcher (`EditFromBannerCommand`), target text | "Dismiss owner message" |
| Repository banner | `RepositoryAreaViewModel.ShowBanner` (`:366–372`) | `MainWindow.xaml:706–718` | **Fix in Settings** (`FixSettingsCommand`) | "Dismiss" |

Facts that constrain auto-hide:

- **The Workspace banner is the only way to reach Undo** for Move to Trash, Archive, Folder Archive / Trash and Bulk Archive
  (`OfferUndo`, `:2949–2954`; no Ctrl+Z, no menu item). The `BannerMessage` setter withdraws the Undo as soon as the text changes
  or is cleared (`:656–657`). Hiding a message that carries Undo **removes the only access to that Undo**.
- **Details** of a Bulk Archive / Bulk Unarchive (Phase 19) are reachable only while the banner shows that summary
  (`IsBulkDetailsAvailable`, `:663`). Hiding the summary removes the only access to Details.
- The content Trash notice is the only access to the content Undo; the Destination notice is the only access to Open destination.
- Leaving the Workspace tab ends the visit and withdraws the Workspace Undo (`EndVisit`, `:2966–2970`) but leaves the text.
- Setting the same text again raises no change (`SetProperty`), so "the same message again" is invisible today.
- Phase 25 added an injectable clock (`IDragHoverClock` / `TaskDelayHoverClock`, `WorkspaceTreeHitTest.cs:84–124`) with a hand-fired
  test clock — the pattern to reuse.

## 2. Classification of the current messages

Kinds: **Result** (an operation finished as asked), **Error / refusal** (nothing or not everything was done, or the user must act),
**Progress** (an operation is running), **Notice** (explains a state the user did not cause directly, e.g. why the selection is under
Archived), **Actionable** (the message carries Undo, Details, Edit, Fix or Open destination).

| Message (examples, file:line) | Kind | Proposed |
| --- | --- | --- |
| Drop: "Moved “X” to “F”." / "Moved “X” to the top level." (`.DragDrop.cs:268`, tree shown) | Result | **auto-hide** |
| Drop: "Moved “X” to “F”. The Workspace tree could not be updated." (`.DragDrop.cs:268`, tree not shown) | Result + partial failure | keep |
| "Removed the empty folder “X”." (`:2139`) | Result | **auto-hide** |
| Undo of Archive: "Restored “X” to …" (`UndoArchiveCoreAsync`, `:1452`, typed `Restored` branch) — the Undo is consumed, none is offered | Result | **auto-hide** |
| Undo of Project Trash, outcome `ProjectRestoreOutcome.Restored` of a not-archived Project: "Restored “X” to …" (`RestoreCoreAsync`, `:1315–1319`, shown by `UndoCoreAsync`, `:1251`) | Result | **auto-hide** (§3.3) |
| Undo of Project Trash, `Restored` of a Project archived by an earlier build: "Restored “X”. It is archived: find it under Archived." (`:1308–1314`) | Notice (tells where to find it) | keep |
| Undo of Project Trash, any other outcome (`NameConflict`, `ParentArchived`, `ParentTrashed`, `NotTrashed`, missing, store exception) (`:1293–1342`) | Refusal / notice / error | keep |
| Undo of Folder Archive / Trash, outcome `FolderClearOutcome.Cleared`: "Unarchived / Restored folder “X” to …" (`ClearFolderCoreAsync`, `:2354–2360`, shown by `UndoFolderCoreAsync`, `:2320`) | Result | **auto-hide** (§3.3) |
| Undo of Folder, any other outcome (`NotArchived` / `NotTrashed`, `MarkedAgain`, `ParentArchived`, `ParentTrashed`, `NameConflict`, missing, store exception) (`:2337–2389`) | Refusal / notice / error | keep |
| Restore / Unarchive from the Workspace Trash or Archived panel (`RestoreFromTrashAsync`, `:1264–1288`, and the Archived panel's Unarchive through `ClearFolderCoreAsync`, `:1789`) — the text goes to the **panel**, not the banner | panel notice | unchanged (out of scope) |
| Content: "Restored to {path}." (`ProjectContentViewModel.cs:935`) | Result | **auto-hide** |
| "Moved “X” to the Trash." (`:1198`), "Archived “X”." (`:1389`), "Archived folder / Moved folder … to the Trash." (`:2260`) — Undo offered | Result + Undo | keep while Undo is offered (D9) |
| Bulk Archive summary (`:1633`, Details + Undo), Bulk Undo summary (`:1769`, Details) | Result + Details | keep (D12) |
| "Moving … to the Trash…", "Archiving …", "Restoring …", "Archiving N projects…", "Archiving folder …" (`:1178`, `:1369`, `:1247`, `:1431`, `:1554`, `:1698`, `:2227`, `:2316`) | Progress | keep (replaced by the result) |
| "Could not … Nothing was changed.", `ReportError` texts, typed refusals ("This project is archived. Unarchive it first.", duplicate name, cycle, "No projects to archive here.", "… changed since you confirmed. Review it again.") | Error / refusal | keep |
| Reconcile / reveal notices ("The project is archived — it is selected under Archived.", "This folder no longer exists.", "This item's folder is in the Trash.", `:1016`, `:2490`, `:3049`) | Notice | keep (D16) |
| Settings / preferences save failure via the Workspace banner (`:165`) | Error | keep |
| Content errors and refusals (`:632`, `:717`, `:726`, `:821`, `:831`, `:850`, `:873`, `:876`, `:912`, `:925`, `:1006`, `:1028`, `:1053`, `:1228`, `:1270`, `:1423`, `:1534`) | Error / refusal / notice | keep |
| Content Trash notice (Undo), Destination notice (Open destination) | Actionable | keep (unchanged) |
| Owner banner, Repository banner (all failures to open / missing items, with Edit / Fix in Settings) | Error + action | keep (unchanged, D15) |

Result: the auto-hidden set is small and explicit — successful results that carry **no** Undo, Details or follow-up action. Every
other message behaves exactly as today. Out of scope: the status bar `StatusText`, search notices, Trash / Archived panel notices,
load errors, dialogs' own messages (Manage tags, editors, Bulk dialogs) and the Restore screens.

## 3. Proposed scope

### 3.1 Opt-in kind, never inferred from text

- Each owner (`WorkspaceAreaViewModel`, `ProjectContentViewModel`) gets one entry point, e.g. `ShowBanner(string? text, BannerKind kind)`
  with `BannerKind { Persistent, AutoDismiss }`. **Persistent is the default**; the existing assignments keep their behaviour without
  change. Only the call sites listed as "auto-hide" in §2 pass `AutoDismiss`, and only on their success branch.
- The kind is decided at the call site, never by matching the text.
- A message that carries Undo or Details is never `AutoDismiss` (guard in the entry point: if Undo is offered or the Details summary is
  the message, the kind is forced to Persistent — D9(a), D12). `OfferUndo` after an `AutoDismiss` message cancels its timer.

### 3.2 The 5-second rule

Two counters per owner (Workspace banner, content banner):

- **Message turn** (`_bannerTurn`): incremented by every `ShowBanner` call, any kind, **including the same text again**, and by Dismiss.
  It identifies the message the user sees.
- **Timer arm** (`_bannerArm`): incremented every time a 5-second timer is scheduled **and** every time a pending timer is cancelled.
  One message can be armed several times (shown again after a tab switch, after the pointer and focus leave the banner); each arm
  gets a new number, so a callback of an earlier arm of the **same** message is recognisably stale.

Rules:

1. **Arm.** A timer is armed only when all hold: the current message is `AutoDismiss`; its surface is visible (Workspace tab selected for
   both banners; the content banner also needs its Project shown) (D13); the pointer is not over the banner and keyboard focus is not
   inside it (D14). Arming = cancel any pending timer, `arm = ++_bannerArm`, `Schedule(AutoDismissDelay, () => OnExpired(turn, arm))`
   with the current turn. `AutoDismissDelay = 5 s` (one constant).
2. **Cancel.** Disposing the pending timer **and** incrementing `_bannerArm`, so the cancelled arm's number is no longer current even
   if the clock still runs its callback (the hand-fired test clock always does).
3. **Expiry.** `OnExpired(turn, arm)` clears the message (exactly as Dismiss) only if: `turn == _bannerTurn` **and** `arm == _bannerArm`
   **and** the banner is visible **and** the pointer is outside it **and** keyboard focus is outside it **and** the owner is not
   deactivated. Otherwise it does nothing. Clearing an `AutoDismiss` message never touches Undo (it has none).
4. **New message.** `ShowBanner` increments the turn and cancels (rule 2); if the new message is `AutoDismiss` and rule 1 holds, it is
   armed with its own full 5 s (D8). A Persistent message is never armed.
5. **Manual dismiss.** Dismiss clears at once, increments the turn and cancels (behaviour otherwise unchanged).
6. **Attention (D14).** Pointer enters or focus enters the banner → cancel (rule 2). The view reports both states; when the pointer
   **and** focus are both outside again, the message is re-armed with a fresh 5 s (rule 1). Pointer out while focus stays (for example
   on the Dismiss button after Tab) → still not armed; it is armed only when focus leaves too.
7. **Visibility (D13).** Surface hidden (leaving the Workspace tab, another Project selected for the content banner, content view
   deactivated, a Retry / visit end) → cancel; the text stays as today. Shown again with the same `AutoDismiss` message → re-armed with
   a fresh 5 s. Search activation and the Trash / Archived panels do not hide the Workspace banner, so they change nothing.
8. **Undo / Details guard.** `OfferUndo` and a Details summary make the message Persistent (§3.1) and cancel (rule 2).
9. **Window close / shutdown.** Closing the main window (or app shutdown) marks the owners deactivated and cancels; a callback that still
   arrives fails rule 3. No timer keeps the process alive.
10. **Clock.** An injectable `IBannerClock` (same contract as `IDragHoverClock`: `Schedule(delay, callback) → IDisposable`, the callback
    posted to the UI context; production built on the same `Task.Delay` pattern). Tests use a hand-fired clock that keeps every
    scheduled callback so stale ones can be fired on purpose; no sleeps.
11. **View hooks.** The window reports, per banner: visible / hidden, pointer over (`IsMouseOver` of the banner border) and keyboard
    focus within (`IsKeyboardFocusWithin`). The view model owns all timing; the view never clears a banner itself.

### 3.3 Undo results choose `AutoDismiss` from a typed outcome

At `91d8380` / `0f42125`:

- `UndoCoreAsync` (`:1239–1260`) awaits `RestoreCoreAsync(nodeId, name)` (`:1291–1342`), which returns
  `(string Notice, WorkspaceNodeViewModel? Placed)`. Its switch on `ProjectRestoreOutcome` builds the notice: `Restored` (two texts:
  placed in the tree, or archived by an earlier build), `NameConflict`, `ParentArchived`, `ParentTrashed`, `NotTrashed`, default
  (missing); a store exception returns "Could not restore … It is still in the Trash.".
- `UndoFolderCoreAsync` (`:2307–2328`) awaits `ClearFolderCoreAsync(nodeId, name, kind, lifecycleEvent)` (`:2335–2389`), same tuple;
  switch on `FolderClearOutcome`: `Cleared`, `NotArchived` / `NotTrashed`, `MarkedAgain`, `ParentArchived`, `ParentTrashed`,
  `NameConflict`, default (missing); exception → "Could not unarchive / restore …".
- Both helpers also serve the panels (`RestoreFromTrashAsync`, `:1264–1288`; the Archived panel's Unarchive, `:1789`), which use only `Notice`.
- Today the callers set `BannerMessage = notice` and select `placed` when it is not null. `Placed` cannot tell success: it is null for
  the "restored but archived" success and when the restored node's parent is not loaded. The text cannot tell success either.

Proposed:

- Both helpers return a third, typed element: `RestoreResultKind { Restored, RestoredArchived, Refused, AlreadyCleared, Missing,
  Failed }` (`RestoreCoreAsync`: `Restored` only for `ProjectRestoreOutcome.Restored` of a not-archived Project; `RestoredArchived`
  for the archived case; `ClearFolderCoreAsync`: `Restored` only for `FolderClearOutcome.Cleared`; `MarkedAgain`, the parent and name
  outcomes → `Refused`; `NotArchived` / `NotTrashed` → `AlreadyCleared`; default → `Missing`; exception → `Failed`). The kind is set in
  the same switch arm that sets the notice.
- `Notice` stays the display text; `Placed` stays for the tree and the selection rule (unchanged: selected only while the visit and the
  selection version captured at the click are current).
- `UndoCoreAsync` / `UndoFolderCoreAsync`: `ShowBanner(notice, kind == RestoreResultKind.Restored ? AutoDismiss : Persistent)`.
  `UndoArchiveCoreAsync` already switches on its typed result; it passes `AutoDismiss` only in its `Restored` arm (`:1452`).
- Nothing compares `notice` strings and nothing derives success from `placed != null` (TG26-C5).
- Panel Restore / Unarchive keep their current behaviour: they read `Notice` only and never touch the banner timer.

### 3.4 Not in Phase 26

- Toolbar button customisation (R4) stays in **Phase 27** (D10/D11 of the Phase 25 proposal).
- No new Undo path (no Ctrl+Z, no menu) unless D9(b) is chosen — that would change the Undo contract and needs its own tests.
- No change to Owner / Repository banners, the content Trash and Destination notices, schema, migrations, packages, version.

## 4. Risks

- **Losing Undo / Details.** Mitigation: Undo- or Details-bearing messages are forced Persistent (§3.1); tests AD9–AD11.
- **A late timer hides a newer message** (e.g. an error that replaced a result). Mitigation: message turn (§3.2 rules 3–5); AD4, AD5.
- **A stale arm of the same message hides it early** (timer from before a tab switch or a hover, while a fresh 5 s is running).
  Mitigation: a new arm number per scheduling, cancel invalidates the arm (§3.2 rules 1–2); AD6, AD16, AD16b.
- **Hidden while the user is reading or about to click Dismiss** (pointer left, focus still inside). Mitigation: expiry requires the
  pointer and focus both outside (§3.2 rule 3, 6); AD16b.
- **Messages vanish before they are read** (screen reader, user looking elsewhere, window in the background). Mitigation: only success
  results auto-hide; pause on hover / focus (D14); count only while visible (D13).
- **Undo refusal hidden as if it succeeded** (success guessed from the text or from `placed`). Mitigation: typed `RestoreResultKind`
  chosen in the same switch arm as the notice (§3.3); AD15a–AD15d; TG26-C5.
- **Classification drift** (a new call site forgets the kind). Mitigation: Persistent default; contract test lists the `AutoDismiss`
  call sites (TG26-C2).

## 5. Acceptance tests (design level; fake data; hand-fired clock; no sleeps)

### 5.1 View model (`BannerAutoDismissTests`, Workspace and content)

| Case | Action | Expected |
| --- | --- | --- |
| AD1 | drop P on F (tree shown) → "Moved “P” to “F”." | one timer scheduled with 5 s; before firing the message is shown; fire → `BannerMessage` null |
| AD2 | as AD1, inspect the scheduled delay | exactly `AutoDismissDelay` (5 s); no timer for Persistent messages |
| AD3 | result A (auto), then result B (auto) before A expires | A's timer disposed; B has its own 5 s; firing B's clears B |
| AD4 | result A (auto), then error E (Persistent); fire A's timer late | E stays; nothing scheduled for E |
| AD5 | result A, then the same text A again; fire the first timer | the message stays (old turn); the second timer clears it |
| AD6 | result A armed (arm 1); leave the tab (cancel); come back → arm 2 scheduled with 5 s; **fire arm 1's callback on purpose**; then fire arm 2 | after arm 1: A still shown (stale arm, same turn); after arm 2: cleared. While the tab was hidden no timer existed (D13) |
| AD7 | result A; Dismiss; fire A's timer | banner stays empty; no exception; a later message is unaffected |
| AD8 | result A while the Workspace tab is hidden | no timer until the tab is shown; then 5 s |
| AD9 | Move to Trash success (Undo offered); advance any time | no timer; message and Undo stay until replaced or dismissed (D9(a)) |
| AD10 | Archive success, Folder Archive / Trash success (Undo offered) | as AD9 |
| AD11 | Bulk Archive summary with Details (and Undo); Bulk Undo summary with Details | no timer; Details stays available |
| AD12 | progress "Archiving “X”…" then the store is held (gate) | no timer while progress shows; the result that follows decides |
| AD13 | every refusal / error listed in §2 (sampled per family: Could not…, typed refusal, reconcile notice, ReportError, preferences) | Persistent: no timer |
| AD14 | drop with the tree not updated ("… could not be updated.") | Persistent |
| AD15 | Undo of Archive succeeds → "Restored “X” to …" | auto: one 5 s timer; fire → cleared; no Undo involved |
| AD15a | Move P to the Trash, Undo; store returns `ProjectRestoreOutcome.Restored` (P not archived) → "Restored “P” to …" | `RestoreResultKind.Restored`; one 5 s timer; fire → cleared; selection rule unchanged |
| AD15b | as AD15a, but window B restored P first (`NotTrashed`), or its folder was trashed (`ParentTrashed`), or a same-named item took its place (`NameConflict`) | `Refused` / `AlreadyCleared`; banner text kept; **no timer** (advance and fire anything: unchanged) |
| AD15c | Archive (or Trash) folder F, Undo; store returns `FolderClearOutcome.Cleared` → "Unarchived / Restored folder “F” to …" | `Restored`; one 5 s timer; fire → cleared |
| AD15d | as AD15c, but window B archived F again (`MarkedAgain`) or archived its parent (`ParentArchived`) | `Refused`; banner kept; **no timer** |
| AD15e | Restore a Project and a Folder from the Workspace Trash panel; Unarchive a Folder from the Archived panel | panel notice as today; the Workspace banner and its timer untouched |
| AD16 | result A armed (arm 1); pointer enters (cancel) at 4 s; pointer leaves → arm 2 with 5 s; **fire arm 1 on purpose**; fire arm 2 | after arm 1: A still shown; after arm 2: cleared (D14) |
| AD16b | result A armed; focus moves to the banner's Dismiss button (Tab) → cancel; pointer enters and leaves while focus stays | **no** new timer while focus is inside; fire the old arm → A still shown. Focus leaves the banner → one new 5 s timer; fire it → cleared |
| AD17 | content: "Restored to {path}." then select another Project; fire | ignored (content deactivated); the new Project shows no stale message |
| AD18 | owner deactivated / window closed with a pending timer; fire | nothing happens; timer disposed |
| AD19 | content errors, Trash notice (Undo), Destination notice | unchanged: no timer |

### 5.2 Contract (`Phase26ContractTests`)

- TG26-C1: `BannerMessage` setters stay private; every write goes through the owner's `ShowBanner`; the default kind is Persistent.
- TG26-C2: the `AutoDismiss` call sites are exactly the reviewed list (§2), on success branches only.
- TG26-C3: the expiry callback checks the message turn **and** the timer arm, visibility, pointer-outside and focus-outside before
  clearing; every cancel increments the arm; `OfferUndo` cancels a pending timer; Undo / Details force Persistent.
- TG26-C4: Owner and Repository banners, the content Trash and Destination notices and `IWorkspaceStore` are unchanged; no migration
  (LatestVersion 10), packages unchanged.
- TG26-C5: `RestoreCoreAsync` and `ClearFolderCoreAsync` return a `RestoreResultKind` set in the same switch arm as the notice; the
  Undo callers choose the kind from it; no `notice` string comparison and no `placed != null` test decides `AutoDismiss`; the panel
  callers read `Notice` only.
- TG26-C6: the window wires each banner's visibility, `IsMouseOver` and `IsKeyboardFocusWithin` to the view model and never clears a
  banner itself.

### 5.3 GUI (Windows, test profile only; isolated launcher, `--data-root`, fake data)

| Case | Action | Expected |
| --- | --- | --- |
| GT26-1 | drag P onto F | "Moved “P” to “F”." shows, disappears after about 5 s without input |
| GT26-2 | drop P onto F, then within 3 s drop Q onto G | the second message stays a full ~5 s after it appeared (not hidden by the first timer) |
| GT26-3 | Fixture (created in the app on the test profile): top-level Folders F and G, top-level Projects P and **Dup**, and a Project **Dup** inside F (allowed: different parents). The drop pre-check does not compare names (`TryResolveDrop`, `.DragDrop.cs:112–136`), so dragging the top-level Dup onto F shows the Move cursor and the store refuses with `DuplicateName` (as DD6). Steps: drop P onto G (t0: "Moved “P” to “G”." appears); within 3 s drop the top-level Dup onto F | "The destination already contains a folder or project named 'Dup'." appears; Dup stays at the top level (database unchanged); the refusal is still shown at t0 + 7 s and at t0 + 10 s (after the Move banner's timer would have expired) and stays until Dismiss |
| GT26-4 | Move a project to the Trash | the message and Undo stay well past 5 s; Undo still works |
| GT26-5 | Bulk Archive in a folder | the summary and Details stay past 5 s; Details opens |
| GT26-6 | drop P onto F, rest the pointer on the banner for 8 s, move away | it stays while hovered and hides ~5 s after leaving (D14) |
| GT26-6b | drop P onto F, Tab to the banner's Dismiss button, move the pointer over the banner and away, wait 8 s, then click in the tree | stays while focus is on Dismiss; hides ~5 s after focus leaves (D14) |
| GT26-11 | Move a project to the Trash, Undo (restores to a loaded folder) | "Restored “X” to …" hides after ~5 s |
| GT26-12 | Archive a folder, Undo | "Unarchived folder “F” to …" hides after ~5 s |
| GT26-7 | drop P onto F, switch to Repository at once, wait 10 s, return | the message is still there and hides ~5 s after returning (D13) |
| GT26-8 | content: Undo a content Trash so that the item returns to another folder ("Restored to …") | hides after ~5 s; the Trash notice behaves as before |
| GT26-9 | drop P onto F and press Dismiss at once | gone at once; no later flicker |
| GT26-10 | Owner / Repository open failure | banner stays (unchanged) |

## 6. Decisions (D7–D9, D12–D16 approved)

Approved as recommended: **D7(a)** only the successful results listed in §2, without any action in them, auto-hide; **D8** each new
message (same text included) has its own 5-second schedule; **D9(a)** and **D12(a)** banners with Undo or Details stay; **D13(a)** count
only while the banner is visible, a fresh 5 s when it is shown again; **D14(a)** hold while the pointer is over the banner or keyboard
focus is inside it, a fresh 5 s once both are outside; **D15(a)** Owner and Repository banners unchanged; **D16(a)** reconcile and
reveal notices stay. The options as they were proposed:

- **D7 which messages auto-hide**: (a) only successful results without Undo, Details or another action (§2; recommended);
  (b) also reconcile / reveal notices; (c) every non-error message.
- **D8 new messages**: each message owns its turn and each scheduling its arm; a new message (same text included) restarts its own
  5 s and retires the old turn and arm (recommended).
- **D9 Undo**: (a) a message carrying Undo does not auto-hide while Undo is offered (recommended; no contract change); (b) it hides
  after 5 s and Undo moves to a persistent place (Ctrl+Z / menu) — changes the Undo contract, its own tests, larger scope.
- **D12 Details**: (a) Bulk summaries with Details stay (recommended); (b) hide them and offer Details elsewhere.
- **D13 hidden surfaces**: (a) count only while the banner is visible; a hidden or re-shown banner starts a fresh 5 s (recommended);
  (b) count from the moment the message is set, visible or not.
- **D14 attention pause**: (a) hold while hovered or focused, a fresh 5 s only after both the pointer and focus are outside
  (recommended); (b) no pause.
- **D15 Owner / Repository banners**: (a) unchanged — all their messages are failures with an action (recommended); (b) include them.
- **D16 reconcile / reveal notices**: (a) keep (they explain why the selection or tree changed; recommended); (b) auto-hide (D7(b)).
- Phase 27 (R4) questions D10 / D11 stay as written in the Phase 25 proposal.

## 7. Not changed by this proposal

Schema, migrations, packages, version, Phase 25 behaviour other than the drop's success message timing, the .NET 8 legacy variant.
No release.

## 8. Implementation status (pending verification)

Implemented on `codex/phase-26-notification-auto-dismiss` after `ed6cb2c`. The solution builds in Debug and Release (0 warnings,
0 errors). Tests and contracts are written but **not run**; no GUI run. Pending: `BannerAutoDismissTests` (AD1–AD16b, AD18),
`ContentBannerAutoDismissTests` (content success, AD17, AD19), `Phase26ContractTests` (TG26-C1–C6) and the synced `Phase25ContractTests`
in Debug and Release; the full suites; GUI GT26-1–12 (with GT26-6b) on a Windows test profile. Phase 25 evidence (§0, including the
failing full-suite figures and O2 with no known cause) and the separately tracked Phase 15–23, D10 and P3 items are unchanged.
Toolbar button customisation stays in Phase 27.

Files:

- `BannerAutoDismiss.cs` (new): `BannerKind`, `IBannerClock` / `TaskDelayBannerClock`, the per-banner schedule `BannerAutoDismiss`
  (message turn, timer arm, rules 1–10 of §3.2) and `RestoreResultKind` (§3.3).
- `WorkspaceAreaViewModel.cs`: `BannerMessage` setter → `ShowBanner(value, Persistent)`; `ShowBanner` with the Undo / Details guard;
  `ReportBannerView`, `ShutdownBanners`, `BannerClock` (handed to each new content view); tab selection and Retry drive the owner
  state; `OfferUndo` makes the message Persistent; `AutoDismiss` at Removed empty folder and the Undo Archive `Undone` arm;
  `RestoreCoreAsync` / `ClearFolderCoreAsync` return `RestoreResultKind`, and `UndoCoreAsync` / `UndoFolderCoreAsync` choose the kind
  from it. `WorkspaceAreaViewModel.DragDrop.cs`: the drop result is `AutoDismiss` only when the tree was updated.
- `ProjectContentViewModel.cs`: the same entry point; `AutoDismiss` only for "Restored to …" after a typed `Restored` outcome;
  `Deactivate` shuts the schedule down; the Trash notice (Undo) and the Destination notice are unchanged.
- `MainWindow.xaml` / `.xaml.cs`: both banner borders report visible / pointer over / keyboard focus within; `OnClosed` shuts the
  timers down. The view never clears a banner.

Notes and deviations (for review):

- **Persistent through the setter.** The proposal says every write goes through `ShowBanner`; the existing ~80 assignments were kept
  as `BannerMessage = …`, and the private setter itself is `ShowBanner(value, BannerKind.Persistent)`, so every write does go through
  it with Persistent as the default (TG26-C1). Only the reviewed sites call `ShowBanner(…, AutoDismiss)` (TG26-C2).
- **Reveal outcomes that keep the message.** Two reveal handlers re-assigned the current text (`Selected` / `Superseded =>
  BannerMessage`). That would now start a new turn and stop a running countdown, so they return early instead; the text shown is the
  same as before.
- **Phase 25 contract sync.** TG25-C2 asserted the old drop banner line; the assertion now names the `ShowBanner(…)` line with the same
  text (no other Phase 25 assertion changed).
- **Visibility.** "Visible" is the window's report (`IsVisible` of the banner border, which also covers the tab and the message being
  set) combined with the owner state (Workspace tab selected; content view not deactivated). A content view is created per Project, so
  its `Deactivate` ends its schedule for good (rule 7 / 9).
- **§2 / AD11 wording.** `:1769` is the **Bulk Undo** summary (it carries Details), not a Bulk Unarchive summary; the Bulk Unarchive
  result is the Archived panel's own notice. AD11 covers the Bulk Archive and Bulk Undo summaries.
- **Sampling.** AD13 samples a typed drop refusal and the not-empty folder notice; AD19 covers the content Trash notice and a content
  Undo refusal. The Destination notice and the Owner / Repository banners are covered by contract (TG26-C2, TG26-C4), not by a view-model case.
