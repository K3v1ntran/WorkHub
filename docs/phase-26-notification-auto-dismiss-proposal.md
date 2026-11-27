# WorkHub Phase 26 proposal: operation messages hide after 5 seconds

Revision 1 (for review). Baseline: `main` = `0f42125` (Phase 25 merged). Branch `codex/phase-26-notification-auto-dismiss`.
Request R3 of the Phase 25 proposal (§1 R3, §5 D7–D9). Nothing here is implemented yet.

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
| Undo of Archive: "Restored “X” to …" (`:1452`) — the Undo is consumed, none is offered | Result | **auto-hide** |
| Undo of Trash / Folder lifecycle: success text of `RestoreCoreAsync` notice (`:1251`, `:2320`) | Result (success branch only) | **auto-hide** (refusal branches keep) |
| Content: "Restored to {path}." (`ProjectContentViewModel.cs:935`) | Result | **auto-hide** |
| "Moved “X” to the Trash." (`:1198`), "Archived “X”." (`:1389`), "Archived folder / Moved folder … to the Trash." (`:2260`) — Undo offered | Result + Undo | keep while Undo is offered (D9) |
| Bulk Archive summary (`:1633`, Details + Undo), Bulk Unarchive summary (`:1769`, Details) | Result + Details | keep (D12) |
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

1. **Start.** The count starts when an `AutoDismiss` message is set **and its surface is visible** (Workspace tab selected for both
   Workspace and content banners; the content banner also needs its Project shown). If it is set while hidden, the count starts when
   the surface becomes visible (D13).
2. **Expiry.** After 5 s (`AutoDismissDelay = 5 s`, one constant) the message is cleared exactly as by Dismiss — only if it is still the
   same message turn (below). Clearing an `AutoDismiss` message never touches Undo (it has none).
3. **New message replaces the schedule.** Every `ShowBanner` call — any kind, **including the same text again** — starts a new message
   turn: the previous timer is cancelled and its turn number is retired. A new `AutoDismiss` message gets its own full 5 s (D8).
   A Persistent message stops any countdown.
4. **Late callbacks.** Each scheduled callback carries its turn number; on expiry it does nothing unless the turn is still current and
   the surface still owns that message. A callback of an older message can never hide a newer one (same rule as the Phase 25 hover turn).
5. **Manual dismiss.** Dismiss clears at once and cancels the timer (behaviour otherwise unchanged).
6. **Pause on attention (D14).** While the pointer is over the banner or keyboard focus is inside it, the countdown is held; when both
   leave, a fresh 5 s starts.
7. **Context changes.** Leaving the Workspace tab, a Retry / visit end, selecting another Project (content banner) or deactivating the
   content view cancels the timer; the text stays as today and, if still `AutoDismiss`, counts again from 5 s when shown (D13).
   Search activation and the Trash / Archived panels do not hide the Workspace banner, so they do not affect the timer.
8. **Window close / shutdown.** Closing the main window (or app shutdown) disposes every pending timer; a callback that still arrives
   finds its owner deactivated and does nothing. No timer keeps the process alive.
9. **Clock.** An injectable `IBannerClock` (same contract as `IDragHoverClock`: `Schedule(delay, callback) → IDisposable`, the
   callback posted to the UI context; production built on the same `Task.Delay` pattern). Tests use a hand-fired clock; no sleeps.

### 3.3 Not in Phase 26

- Toolbar button customisation (R4) stays in **Phase 27** (D10/D11 of the Phase 25 proposal).
- No new Undo path (no Ctrl+Z, no menu) unless D9(b) is chosen — that would change the Undo contract and needs its own tests.
- No change to Owner / Repository banners, the content Trash and Destination notices, schema, migrations, packages, version.

## 4. Risks

