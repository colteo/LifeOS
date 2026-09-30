using System.Globalization;

namespace LifeOS.App.Services.Finance;

// Parses amounts typed on the phone: "12,50" or "12.50" (exactly one decimal separator, either
// one), integers such as "12", no thousands separators. With allowNegative, a single leading "-"
// is accepted ("-350,00"). Range and decimal places are validated by the API. Plain .NET.
public static class DecimalText
{
	public static bool TryParse(string? text, bool allowNegative, out decimal value)
	{
		value = 0;

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var normalized = text.Trim().Replace(',', '.');
		var negative = allowNegative && normalized.StartsWith('-');

		if (negative)
		{
			normalized = normalized[1..];
		}

		if (normalized.Count(character => character == '.') > 1
			|| !decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
		{
			value = 0;
			return false;
		}

		if (negative)
		{
			value = -value;
		}

		return true;
	}
}
