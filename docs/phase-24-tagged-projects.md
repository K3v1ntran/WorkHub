# WorkHub Phase 24: Projects with this tag

Approved specification (Revision 2 + R2a). D1–D9 are approved, with D8(b): implement on the Phase 24 branch and keep earlier verification evidence separate. The product baseline is commit c3d9f04.

This feature is a read-only list inside Manage tags with a Show in Workspace action. It does not filter the Workspace tree or change tags, schema, or dependencies.

The accented pairs Äb/Az and Ölfeld/Ost distinguish the approved sort from normalized-key ordinal sorting. Kanri/Ｋａｎｒｉ exercises the ordinal tie-break after a width-insensitive comparison.

Use the tracked .NET 10 solution. Write the acceptance tests below, but do not run tests, SQL probes, or WorkHub in this implementation turn. A compile-only check is allowed where supported. Record any Linux or WPF limitations and leave verification pending. Phase 15–23, D10 (Settings toolbar), and P3 remain pending.

Implement on codex/phase-24-tagged-projects. Commit and push the phase branch for Codex review. Keep the local Directory.Build.props version change out of the commit. Do not merge to main, tag, or release.

## 3. Phase 24 design

### 3.1 User flow

1. **Manage tags…** (unchanged entry points, `WorkspaceAreaViewModel.cs:202, 987–998`). Selecting a tag row also loads a
   **Projects with this tag** list below the actions (D3: always visible for the selected row).
