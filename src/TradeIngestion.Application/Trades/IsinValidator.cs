namespace TradeIngestion.Application.Trades;

/// <summary>ISO 6166 ISIN validation: 2-letter country prefix, 9 alphanumeric characters, Luhn check digit.</summary>
public static class IsinValidator
{
    public static bool IsValid(string? isin)
    {
        if (isin is null || isin.Length != 12)
        {
            return false;
        }

        if (!char.IsAsciiLetterUpper(isin[0]) || !char.IsAsciiLetterUpper(isin[1]) || !char.IsAsciiDigit(isin[11]))
        {
            return false;
        }

        // Expand letters to two digits (A=10 ... Z=35), then apply Luhn over the whole digit string.
        var digits = new List<int>(24);
        foreach (var c in isin)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Add(c - '0');
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                var value = c - 'A' + 10;
                digits.Add(value / 10);
                digits.Add(value % 10);
            }
            else
            {
                return false;
            }
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Count - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
