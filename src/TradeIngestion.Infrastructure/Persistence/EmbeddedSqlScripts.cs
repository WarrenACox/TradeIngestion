using System.Text.RegularExpressions;

namespace TradeIngestion.Infrastructure.Persistence;

/// <summary>Access to the SQL artifacts in /Database/Scripts, which are embedded into this assembly at build time.</summary>
public static partial class EmbeddedSqlScripts
{
    public const string GetPortfolioSnapshot = "GetPortfolioSnapshot.sql";
    public const string SeedData = "SeedData.sql";

    public static string Read(string scriptName)
    {
        var resourceName = $"TradeIngestion.Scripts.{scriptName}";
        using var stream = typeof(EmbeddedSqlScripts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL script '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Splits a script into batches on lines containing only "GO" (the sqlcmd / SSMS batch separator).</summary>
    public static IReadOnlyList<string> SplitBatches(string script) =>
        BatchSeparator().Split(script)
            .Where(batch => !string.IsNullOrWhiteSpace(batch))
            .ToList();

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatchSeparator();
}
