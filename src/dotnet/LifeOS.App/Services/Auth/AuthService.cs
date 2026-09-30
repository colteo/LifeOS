using LifeOS.App.Services.Users;
using LifeOS.Contracts.Users;

namespace LifeOS.App.Services.Auth;

public enum AuthState
{
	Restoring,
	SignedOut,

	// Credentials are kept, but LifeOS could not be reached; the user can retry.
	Unreachable,
	Authenticated
}

// Sign-in state of the app: Google sign-in, session restore at startup, sign-out.
// Tokens are handled by TokenSession; this service never logs or displays them.
public sealed class AuthService
{
	public const string CallbackUri = "lifeos://auth";

	private const string UnreachableMessage = "Impossibile contattare LifeOS. Controlla la connessione e riprova.";

	private readonly TokenSession _session;
	private readonly AuthApiClient _authApi;
	private readonly MeApiClient _meApi;
	private readonly ApiSettings _settings;

	public AuthService(TokenSession session, AuthApiClient authApi, MeApiClient meApi, ApiSettings settings)
	{
		_session = session;
		_authApi = authApi;
		_meApi = meApi;
		_settings = settings;

		_session.SessionEnded += reason => SetState(AuthState.SignedOut, null, reason);
	}

	public event Action? StateChanged;

	public AuthState State { get; private set; } = AuthState.Restoring;

	public MeResponse? CurrentUser { get; private set; }

	// A readable message for the current state (cancelled sign-in, expired session, …), if any.
	public string? Message { get; private set; }

	public bool IsOnboardingComplete => CurrentUser?.OnboardingStatus == "Completed";

	// Startup: uses the stored refresh token, if any. Only a server rejection signs the user out;
	// an unreachable server keeps the stored token.
	public async Task RestoreSessionAsync()
	{
		SetState(AuthState.Restoring, null, null);

		switch (await _session.RefreshAsync(failedAccessToken: null))
		{
			case SessionUpdate.Established:
				await LoadProfileAsync();
				break;

			case SessionUpdate.Unavailable:
				SetState(AuthState.Unreachable, null, UnreachableMessage);
				break;

			case SessionUpdate.Ended when State == AuthState.Restoring:
				// Nothing stored (a rejection has already been reported through SessionEnded).
				SetState(AuthState.SignedOut, null, null);
				break;
		}
	}

	// Retry after Unreachable: reload the profile if a session exists, otherwise restore it.
	public Task RetryAsync() =>
		_session.AccessToken is null ? RestoreSessionAsync() : LoadProfileAsync();

	public async Task SignInWithGoogleAsync()
	{
		var verifier = Pkce.CreateVerifier();
		var challenge = Pkce.CreateS256Challenge(verifier);
		var start = new Uri(
			_settings.BrowserBaseAddress,
			$"api/auth/google/start?code_challenge={challenge}&code_challenge_method=S256&redirect_uri={Uri.EscapeDataString(CallbackUri)}");

		WebAuthenticatorResult result;

		try
		{
			result = await MainThread.InvokeOnMainThreadAsync(
				() => WebAuthenticator.Default.AuthenticateAsync(start, new Uri(CallbackUri)));
		}
		catch (TaskCanceledException)
		{
			SetState(AuthState.SignedOut, null, "Accesso annullato.");
			return;
		}
		catch (Exception)
		{
			SetState(AuthState.SignedOut, null, "Impossibile avviare l'accesso con Google.");
			return;
		}

		var code = result.Properties.GetValueOrDefault("code");

		if (string.IsNullOrEmpty(code))
		{
			SetState(AuthState.SignedOut, null, "Accesso con Google non riuscito. Riprova.");
			return;
		}

		var exchange = await _authApi.ExchangeCodeAsync(code, verifier);

		switch (exchange.Status)
		{
			case AuthCallStatus.Rejected:
				SetState(AuthState.SignedOut, null, "Accesso non riuscito. Riprova.");
				return;

			case AuthCallStatus.Unavailable:
				SetState(AuthState.SignedOut, null, UnreachableMessage);
				return;
		}

		if (await _session.EstablishAsync(exchange.Value!) != SessionUpdate.Established)
		{
			// StorageFailed: already reported through SessionEnded.
			return;
		}

		await LoadProfileAsync();
	}

	public const string InvalidSessionMessage = "Stato della sessione non valido. Esci e accedi di nuovo.";

	// Replaces the signed-in user's profile with a fresher /api/me-shaped response (e.g. returned by
	// an onboarding step). Refused (returns false) when not signed in or when the response belongs
	// to another user: that would be an invalid client state, never a reason to switch user.
	public bool SetCurrentUser(MeResponse user)
	{
		if (State != AuthState.Authenticated || CurrentUser is null || CurrentUser.UserId != user.UserId)
		{
			return false;
		}

		SetState(AuthState.Authenticated, user, null);

		return true;
	}

	// Always succeeds locally; the server logout is best effort.
	public async Task SignOutAsync()
	{
		var refreshToken = await _session.ClearAsync();
		SetState(AuthState.SignedOut, null, null);

		if (refreshToken is not null)
		{
			await _authApi.LogoutAsync(refreshToken);
		}
	}

	private async Task LoadProfileAsync()
	{
		var me = await _meApi.GetMeAsync();

		switch (me.Status)
		{
			case AuthCallStatus.Success:
				SetState(AuthState.Authenticated, me.Value, null);
				break;

			case AuthCallStatus.Unavailable:
				// Valid credentials are kept; only the profile could not be loaded.
				SetState(AuthState.Unreachable, null, UnreachableMessage);
				break;

			default:
				// The server refused this session (or the user no longer exists).
				await _session.EndAsync("La sessione non è più valida. Accedi di nuovo.");
				break;
		}
	}

	private void SetState(AuthState state, MeResponse? currentUser, string? message)
	{
		State = state;
		CurrentUser = currentUser;
		Message = message;
		StateChanged?.Invoke();
	}
}
