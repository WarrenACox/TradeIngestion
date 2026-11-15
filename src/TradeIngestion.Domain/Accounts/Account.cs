namespace TradeIngestion.Domain.Accounts;

/// <summary>A trading account. <see cref="AccountCode"/> is the business identifier used by upstream systems (e.g. "ACC-001").</summary>
public sealed class Account
{
    private Account()
    {
    }

    public Account(string accountCode, string name, string baseCurrency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCurrency);
        AccountCode = accountCode;
        Name = name;
        BaseCurrency = baseCurrency;
    }

    public int Id { get; private set; }

    public string AccountCode { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string BaseCurrency { get; private set; } = null!;
}
