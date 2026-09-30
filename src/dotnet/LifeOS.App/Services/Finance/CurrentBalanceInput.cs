using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.App.Services.Finance;

// The optional "current balance" of a new account (ADR-007): the balance is true at an exact instant.
// Shared by the onboarding first-account step and the Accounts page. Plain .NET, no MAUI.
public sealed class CurrentBalanceInput
{
	// The date/time field starts at the current local minute. Left unchanged, it means "right now":
	// the exact instant of submitting is sent, never the start of the minute or of the day.
	private DateTime defaultAtLocal;

	public CurrentBalanceInput(DateTime localNow)
	{
		Reset(localNow);
	}

	public bool Enabled { get; set; }

	// Signed: a debt (e.g. a credit card) is negative. Zero is valid.
	public string AmountText { get; set; } = string.Empty;

	// Local wall-clock date and time at which the balance is true.
	public DateTime AtLocal { get; set; }

	// Back to unchecked and empty, with "now" as the new default time.
	public void Reset(DateTime localNow)
	{
		Enabled = false;
		AmountText = string.Empty;
		defaultAtLocal = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, 0, localNow.Kind);
		AtLocal = defaultAtLocal;
	}

	// Null request (and true) when disabled. The API remains authoritative for range, precision
	// and its clock-skew allowance.
	public bool TryBuildRequest(DateTimeOffset utcNow, TimeZoneInfo timeZone, out OpeningBalanceRequest? request, out string? error)
	{
		request = null;
		error = null;

		if (!Enabled)
		{
			return true;
		}

		if (!DecimalText.TryParse(AmountText, allowNegative: true, out var amount))
		{
			error = "Enter a valid current balance, for example 1250.00 or -350.00.";
			return false;
		}

		DateTimeOffset asOfUtc;

		if (AtLocal == defaultAtLocal)
		{
			asOfUtc = utcNow;
		}
		else if (!LocalDateTimeConverter.TryToUtc(AtLocal, timeZone, out asOfUtc, out error))
		{
			return false;
		}

		if (asOfUtc > utcNow)
		{
			error = "The balance date and time cannot be in the future.";
			return false;
		}

		request = new OpeningBalanceRequest(amount, asOfUtc);

		return true;
	}
}