2. **List and header.** Each row: project name, folder path (top level shown as "Workspace"), a state marker — none for live,
   "Archived" for an archived project or one inside an archived folder, "In the Trash" for a project directly or through a folder
   in the Workspace Trash — and the stored spelling when it differs from the tag row's display spelling. Rows come in the order of
   §3.3. The header is built **only** from the list result (never from the tag row's counts, which may be older): "*E* projects" +
   (", *T* in the Trash keep it" when *T* > 0) + (" — and *N* more" when *N* = *E* + *T* − rows shown > 0), with *E* =
   `EligibleTotal`, *T* = `TrashedTotal`.
3. **Show in Workspace** (enabled for one selected list row that is not "In the Trash", not while `IsWriting` or `IsRefreshing`)
   closes the dialog and reveals that project **by id** at its current place (§3.5). Double-click / Enter on a list row = Show in
   Workspace (D4).
4. **Snapshot and late results.** The list is read when a row is selected and re-read for the still-selected key inside the
   existing post-commit refresh. Every read gets a new **request version**; selecting another row, selecting the same row again
   after another one, and every re-read bump it. A result **or a fault** is applied only when its version is still the current
   one — the key alone is never trusted (returning to the same tag starts a new request; the older request's answer is dropped).
   A current-version fault shows "The projects could not be listed." in the list area only; it never changes the dialog's result
   message, never sets `RefreshFailed` and never locks the change actions.
5. Closing via Show in Workspace follows the Phase 22/23 close contract: unavailable while `IsWriting`; `ChangedAny` keeps its
   meaning, so a committed change earlier in the same dialog still reloads the selected Project's details **before** the reveal
   (D7, §3.5).

### 3.2 Which projects are listed (D5)

Every Project that has a `ProjectTags` row with the key, in any effective state: **eligible** = live or archived (directly or
through a folder); **trashed** = in the Workspace Trash (directly or through a folder). The trashed group is listed so the totals
add up, but it cannot be revealed. Nothing else is read: no content, notes or links.

### 3.3 Store and service contract (additive, D2)

```
// IWorkspaceStore (Phase 24): read only; ONE deferred read transaction; never writes.
Task<TaggedProjectsResult> ListProjectsWithTagAsync(string nameKey, int limit, CancellationToken cancellationToken = default);

public sealed record TaggedProjectsResult(IReadOnlyList<TaggedProject> Rows, int EligibleTotal, int TrashedTotal)
{
    public int Hidden => EligibleTotal + TrashedTotal - Rows.Count;          // shown as "and N more"
}

public sealed record TaggedProject(long ProjectNodeId, string Name, string Path, string StoredSpelling, TaggedProjectState State);
public enum TaggedProjectState { Live, Archived, Trashed }   // effective state, same NodeLifecycle as tag eligibility
```

- **One snapshot.** `BeginTransaction(deferred: true)`; inside it: every `ProjectTags` row of the key, each project's effective
  state (the `NodeLifecycle` used by `IsTagEligible`, so `Trashed` means exactly "keeps the tag" in Phases 21–23), name and path;
  `EligibleTotal` = count of Live + Archived, `TrashedTotal` = count of Trashed — computed over **all** rows **before** the cap.
- **Name comparison `N(a, b)` (R2a).** Exactly the name part of the tree order `WorkspaceNodeOrder` (`WorkspaceRules.cs:181–200`):
  `CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth)`; when that is
  0, `string.CompareOrdinal(a, b)`. (It can be called as `WorkspaceNodeOrder.Instance.Compare((Folder, a, 0), (Folder, b, 0))`,
  as `WorkspaceSearchText.cs:115` does.) It is **not** "NFKC + upper case, then ordinal": that is how `NameKey` is built for
  uniqueness, and it orders e.g. `Az` before `Äb`, while the culture comparison orders `Äb` before `Az`.
- **Total order before the cap** (applied to the full set, then the first `limit` rows are kept):
  1. group: eligible (Live and Archived together) before Trashed;
  2. folder path, as the list of folder names from the top level down (a top-level project has the empty list, shown as
     "Workspace"): compared segment by segment with `N`; at the first unequal segment that result decides; if one list is a prefix
     of the other, the shorter list comes first (so top-level projects come before every project in a folder);
  3. project name with `N` (culture ignoring case and width, then ordinal);
  4. `ProjectNodeId` ascending (final tie-break; makes the order total, e.g. two directly trashed projects with exactly the same
     name in the same folder).
- **Cap.** `limit` = 500 (D6); `Rows.Count = min(limit, EligibleTotal + TrashedTotal)`; `Hidden ≥ 0`.
- `WorkspaceService.ListProjectsWithTagAsync(nameKey)` refuses an empty key before the store (as `PrepareTagDeleteAsync`) and
  passes `limit = 500`.

### 3.4 View-model contract

`TaggedProjects` (rows), `TaggedProjectsHeader` (from the result only), `TaggedProjectsError` (string or null),
`SelectedTaggedProject`, `ShowInWorkspaceCommand`, `RevealTarget` (project id or null). A private `_listVersion` is bumped on every
list request; the continuation applies rows, header and error only when its captured version equals `_listVersion`; a dropped
fault is logged without tag or project names and changes nothing visible.

### 3.5 Show in Workspace: reveal by id and project banners

After `ShowManageTags` returns, `WorkspaceAreaViewModel.ManageTagsAsync` (`:987–998`):

1. if `ChangedAny` and a project is selected: the existing details reload (`StartDetailsLoad`, awaited) — unchanged;
2. if `RevealTarget` is set: `RevealAsync(new SearchItemRef(SearchResultKind.Project, id))` (`:3000`). The resolve reads the
   project's **current** state and location (`RevealCoreAsync`, `:3034–3129`): a project moved since the list was read is found
   and selected at its new place; archived / in an archived folder → **Archived** opens with it / the outermost archived folder
   selected (existing behaviour); a path that keeps changing for both bounded attempts → `WorkspaceChanged`;
3. the caller maps the outcome to a **project** banner (same pattern as Open destination, `:3009–3031`):

| `WorkspaceRevealOutcome` | Banner |
| --- | --- |
| `Selected`, `Superseded` | none (the tree selection shows the project; a newer action won) |
| `Archived` | "The project is archived — it is selected under Archived." |
| `InArchivedFolder` | "The project is in an archived folder — that folder is selected under Archived." |
| `NotFound` | "The project no longer exists." |
| `Trashed`, `ProjectTrashed` | "The project is in the Trash." |
| `FolderTrashed` | "The project's folder is in the Trash." |
| `WorkspaceChanged`, `NoLongerArchived`, `MovedMeanwhile`, `ContainerMissing` | "The project was moved or changed meanwhile. Try Show in Workspace again." |
| `Busy` | "The database stayed busy (another program or window is writing). Try again." |
| other (`Failed`) | "The project could not be shown. Try again." |

Selection rule (existing): `SelectedNode`'s setter returns early when the same node is selected again (`:525–531`), so a reveal of
the already-selected project starts **no** extra details load; selecting another project starts exactly one.

### 3.6 Code layers expected to change

| Layer | File | Change |
| --- | --- | --- |
| Core | `IWorkspaceStore.cs`, new `TaggedProjectModels.cs`, `WorkspaceService.cs` | one read member, the result / row records and the state enum, the service wrapper |
| Infrastructure | `SqliteWorkspaceStore.Tags.cs` | the read in one deferred transaction (§3.3), sort and cap in code after the totals |
| Presentation | `ManageTagsViewModel.cs` | §3.4; list re-read inside `RefreshAfterCommitAsync` with a new version |
| Presentation | `WorkspaceAreaViewModel.cs` (`ManageTagsAsync`) | §3.5 steps 1–3 |
| App | `ManageTagsWindow.xaml` | the list (AutomationIds `ManageTagsProjects`, `ManageTagsProjectsHeader`, `ManageTagsShowInWorkspace`), the button; Enter / double-click on the list only |
| Tests | new `TaggedProjectsStoreTests`, `ManageTagsProjectsViewModelTests`, `ManageTagsRevealTests`, `Phase24ContractTests`; the exact tag-member lists in `Phase21ContractTests` R3, `Phase22ContractTests` TD-C2, `Phase23ContractTests` TP-C1 gain the one member (Phase 24 snapshot only, §4.6) |
| README | Manage tags paragraph | two sentences |

### 3.7 Out of scope

Any write (no bulk edit from the list, no remove-from-this-project, no editor opening from the dialog); tags on folders/content;
search syntax changes (no `tag:` filter); Undo; schema, package, version, security or profile changes; the Project editor.

### 3.8 Schema and dependencies: none

V001–V010 and `SchemaFingerprints.json` unchanged; packages unchanged; .NET 8 deliverable unchanged.

---

## 4. Acceptance tests (design level; fake data only; gates, no sleeps)

### 4.1 Fixture

P1 live {clientA, Infra}; P2 live in folder F1 {ClientA}; P3 archived {clientA}; P4 live in archived folder A1 {clientA}; P5
directly in the Trash {clientA}; P6 live in folder F2, F2 trashed {clientA}; P7 live {Infra}. Key `clienta` → P1–P6
(`EligibleTotal` 4, `TrashedTotal` 2).

**Comparer and cap fixture `K` (R2a; TG5a–c, TG8; tag key `bulk`; fake names).** V009 keeps `NameKey` unique among siblings that
are not trashed (`V009_WorkspaceFolderLifecycle.cs:59–64`), so same-`NameKey` siblings exist only in the Trash group:

| Part | Projects (tag `bulk` added while each project is still writable) |
| --- | --- |
| Eligible, top level | `Äb`, `Az` |
| Eligible, folder `Gen` | `G001` … `G476` (zero-padded: culture and ordinal agree) — TG5a / TG5b; `G001` … `G516` in TG5c |
| Eligible, folder `Ölfeld` | `P` |
| Eligible, folder `Ost` | `P` |
| Trash group, directly trashed, in live folder `T` | built in this order, each step finished before the next: create `Dup` (id *d1*), tag, move to Trash; create `Dup` again (id *d2* > *d1*), tag, move to Trash; create `Kanri`, tag, move to Trash; create `Ｋａｎｒｉ` (full width, same `NameKey`), tag, move to Trash; then `X01` … `X16`, each created, tagged, moved to Trash. TG5c uses only `Dup` (*d1*), `Dup` (*d2*), `Kanri` |

Expected order of `K` under §3.3 (eligible group): `Äb`, `Az` (top level; culture: `Ä` = `A` at the primary level, then `b` <
`z`), then folder `Gen` (`G001` …), then `Ölfeld/P`, then `Ost/P` (culture: `Ö` = `O`, then `l` < `s`). Trash group (all in `T`):
`Dup` *d1*, `Dup` *d2* (names equal under culture and ordinal → `ProjectNodeId`), `Kanri`, `Ｋａｎｒｉ` (equal under culture
ignoring width → ordinal: U+004B < U+FF2B), `X01` … `X16`. Under "NFKC + upper case, then ordinal" the order would instead be
`Az` before `Äb` and `Ost` before `Ölfeld` (U+00C4 / U+00D6 sort after ASCII), which TG8 rejects.

### 4.2 Store (`TaggedProjectsStoreTests`, Phase 24 snapshot)

| Case | Action | Expected |
| --- | --- | --- |
| TG1 listing | `ListProjectsWithTagAsync("clienta", 500)` | `Rows` = P1–P6 once each in §3.3 order (eligible: P1 "Workspace", P3 "Workspace", P4 "A1", P2 "F1" ordered by path then name; then Trashed P5, P6); states; paths; stored spelling `ClientA` on P2; P7 absent; `EligibleTotal` 4, `TrashedTotal` 2, `Hidden` 0 |
| TG2 same rule as eligibility | for every listed project, `State == Trashed` ⇔ it is a "keeps the tag" row in `PrepareTagDeleteAsync` / `PrepareTagChangeAsync` | identical sets; `TrashedTotal` = the preview's `Trashed` |
| TG3 read only | data snapshot before / after | identical; only `BeginTransaction(deferred: true)` (contract TG-C2) |
| TG4 unknown key / empty key | unknown key → empty `Rows`, totals 0; empty key → refused by the service before the store | as stated |
| **TG5a** exactly 500 | fixture `K` with `G001` … `G476`: eligible 480 (`Äb`, `Az`, 476 × `Gen/G…`, `Ölfeld/P`, `Ost/P`), trashed 20 (`Dup` *d1*, `Dup` *d2*, `Kanri`, `Ｋａｎｒｉ`, `X01` … `X16`); `ListProjectsWithTagAsync("bulk", 500)` | `Rows.Count` 500; `EligibleTotal` 480; `TrashedTotal` 20; `Hidden` 0; `Rows` element by element: `Äb`, `Az`, `Gen/G001` … `Gen/G476`, `Ölfeld/P`, `Ost/P`, `T/Dup` (*d1*), `T/Dup` (*d2*), `T/Kanri`, `T/Ｋａｎｒｉ`, `T/X01` … `T/X16`; **both `Dup` rows present**, *d1* before *d2* |
| **TG5b** 501 | the TG5a fixture + one eligible top-level project `Aa` (culture: `Aa` < `Äb`, so it sorts **first**) | `Rows.Count` 500; `EligibleTotal` 481; `TrashedTotal` 20; `Hidden` 1; `Rows` = `Aa`, then the TG5a order up to `T/X15`; `T/X16` (the last row) is the one hidden; both `Dup` rows still present in *d1*, *d2* order |
| **TG5c** cap inside the eligible Live/Archived group | fixture `K` with `G001` … `G516`: eligible 520 (`Äb`, `Az`, 516 × `Gen/G…`, `Ölfeld/P`, `Ost/P`), trashed 3 (`Dup` *d1*, `Dup` *d2*, `Kanri`) | `Rows.Count` 500 = the first 500 eligible rows in order: `Äb`, `Az`, `Gen/G001` … `Gen/G498`; **no** Trash row; `EligibleTotal` 520; `TrashedTotal` 3; `Hidden` 23 |
| **TG8** comparer (R2a) | fixture `K` (TG5a variant); compare the returned order with two reference sorts of the same rows | `Rows` equal to the §3.3 order (culture `IgnoreCase \| IgnoreWidth`, then ordinal, per segment, then `ProjectNodeId`): `Äb` before `Az`, `Ölfeld/P` before `Ost/P`, `Kanri` before `Ｋａｎｒｉ`, `Dup` *d1* before *d2*; and **not** equal to the "NFKC + upper case, then ordinal" order (which would put `Az` before `Äb` and `Ost/P` before `Ölfeld/P`) |
| TG6 consistency | window B moves P2 from F1 to F2's sibling F3 between two reads | each read is one snapshot; the second read shows the new path and keeps the same totals |
| **TG7** counts from the list read, not from the tag row | read `ListTagsInUseAsync` (row: 4 changeable / 2 trashed); window B then adds the key to P7 and trashes P1; read `ListProjectsWithTagAsync` | the list result says `EligibleTotal` 4 (P2, P3, P4, P7), `TrashedTotal` 3 (P1, P5, P6) — different from the old row — and its rows match those totals |

### 4.3 View model (`ManageTagsProjectsViewModelTests`, Phase 24 snapshot)

| Case | Action | Expected |
| --- | --- | --- |
| TGV1 load on selection | select `clientA` | rows = TG1 order; header "4 projects, 2 in the Trash keep it" from the list result |
| **TGV2a** late result of another key | hold the `clientA` list read (gate); select `Infra` (its read completes and is shown); release the `clientA` read | the `clientA` result is dropped; rows, header, error and Show in Workspace reflect `Infra` |
| **TGV2b** same key again, old read succeeds late | hold read #1 (`clientA`); select `Infra` (read #2 shown); select `clientA` again — before read #3 runs, window B adds the key to P7, so read #3 returns 5 eligible rows — let read #3 complete and be shown; then release read #1 (its snapshot: 4 eligible) | rows still = read #3 (P7 present), header "5 projects, …", no error; Show in Workspace state from read #3's selection; read #1's result was dropped although its key equals the selected key |
| **TGV2c** same key again, old read faults late | as TGV2b, but read #1 is released with `IOException` after read #3 is shown | rows, header from read #3; `TaggedProjectsError` null; Show in Workspace still enabled for a selected live row; dialog `Message` unchanged |
| TGV3 Show in Workspace gating | select P1 / P5 / none | enabled for P1, disabled for P5 (Trash) and none; disabled while `IsWriting` (apply held) and while `IsRefreshing` |
| TGV4 close for reveal | Show in Workspace on P2 | `CloseRequested` with `ChangedAny`; `RevealTarget == P2`; no store write |
| TGV5 refresh after commit | Rename `clientA` → `ClientX` committed; post-commit `tagsInUse` and list reads gated (armed before the apply is released, Phase 23 sequence S) | while held: Phase 23 state unchanged (TP3 assertions); after release: a new-version list read for the selected key; if the key vanished, empty rows and header "0 projects" |
| TGV6 list read failure (current version) | the current list read throws | `TaggedProjectsError` = "The projects could not be listed."; rows empty; dialog `Message`, `RefreshFailed` and change actions unaffected |
| **TGV7** header from the list read | as TG7: the dialog shows the old tag row (4 / 2); the list read returns 4 / 3 | header "4 projects, 3 in the Trash keep it" (the row's own count text stays as read); a 501-row fixture shows "… — and 1 more" |

### 4.4 Workspace flow (`ManageTagsRevealTests`, area tests with the search service wired)

Every case asserts: store Apply count unchanged by Show in Workspace (0 extra rename/merge/delete calls), data snapshot unchanged
by the reveal, and the banner exactly as §3.5.

| Case | Action | Expected outcome / target / banner |
| --- | --- | --- |
| TGA1 reveal live | Show in Workspace on P2 | `Selected`; tree selects P2 inside F1; no banner |
| TGA2 reveal archived | on P3; on P4 | `Archived` / `InArchivedFolder`; Archived opens with P3 / A1 selected; the two banners of §3.5 |
| **TGA3a** moved meanwhile, still found | window B moves P2 from F1 to F3 after the list was read | `Selected`; tree selects P2 **inside F3**; no banner |
| **TGA3b** removed meanwhile | the resolve for P2 is scripted to return not found (or B removes it through the store where possible) | `NotFound`; "The project no longer exists."; selection unchanged |
| **TGA3c** project trashed meanwhile | B moves P2 to the Workspace Trash | `Trashed` / `ProjectTrashed`; "The project is in the Trash." |
| **TGA3d** folder trashed meanwhile | B trashes F1 | `FolderTrashed`; "The project's folder is in the Trash." |
| **TGA3e** location keeps changing | the resolve is gated and B moves P2 between the two bounded attempts (gates, no sleep) | `WorkspaceChanged`; "The project was moved or changed meanwhile. Try Show in Workspace again." |
| **TGA3f** busy | the resolve reports `WorkspaceSearchBusyException` | `Busy`; the busy banner |
| **TGA4a** committed change, target = the selected project | P1 selected; in the dialog Rename `clientA` → `ClientX` (committed), then Show in Workspace on P1 | ordered log: `details:P1` reload ×1 (from `ChangedAny`) → reveal `Selected` on P1; **no** further `details:P1` load (selection unchanged, `:525–531`); total `details:P1` = before + 1; rename Apply count 1 |
| **TGA4b** committed change, target = another project | P1 selected; Rename committed, then Show in Workspace on P2 | ordered log: `details:P1` reload ×1 → reveal starts → `details:P2` load ×1 (selection changed to P2); totals: `details:P1` before + 1, `details:P2` before + 1; the P1 reload completes before the reveal starts; rename Apply count 1 |

### 4.5 Contract (`Phase24ContractTests`, Phase 24 snapshot)

No migration (LatestVersion 10), packages unchanged; `IWorkspaceStore` tag members = Phase 21's four + Phase 22's two +
`ListProjectsWithTagAsync`; its signature returns `Task<TaggedProjectsResult>` and takes `(string, int, CancellationToken)`; the
new read uses `BeginTransaction(deferred: true)` exactly once and contains no `INSERT` / `UPDATE` / `DELETE`;
`DialogService.ShowManageTags` still wires `Closing` to `CanClose`; the Enter handler on Confirm stays delete-only.

### 4.6 Regressions and evidence by snapshot

| Group | Phase 23 snapshot `c3d9f04` | Phase 24 snapshot |
| --- | --- | --- |
| Phase 21 R3, Phase 22 TD-C2, Phase 23 TP-C1 (exact `*Tag*` member lists) | run **as written** (six members) | the one expected-list line gains `ListProjectsWithTagAsync`; every other assertion unchanged |
| Phase 21–23 VM, store and editor tests (TR*, TE*, TD*, TD-VM*, TP*, TP-R14) | run as written | run as written |
| Full Debug, full Release | Phase 23 evidence | Phase 24 evidence |

Separate evidence folders per snapshot (`work\phase23-…\`, `work\phase24-…\`); a run counts only for the snapshot it was built
from. Phase 24 results close nothing of Phase 14–23, D10 or P3.

### 4.7 GUI (test profile only; same launcher and UIA rules as Phase 22 §4.6)

| Case | Action | Expected |
| --- | --- | --- |
| GT24-1 list | open Manage tags, select `clientA` | rows, markers and header as TG1 / TGV1 (UIA texts) |
| GT24-2 reveal | Show in Workspace on P2; then on P3 | dialog closes; tree selects P2; Archived opens with P3 and its banner |
| GT24-3 keyboard | Enter in the list on P1; Enter on Confirm in Delete mode | reveal of P1; Confirm never applies on Enter (Phase 22 GD5 unchanged) |

No real profile, not even its metadata.