- **Losing Undo / Details.** Mitigation: Undo- or Details-bearing messages are forced Persistent (§3.1); tests AD9–AD11.
- **A late timer hides a newer message** (e.g. an error that replaced a result). Mitigation: turn numbers (§3.2.4); AD4–AD6.
- **Messages vanish before they are read** (screen reader, user looking elsewhere, window in the background). Mitigation: only success
  results auto-hide; pause on hover / focus (D14); count only while visible (D13).
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
| AD6 | result A in visit 1; leave the tab and come back; fire the visit-1 timer | ignored; a new 5 s starts when the banner is visible again (D13) |
| AD7 | result A; Dismiss; fire A's timer | banner stays empty; no exception; a later message is unaffected |
| AD8 | result A while the Workspace tab is hidden | no timer until the tab is shown; then 5 s |
| AD9 | Move to Trash success (Undo offered); advance any time | no timer; message and Undo stay until replaced or dismissed (D9(a)) |
| AD10 | Archive success, Folder Archive / Trash success (Undo offered) | as AD9 |
| AD11 | Bulk Archive summary with Details (and Undo); Bulk Unarchive summary with Details | no timer; Details stays available |
| AD12 | progress "Archiving “X”…" then the store is held (gate) | no timer while progress shows; the result that follows decides |
| AD13 | every refusal / error listed in §2 (sampled per family: Could not…, typed refusal, reconcile notice, ReportError, preferences) | Persistent: no timer |
| AD14 | drop with the tree not updated ("… could not be updated.") | Persistent |
| AD15 | Undo of Archive succeeds → "Restored “X” to …" | auto: cleared after 5 s; no Undo involved |
| AD16 | pointer over the banner (view-model hook) at 4 s; leave; fire | the first timer is cancelled on enter; a fresh 5 s after leave (D14) |
| AD17 | content: "Restored to {path}." then select another Project; fire | ignored (content deactivated); the new Project shows no stale message |
| AD18 | owner deactivated / window closed with a pending timer; fire | nothing happens; timer disposed |
| AD19 | content errors, Trash notice (Undo), Destination notice | unchanged: no timer |

### 5.2 Contract (`Phase26ContractTests`)

- TG26-C1: `BannerMessage` setters stay private; every write goes through the owner's `ShowBanner`; the default kind is Persistent.
- TG26-C2: the `AutoDismiss` call sites are exactly the reviewed list (§2), on success branches only.
- TG26-C3: the expiry callback checks the turn number before clearing; `OfferUndo` cancels a pending timer; Undo / Details force Persistent.
- TG26-C4: Owner and Repository banners, the content Trash and Destination notices and `IWorkspaceStore` are unchanged; no migration
  (LatestVersion 10), packages unchanged.

### 5.3 GUI (Windows, test profile only; isolated launcher, `--data-root`, fake data)

| Case | Action | Expected |
| --- | --- | --- |
| GT26-1 | drag P onto F | "Moved “P” to “F”." shows, disappears after about 5 s without input |
| GT26-2 | drop P onto F, then within 3 s drop Q onto G | the second message stays a full ~5 s after it appeared (not hidden by the first timer) |
| GT26-3 | drop P onto F, then within 3 s trigger a refusal (drag A onto its child, or rename to a duplicate) | the refusal stays until dismissed |
| GT26-4 | Move a project to the Trash | the message and Undo stay well past 5 s; Undo still works |
| GT26-5 | Bulk Archive in a folder | the summary and Details stay past 5 s; Details opens |
| GT26-6 | drop P onto F, rest the pointer on the banner for 8 s, move away | it stays while hovered and hides ~5 s after leaving (D14) |
| GT26-7 | drop P onto F, switch to Repository at once, wait 10 s, return | the message is still there and hides ~5 s after returning (D13) |
| GT26-8 | content: Undo a content Trash so that the item returns to another folder ("Restored to …") | hides after ~5 s; the Trash notice behaves as before |
| GT26-9 | drop P onto F and press Dismiss at once | gone at once; no later flicker |
| GT26-10 | Owner / Repository open failure | banner stays (unchanged) |

## 6. Decisions for review

- **D7 which messages auto-hide**: (a) only successful results without Undo, Details or another action (§2; recommended);
  (b) also reconcile / reveal notices; (c) every non-error message.
- **D8 new messages**: each message owns its turn; a new message (same text included) restarts its own 5 s and retires the old
  timer (recommended).
- **D9 Undo**: (a) a message carrying Undo does not auto-hide while Undo is offered (recommended; no contract change); (b) it hides
  after 5 s and Undo moves to a persistent place (Ctrl+Z / menu) — changes the Undo contract, its own tests, larger scope.
- **D12 Details**: (a) Bulk summaries with Details stay (recommended); (b) hide them and offer Details elsewhere.
- **D13 hidden surfaces**: (a) count only while the banner is visible; a hidden or re-shown banner starts a fresh 5 s (recommended);
  (b) count from the moment the message is set, visible or not.
- **D14 attention pause**: (a) hold while hovered or focused, fresh 5 s after (recommended); (b) no pause.
- **D15 Owner / Repository banners**: (a) unchanged — all their messages are failures with an action (recommended); (b) include them.
- **D16 reconcile / reveal notices**: (a) keep (they explain why the selection or tree changed; recommended); (b) auto-hide (D7(b)).
- Phase 27 (R4) questions D10 / D11 stay as written in the Phase 25 proposal.

## 7. Not changed by this proposal

Schema, migrations, packages, version, Phase 25 behaviour other than the drop's success message timing, the .NET 8 legacy variant.
No release.
