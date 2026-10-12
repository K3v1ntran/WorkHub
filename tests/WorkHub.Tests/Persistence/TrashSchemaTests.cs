using Microsoft.Data.Sqlite;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// Database-enforced V006 invariants with raw SQL in the product's SQLite runtime (Phase 7 proposal revision 2 §4.1, §10.1).
/// What SQL cannot express (the marked set is exactly the root plus its active subtree, ItemCount equals the rows marked)
/// is the store's job and is covered by the store tests.
/// </summary>
public sealed class TrashSchemaTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnection _connection;
    private readonly long _alpha;
    private readonly long _beta;

    public TrashSchemaTests()
    {
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize(); // pinned: V007 adds delete guards (see WorkspaceTrashSchemaTests)
        _connection = _root.Open();
        _alpha = Db.InsertNode(_connection, null, "Project", "Alpha");
        Db.InsertProjectDetails(_connection, _alpha, "0123456789abcdef0123456789abcdef");
        _beta = Db.InsertNode(_connection, null, "Project", "Beta");
        Db.InsertProjectDetails(_connection, _beta, "fedcba9876543210fedcba9876543210");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _root.Dispose();
    }

    private long Insert(string sql, params (string, object?)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql + " RETURNING Id;";
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Db.Now);
        return (long)command.ExecuteScalar()!;
    }

    private long Folder(long project, long? parent, string name, long? marker = null) =>
        Insert("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt, TrashEntryId) VALUES ($p, $parent, $n, upper($n), $now, $now, $m)",
            ("$p", project), ("$parent", parent), ("$n", name), ("$m", marker));

    private long Link(long project, long? folder, string name = "L", long? marker = null) =>
        Insert("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt, TrashEntryId) VALUES ($p, $f, 'Web', $n, 'https://x.test/', $now, $now, $m)",
            ("$p", project), ("$f", folder), ("$n", name), ("$m", marker));

    private long Note(long project, long? folder, string title = "N", long? marker = null) =>
        Insert("INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt, TrashEntryId) VALUES ($p, $f, $t, 'body', $now, $now, $m)",
            ("$p", project), ("$f", folder), ("$t", title), ("$m", marker));

    private long Entry(long project, string kind, long rootId, long count = 1) =>
        Insert("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, $k, $r, $c, $now)",
            ("$p", project), ("$k", kind), ("$r", rootId), ("$c", count));

    private void Exec(string sql, params (string, object?)[] parameters) => Db.Exec(_connection, sql, parameters);

    private string Fails(Action action) => Assert.Throws<SqliteException>(action).Message;

    private string Fails(string sql, params (string, object?)[] parameters) => Fails(() => Exec(sql, parameters));

    private long? Marker(string table, long id) =>
        Db.ScalarString(_connection, $"SELECT CAST(TrashEntryId AS TEXT) FROM {table} WHERE Id = $id;", ("$id", id)) is { } text ? long.Parse(text) : null;

    // ---- Same-Project rule: INSERT ------------------------------------------------------------------------------------

    [Fact]
    public void CrossProjectMarkedInsert_IsRejected_ForEveryContentKind()
    {
        var alphaRoot = Folder(_alpha, null, "Root");
        var alphaEntry = Entry(_alpha, "Folder", alphaRoot);
        var betaFolder = Folder(_beta, null, "B");

        // Row in Project B, marker = entry of Project A.
        Assert.Contains("WH_TRASH_INSERT", Fails(() => Folder(_beta, null, "X", alphaEntry)));
        Assert.Contains("WH_TRASH_INSERT", Fails(() => Link(_beta, betaFolder, marker: alphaEntry)));
        Assert.Contains("WH_TRASH_INSERT", Fails(() => Note(_beta, betaFolder, marker: alphaEntry)));
        Assert.Equal(0, Db.ScalarLong(_connection, "SELECT count(*) FROM ProjectLinks;") + Db.ScalarLong(_connection, "SELECT count(*) FROM ProjectNotes;"));
    }

    [Fact]
    public void SameProjectMarkedInsert_IsAlsoRejected_RowsAlwaysStartActive()
    {
        var root = Folder(_alpha, null, "Root");
        var entry = Entry(_alpha, "Folder", root);

        Assert.Contains("WH_TRASH_INSERT", Fails(() => Folder(_alpha, null, "X", entry)));
        Assert.Contains("WH_TRASH_INSERT", Fails(() => Link(_alpha, null, marker: entry)));
        Assert.Contains("WH_TRASH_INSERT", Fails(() => Note(_alpha, null, marker: entry)));
    }

    [Fact]
    public void ActiveRows_WithNullMarkers_AreInsertedAndUpdatedNormally_InTwoProjects()
    {
        foreach (var project in new[] { _alpha, _beta })
        {
            var folder = Folder(project, null, "Specs");
            var child = Folder(project, folder, "Drafts");
            var link = Link(project, child);
            var note = Note(project, child);
            Exec("UPDATE ProjectFolders SET Name = 'Specs 2', NameKey = 'SPECS 2', UpdatedAt = 'u' WHERE Id = $id;", ("$id", folder));
            Exec("UPDATE ProjectLinks SET Name = 'L2', FolderId = $f, UpdatedAt = 'u' WHERE Id = $id;", ("$f", folder), ("$id", link));
            Exec("UPDATE ProjectNotes SET Title = 'N2', Revision = Revision + 1, UpdatedAt = 'u' WHERE Id = $id;", ("$id", note));
            Exec("UPDATE ProjectNotes SET FolderId = NULL, UpdatedAt = 'm' WHERE Id = $id;", ("$id", note));
            Assert.Null(Marker("ProjectFolders", child));
            Assert.Null(Marker("ProjectLinks", link));
            Assert.Null(Marker("ProjectNotes", note));
        }
    }

    // ---- Same-Project rule: UPDATE / marker transitions ---------------------------------------------------------------

    [Fact]
    public void CrossProjectMarkerUpdate_IsRejected_ForEveryContentKind()
    {
        var alphaEntry = Entry(_alpha, "Folder", Folder(_alpha, null, "Root"));
        var betaFolder = Folder(_beta, null, "B");
        var betaLink = Link(_beta, null);
        var betaNote = Note(_beta, null);

        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", alphaEntry), ("$id", betaFolder)));
        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectLinks SET TrashEntryId = $e WHERE Id = $id;", ("$e", alphaEntry), ("$id", betaLink)));
        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", alphaEntry), ("$id", betaNote)));
        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectNotes SET TrashEntryId = 999 WHERE Id = $id;", ("$id", betaNote))); // no such entry
        Assert.Null(Marker("ProjectFolders", betaFolder));
    }

    [Fact]
    public void SameProjectMarking_OfAFolderSubtreeALinkAndANote_IsAccepted_AndClearingIsAlwaysAllowed()
    {
        var root = Folder(_alpha, null, "Root");
        var child = Folder(_alpha, root, "Child");
        var inner = Link(_alpha, child);
        var innerNote = Note(_alpha, root);
        var folderEntry = Entry(_alpha, "Folder", root, 4);

        using (var transaction = _connection.BeginTransaction())
        {
            Db.Exec(_connection, "UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id IN ($a, $b);", ("$e", folderEntry), ("$a", root), ("$b", child));
            Db.Exec(_connection, "UPDATE ProjectLinks SET TrashEntryId = $e WHERE Id = $id;", ("$e", folderEntry), ("$id", inner));
            Db.Exec(_connection, "UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", folderEntry), ("$id", innerNote));
            transaction.Commit();
        }

        var link = Link(_alpha, null, "Alone");
        var linkEntry = Entry(_alpha, "Link", link);
        Exec("UPDATE ProjectLinks SET TrashEntryId = $e WHERE Id = $id;", ("$e", linkEntry), ("$id", link));
        var note = Note(_alpha, null, "Alone");
        var noteEntry = Entry(_alpha, "Note", note);
        Exec("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", noteEntry), ("$id", note));

        Assert.Equal(folderEntry, Marker("ProjectFolders", child));
        Assert.Equal(linkEntry, Marker("ProjectLinks", link));
        Assert.Equal(noteEntry, Marker("ProjectNotes", note));

        Exec("UPDATE ProjectLinks SET TrashEntryId = NULL WHERE Id = $id;", ("$id", link)); // entry -> NULL
        Assert.Null(Marker("ProjectLinks", link));
    }

    [Fact]
    public void EntryToAnotherEntry_MarkerChange_IsRejected_ForEveryContentKind()
    {
        var folder = Folder(_alpha, null, "F");
        var link = Link(_alpha, null);
        var note = Note(_alpha, null);
        var e1 = Entry(_alpha, "Folder", folder);
        var e2 = Entry(_alpha, "Link", link);
        var e3 = Entry(_alpha, "Note", note);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", e1), ("$id", folder));
        Exec("UPDATE ProjectLinks SET TrashEntryId = $e WHERE Id = $id;", ("$e", e2), ("$id", link));
        Exec("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", e3), ("$id", note));

        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", e2), ("$id", folder)));
        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectLinks SET TrashEntryId = $e WHERE Id = $id;", ("$e", e1), ("$id", link)));
        Assert.Contains("WH_TRASH_MARKER", Fails("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", e1), ("$id", note)));
        Assert.Equal(e1, Marker("ProjectFolders", folder));
    }

    // ---- Entry metadata, creation and deletion --------------------------------------------------------------------------

    [Theory]
    [InlineData("Id = Id + 100")]
    [InlineData("ProjectNodeId = $beta")]
    [InlineData("RootKind = 'Link'")]
    [InlineData("RootId = RootId + 1")]
    [InlineData("ItemCount = ItemCount + 1")]
    [InlineData("TrashedAt = 'later'")]
    [InlineData("ItemCount = ItemCount")] // even a no-op update
    public void EveryEntryColumn_IsImmutable(string assignment)
    {
        var entry = Entry(_alpha, "Folder", Folder(_alpha, null, "F"));

        Assert.Contains("WH_TRASH_ENTRY_IMMUTABLE", Fails($"UPDATE ProjectTrashEntries SET {assignment} WHERE Id = $id;", ("$id", entry), ("$beta", _beta)));
        Assert.Equal($"{_alpha}|Folder|1", Db.ScalarString(_connection, "SELECT ProjectNodeId || '|' || RootKind || '|' || ItemCount FROM ProjectTrashEntries WHERE Id = $id;", ("$id", entry)));
    }

    [Fact]
    public void EntryCreation_RequiresAnExistingActiveRootOfThatKindInTheSameProject()
    {
        var alphaFolder = Folder(_alpha, null, "F");
        var betaFolder = Folder(_beta, null, "F");
        var link = Link(_alpha, null);
        var trashedNote = Note(_alpha, null);
        var noteEntry = Entry(_alpha, "Note", trashedNote);
        Exec("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", noteEntry), ("$id", trashedNote));

        Assert.Contains("WH_TRASH_ROOT", Fails(() => Entry(_alpha, "Folder", betaFolder)));   // root in another Project
        Assert.Contains("WH_TRASH_ROOT", Fails(() => Entry(_alpha, "Folder", 9999)));         // missing root
        Assert.Contains("WH_TRASH_ROOT", Fails(() => Entry(_alpha, "Note", trashedNote)));    // already trashed root
        Assert.Contains("WH_TRASH_ROOT", Fails(() => Entry(_alpha, "Note", link)));           // wrong kind for that Id
        // An unknown kind: the BEFORE INSERT trigger runs before the CHECK constraint, so either refuses it.
        Assert.Matches("WH_TRASH_ROOT|CK_ProjectTrashEntries_RootKind", Fails(() => Entry(_alpha, "Project", alphaFolder)));
        Assert.Contains("CK_ProjectTrashEntries_ItemCount", Fails(() => Entry(_alpha, "Folder", alphaFolder, 0)));

        Entry(_alpha, "Folder", alphaFolder);
        Assert.Contains("UNIQUE", Fails(() => Entry(_alpha, "Folder", alphaFolder))); // one live entry per root
        Assert.True(Entry(_alpha, "Link", link) > 0); // same numeric Id space, other kind: independent
    }

    [Fact]
    public void ReferencedEntry_CannotBeDeleted_UntilEveryMarkerIsCleared()
    {
        var folder = Folder(_alpha, null, "F");
        var note = Note(_alpha, folder);
        var entry = Entry(_alpha, "Folder", folder, 2);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", entry), ("$id", folder));
        Exec("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", entry), ("$id", note));

        Assert.Contains("FOREIGN KEY", Fails("DELETE FROM ProjectTrashEntries WHERE Id = $id;", ("$id", entry)));
        Exec("UPDATE ProjectFolders SET TrashEntryId = NULL WHERE TrashEntryId = $e;", ("$e", entry));
        Assert.Contains("FOREIGN KEY", Fails("DELETE FROM ProjectTrashEntries WHERE Id = $id;", ("$id", entry))); // the note still refers to it
        Exec("UPDATE ProjectNotes SET TrashEntryId = NULL WHERE TrashEntryId = $e;", ("$e", entry));
        Exec("DELETE FROM ProjectTrashEntries WHERE Id = $id;", ("$id", entry));
        Assert.Equal(0, Db.Count(_connection, "ProjectTrashEntries"));
        Assert.Contains("FOREIGN KEY", Fails("DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", _alpha))); // content still restricts
    }

    // ---- Restore and nested entries -----------------------------------------------------------------------------------

    [Fact]
    public void NormalRestore_ClearsTheMarkersOnAllThreeTables_AndDeletesTheEntry()
    {
        var folder = Folder(_alpha, null, "F");
        var link = Link(_alpha, folder);
        var note = Note(_alpha, folder);
        var entry = Entry(_alpha, "Folder", folder, 3);
        foreach (var (table, id) in new[] { ("ProjectFolders", folder), ("ProjectLinks", link), ("ProjectNotes", note) })
            Exec($"UPDATE {table} SET TrashEntryId = $e WHERE Id = $id;", ("$e", entry), ("$id", id));

        using (var transaction = _connection.BeginTransaction())
        {
            foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
                Db.Exec(_connection, $"UPDATE {table} SET TrashEntryId = NULL WHERE TrashEntryId = $e;", ("$e", entry));
            Db.Exec(_connection, "DELETE FROM ProjectTrashEntries WHERE Id = $e;", ("$e", entry));
            transaction.Commit();
        }

        Assert.Null(Marker("ProjectFolders", folder));
        Assert.Null(Marker("ProjectLinks", link));
        Assert.Null(Marker("ProjectNotes", note));
        Assert.Equal(0, Db.Count(_connection, "ProjectTrashEntries"));
        Assert.Equal(1, Db.ScalarLong(_connection, "SELECT Revision FROM ProjectNotes;")); // the revision trigger never fired
    }

    [Fact]
    public void NestedEntries_StayIndependent_ThroughBothRestores()
    {
        var parent = Folder(_alpha, null, "P");
        var inner = Folder(_alpha, parent, "X");
        var innerNote = Note(_alpha, inner);
        var parentLink = Link(_alpha, parent);

        // Inner X first (X + its note), then P (P + its still-active link only).
        var innerEntry = Entry(_alpha, "Folder", inner, 2);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", innerEntry), ("$id", inner));
        Exec("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", innerEntry), ("$id", innerNote));
        var parentEntry = Entry(_alpha, "Folder", parent, 2);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id AND TrashEntryId IS NULL;", ("$e", parentEntry), ("$id", parent));
        Exec("UPDATE ProjectLinks SET TrashEntryId = $e WHERE FolderId = $id AND TrashEntryId IS NULL;", ("$e", parentEntry), ("$id", parent));

        // Restore P: X keeps its own entry.
        foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
            Exec($"UPDATE {table} SET TrashEntryId = NULL WHERE TrashEntryId = $e;", ("$e", parentEntry));
        Exec("DELETE FROM ProjectTrashEntries WHERE Id = $e;", ("$e", parentEntry));
        Assert.Null(Marker("ProjectFolders", parent));
        Assert.Null(Marker("ProjectLinks", parentLink));
        Assert.Equal(innerEntry, Marker("ProjectFolders", inner));
        Assert.Equal(innerEntry, Marker("ProjectNotes", innerNote));

        foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
            Exec($"UPDATE {table} SET TrashEntryId = NULL WHERE TrashEntryId = $e;", ("$e", innerEntry));
        Exec("DELETE FROM ProjectTrashEntries WHERE Id = $e;", ("$e", innerEntry));
        Assert.Null(Marker("ProjectFolders", inner));
        Assert.Null(Marker("ProjectNotes", innerNote));
        Assert.Equal("ok", Db.ScalarString(_connection, "PRAGMA integrity_check;"));
        Assert.Equal(0, Db.ScalarLong(_connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    // ---- Read-only trashed rows, trashed containers, active-only names --------------------------------------------------

    [Theory]
    [InlineData("ProjectFolders", "Name = 'x', NameKey = 'X'")]
    [InlineData("ProjectFolders", "ParentFolderId = $other")]
    [InlineData("ProjectFolders", "UpdatedAt = 'u'")]
    [InlineData("ProjectLinks", "LinkType = 'File'")]
    [InlineData("ProjectLinks", "Name = 'x'")]
    [InlineData("ProjectLinks", "Target = 'https://y.test/'")]
    [InlineData("ProjectLinks", "FolderId = $other")]
    [InlineData("ProjectLinks", "UpdatedAt = 'u'")]
    [InlineData("ProjectNotes", "Title = 'x', Revision = Revision + 1")]
    [InlineData("ProjectNotes", "Content = 'x', Revision = Revision + 1")]
    [InlineData("ProjectNotes", "FolderId = $other")]
    [InlineData("ProjectNotes", "UpdatedAt = 'u'")]
    public void TrashedRows_AreReadOnly_ExceptTheirMarker(string table, string assignment)
    {
        var other = Folder(_alpha, null, "Other");
        var id = table switch
        {
            "ProjectFolders" => Folder(_alpha, null, "T"),
            "ProjectLinks" => Link(_alpha, null),
            _ => Note(_alpha, null),
        };
        var entry = Entry(_alpha, table switch { "ProjectFolders" => "Folder", "ProjectLinks" => "Link", _ => "Note" }, id);
        Exec($"UPDATE {table} SET TrashEntryId = $e WHERE Id = $id;", ("$e", entry), ("$id", id));

        Assert.Contains("WH_TRASHED", Fails($"UPDATE {table} SET {assignment} WHERE Id = $id;", ("$id", id), ("$other", other)));
    }

    [Fact]
    public void NothingActive_CanBeCreatedInOrMovedIntoATrashedFolder()
    {
        var trashed = Folder(_alpha, null, "Trashed");
        var entry = Entry(_alpha, "Folder", trashed);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", entry), ("$id", trashed));
        var folder = Folder(_alpha, null, "Active");
        var link = Link(_alpha, null);
        var note = Note(_alpha, null);

        Assert.Contains("WH_CONTAINER_TRASHED", Fails(() => Folder(_alpha, trashed, "New")));
        Assert.Contains("WH_CONTAINER_TRASHED", Fails(() => Link(_alpha, trashed)));
        Assert.Contains("WH_CONTAINER_TRASHED", Fails(() => Note(_alpha, trashed)));
        Assert.Contains("WH_CONTAINER_TRASHED", Fails("UPDATE ProjectFolders SET ParentFolderId = $t WHERE Id = $id;", ("$t", trashed), ("$id", folder)));
        Assert.Contains("WH_CONTAINER_TRASHED", Fails("UPDATE ProjectLinks SET FolderId = $t WHERE Id = $id;", ("$t", trashed), ("$id", link)));
        Assert.Contains("WH_CONTAINER_TRASHED", Fails("UPDATE ProjectNotes SET FolderId = $t WHERE Id = $id;", ("$t", trashed), ("$id", note)));
    }

    [Fact]
    public void TrashedFolders_ReserveNoSiblingNames_AtRootOrChildLevel()
    {
        var root = Folder(_alpha, null, "Docs");
        var child = Folder(_alpha, root, "Specs");
        var childEntry = Entry(_alpha, "Folder", child);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", childEntry), ("$id", child));
        Assert.True(Folder(_alpha, root, "SPECS") > 0); // child level: the trashed sibling reserves nothing

        var rootEntry = Entry(_alpha, "Folder", root, 2);
        Exec("UPDATE ProjectFolders SET TrashEntryId = $e WHERE Id = $id;", ("$e", rootEntry), ("$id", root));
        var again = Folder(_alpha, null, "docs");
        Assert.True(again > 0); // root level

        // Clearing the marker while an active sibling holds the name is refused by the unique index (Restore's backstop).
        Assert.Contains("UNIQUE", Fails("UPDATE ProjectFolders SET TrashEntryId = NULL WHERE Id = $id;", ("$id", root)));
        Assert.Contains("UNIQUE", Fails(() => Folder(_alpha, null, "DOCS"))); // active siblings stay unique
    }

    [Fact]
    public void MarkingAndClearing_NeverFireTheNoteRevisionTrigger_OrTouchOtherColumns()
    {
        var note = Note(_alpha, null);
        var before = Db.ScalarString(_connection, "SELECT Title || Content || Revision || CreatedAt || UpdatedAt || coalesce(FolderId, '-') FROM ProjectNotes WHERE Id = $id;", ("$id", note));
        var entry = Entry(_alpha, "Note", note);

        Exec("UPDATE ProjectNotes SET TrashEntryId = $e WHERE Id = $id;", ("$e", entry), ("$id", note));
        Exec("UPDATE ProjectNotes SET TrashEntryId = NULL WHERE Id = $id;", ("$id", note));

        Assert.Equal(before, Db.ScalarString(_connection, "SELECT Title || Content || Revision || CreatedAt || UpdatedAt || coalesce(FolderId, '-') FROM ProjectNotes WHERE Id = $id;", ("$id", note)));
    }
}
