using System.Text.Json.Nodes;
using WorkHub.Core.Settings;

namespace WorkHub.Infrastructure.Settings;

/// <summary>
/// Converts older settings.json layouts to the current one, on the JSON tree before deserialization.
/// <para>
/// v1 → v2: v1 had <c>repositories.{svnSystem,svnOracle,gitSystem,gitOracle,release}.{baseUrl,prefix}</c> and
/// <c>repositories.tortoiseProcPath</c>. v2 has one host per tool: <c>repository.svn</c> / <c>repository.git</c>.
/// The first non-empty v1 baseUrl of each tool becomes its host. Values absent in v1 keep v2 defaults.
/// </para>
/// </summary>
internal static class SettingsUpgrader
{
    /// <returns>The version upgraded from, or null when the tree was already current.</returns>
    public static int? UpgradeInPlace(JsonObject root)
    {
        var version = root["settingsVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : (int?)null;
        if (version >= AppSettings.CurrentSettingsVersion || root["repositories"] is not JsonObject v1)
            return null;

        var svn = new JsonObject();
        Set(svn, "host", FirstNonEmpty(v1, "svnSystem", "svnOracle", "release"));
        Set(svn, "systemPrefix", Prefix(v1, "svnSystem"));
        Set(svn, "oraclePrefix", Prefix(v1, "svnOracle"));
        Set(svn, "releasePrefix", Prefix(v1, "release"));
        Set(svn, "tortoiseProcPath", Text(v1["tortoiseProcPath"]));

        var git = new JsonObject();
        Set(git, "host", FirstNonEmpty(v1, "gitSystem", "gitOracle"));
        Set(git, "systemPrefix", Prefix(v1, "gitSystem"));
        Set(git, "oraclePrefix", Prefix(v1, "gitOracle"));

        root.Remove("repositories");
        root["repository"] = new JsonObject { ["svn"] = svn, ["git"] = git };
        root["settingsVersion"] = AppSettings.CurrentSettingsVersion;
        return version ?? 1;
    }

    private static string? FirstNonEmpty(JsonObject v1, params string[] sections) =>
        sections.Select(s => Text((v1[s] as JsonObject)?["baseUrl"])).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Prefix(JsonObject v1, string section) => Text((v1[section] as JsonObject)?["prefix"]);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void Set(JsonObject target, string name, string? value)
    {
        if (value is not null)
            target[name] = value;
    }
}
