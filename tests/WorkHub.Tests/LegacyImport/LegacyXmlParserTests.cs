using System.Text;
using WorkHub.Core.LegacyImport;
using WorkHub.Infrastructure.LegacyImport;

namespace WorkHub.Tests.LegacyImport;

public sealed class LegacyXmlParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));
    private readonly LegacyXmlParser _parser = new();

    public LegacyXmlParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LegacyParseResult ParseText(string xml)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        return _parser.Parse(path);
    }

    [Fact]
    public void ValidSystemEntry()
    {
        var result = ParseText("""<HubData><SYSTEM><Item SystemCode="4000000375" SystemName="ConSite API" /></SYSTEM></HubData>""");

        Assert.True(result.IsValid);
        var entry = Assert.Single(result.Entries);
        Assert.Equal((LegacySection.System, "4000000375", "ConSite API"), (entry.Section, entry.SystemCode, entry.SystemName));
        Assert.NotNull(entry.LineNumber);
    }

    [Fact]
    public void ValidOracleEntry()
    {
        var entry = Assert.Single(ParseText("""<HubData><ORACLE><Item SystemCode="Oracle_CONSITE" SystemName="CONSITE" /></ORACLE></HubData>""").Entries);

        Assert.Equal((LegacySection.Oracle, "Oracle_CONSITE", "CONSITE"), (entry.Section, entry.SystemCode, entry.SystemName));
    }

    [Fact]
    public void JapaneseNamesAreReadExactly()
    {
        var entry = Assert.Single(ParseText("""<?xml version="1.0" encoding="utf-8"?><HubData><SYSTEM><Item SystemCode="Sub0000001" SystemName="データ連携(ATRAN3)(日本側)" /></SYSTEM></HubData>""").Entries);

        Assert.Equal("データ連携(ATRAN3)(日本側)", entry.SystemName);
    }

    [Fact]
    public void MixedSectionsKeepOrderAndCounts()
    {
        var result = ParseText("""
            <HubData>
              <SYSTEM><Item SystemCode="A1" SystemName="a" /><Item SystemCode="A2" SystemName="b" /></SYSTEM>
              <ORACLE><Item SystemCode="O1" SystemName="o" /></ORACLE>
            </HubData>
            """);

        Assert.Equal(["A1", "A2", "O1"], result.Entries.Select(e => e.SystemCode));
        Assert.Equal(2, result.SystemEntryCount);
        Assert.Equal(1, result.OracleEntryCount);
    }

    [Fact]
    public void DuplicatesAndSameCodeAcrossSections_AreAllReturnedForThePlanner()
    {
        var result = ParseText("""
            <HubData>
              <SYSTEM><Item SystemCode="ABC001" SystemName="Test" /><Item SystemCode="ABC001" SystemName="Test" /></SYSTEM>
              <ORACLE><Item SystemCode="ABC001" SystemName="Different" /></ORACLE>
            </HubData>
            """);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public void MissingAttributesBecomeNullEntries_NotParseErrors()
    {
        var result = ParseText("""<HubData><SYSTEM><Item SystemName="Test" /><Item SystemCode="X1" /></SYSTEM></HubData>""");

        Assert.True(result.IsValid);
        Assert.Null(result.Entries[0].SystemCode);
        Assert.Null(result.Entries[1].SystemName);
    }

    [Fact]
    public void MalformedXml_IsAControlledError()
    {
        var result = ParseText("""<HubData><SYSTEM><Item SystemCode="A" SystemName="B"></SYSTEM></HubData>""");

        Assert.False(result.IsValid);
        Assert.Contains("not valid XML", result.Error);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("""<WorkHub><SYSTEM><Item SystemCode="A" SystemName="B" /></SYSTEM></WorkHub>""")]
    [InlineData("""<hubdata><SYSTEM /></hubdata>""")]
    [InlineData("""<x:HubData xmlns:x="urn:other"><SYSTEM /></x:HubData>""")]
    public void WrongRoot_IsRejected(string xml)
    {
        var result = ParseText(xml);

        Assert.False(result.IsValid);
        Assert.Contains("<HubData>", result.Error);
    }

    [Fact]
    public void EmptyFile_IsRejected()
    {
        Assert.Equal("The file is empty.", ParseText(string.Empty).Error);
    }

    [Fact]
    public void WhitespaceOnlyFile_IsAControlledError()
    {
        Assert.False(ParseText("   \n  ").IsValid);
    }

    [Fact]
    public void NoSections_IsValidWithNothingToImport()
    {
        var result = ParseText("<HubData />");

        Assert.True(result.IsValid);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void UnknownSectionsAndElements_AreIgnoredAndCounted()
    {
        var result = ParseText("""
            <HubData>
              <Settings><Host Value="ignored" /></Settings>
              <SYSTEM><Note>ignored</Note><Item SystemCode="A1" SystemName="a" /></SYSTEM>
              <system><Item SystemCode="LOWER" SystemName="not a known section" /></system>
            </HubData>
            """);

        Assert.True(result.IsValid);
        Assert.Equal(["A1"], result.Entries.Select(e => e.SystemCode));
        Assert.Equal(3, result.IgnoredElements);
    }

    [Fact]
    public void ExternalEntity_IsNeverResolved()
    {
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-CANARY");
        var xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE HubData [ <!ENTITY xxe SYSTEM "file:///{secret.Replace('\\', '/')}"> ]>
            <HubData><SYSTEM><Item SystemCode="A1" SystemName="&xxe;" /></SYSTEM></HubData>
            """;

        var result = ParseText(xml);

        Assert.False(result.IsValid);
        Assert.Contains("DTD", result.Error);
        Assert.Empty(result.Entries);
        Assert.DoesNotContain("CANARY", result.Error);
    }

    [Fact]
    public void EntityExpansionBomb_IsRejected()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE HubData [
              <!ENTITY a "aaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
            ]>
            <HubData><SYSTEM><Item SystemCode="A1" SystemName="&c;" /></SYSTEM></HubData>
            """;

        var result = ParseText(xml);

        Assert.False(result.IsValid);
        Assert.Contains("DTD", result.Error);
    }

    [Fact]
    public void ExternalDtdReference_IsNotFetched()
    {
        var result = ParseText("""<?xml version="1.0"?><!DOCTYPE HubData SYSTEM "http://127.0.0.1:9/evil.dtd"><HubData />""");

        Assert.False(result.IsValid);
        Assert.Contains("DTD", result.Error);
    }

    [Fact]
    public void MissingFile_IsAControlledError()
    {
        Assert.Equal("The file could not be found.", _parser.Parse(Path.Combine(_dir, "nope.xml")).Error);
    }

    [Fact]
    public void KnownLegacyFixture_Has42SystemAnd33OracleEntries()
    {
        var result = _parser.Parse(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyHubData.xml"));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(42, result.SystemEntryCount);
        Assert.Equal(33, result.OracleEntryCount);
        Assert.Equal(0, result.IgnoredElements);
    }
}
