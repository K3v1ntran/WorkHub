using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;

namespace WorkHub.Tests.Repositories;

public sealed class RepositoryCatalogTests
{
    [Fact]
    public void Catalog_ContainsExactlyTheFiveBuiltInGroupsInOrder()
    {
        Assert.Equal(
            ["SVN SYSTEM", "SVN ORACLE", "GIT SYSTEM", "GIT ORACLE", "RELEASE"],
            RepositoryCatalog.All.Select(info => info.DisplayName));
    }

    [Fact]
    public void Catalog_CoversEveryEnumValueOnce()
    {
        Assert.Equal(
            Enum.GetValues<RepositoryType>().Order(),
            RepositoryCatalog.All.Select(info => info.Type).Order());
        Assert.Equal(RepositoryCatalog.All.Count, RepositoryCatalog.All.Select(i => i.StorageCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void StorageCodes_RoundTrip()
    {
        foreach (var info in RepositoryCatalog.All)
        {
            Assert.True(RepositoryCatalog.TryFromStorageCode(info.StorageCode, out var type));
            Assert.Equal(info.Type, type);
            Assert.Same(info, RepositoryCatalog.Get(info.Type));
        }

        Assert.False(RepositoryCatalog.TryFromStorageCode("svn_system", out _));
        Assert.False(RepositoryCatalog.TryFromStorageCode(null, out _));
    }

    [Fact]
    public void Catalog_IsReadOnly()
    {
        Assert.False(RepositoryCatalog.All is IList<RepositoryTypeInfo> { IsReadOnly: false });
    }

    [Fact]
    public void EverySettingsSlotExistsForEveryType()
    {
        var settings = new RepositorySettings { Svn = { Host = "svn-host" }, Git = { Host = "git-host" } };
        Assert.All(RepositoryCatalog.All, info =>
        {
            Assert.NotNull(settings.PrefixFor(info.Type));
            Assert.Equal(info.VersionControl == VersionControlKind.Svn ? "svn-host" : "git-host", settings.HostFor(info.Type));
        });
    }

    [Theory]
    [InlineData("  ABC  ", "ABC")]
    [InlineData("GeSL_Mixed", "GeSL_Mixed")]
    [InlineData("a-b.c_1", "a-b.c_1")]
    public void SystemCode_TrimsAndPreservesCase(string raw, string expected)
    {
        Assert.Equal(expected, SystemCode.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A B")]
    [InlineData("A/B")]
    [InlineData("A\\B")]
    [InlineData("A?B")]
    [InlineData("A#B")]
    [InlineData("ÄBC")]
    [InlineData("ＡBC")]
    public void SystemCode_RejectsInvalidValues(string? raw)
    {
        Assert.False(SystemCode.TryNormalize(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SystemCode_RejectsOverlongValues()
    {
        Assert.False(SystemCode.TryNormalize(new string('A', SystemCode.MaxLength + 1), out _, out _));
        Assert.True(SystemCode.TryNormalize(new string('A', SystemCode.MaxLength), out _, out _));
    }
}
