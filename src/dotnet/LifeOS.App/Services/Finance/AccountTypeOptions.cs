namespace LifeOS.App.Services.Finance;

// Account types offered by the UI. Values are the API names; the API remains authoritative for
// which types are valid.
public static class AccountTypeOptions
{
	public static IReadOnlyList<string> All { get; } = ["BankAccount", "Cash", "CreditCard", "Savings", "Other"];

	public const string Default = "BankAccount";

	public static string ItalianLabel(string accountType) => accountType switch
	{
		"BankAccount" => "Conto corrente",
		"Cash" => "Contanti",
		"CreditCard" => "Carta di credito",
		"Savings" => "Risparmio",
		"Other" => "Altro",
		_ => accountType
	};
}
