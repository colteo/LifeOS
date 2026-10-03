namespace LifeOS.App.Services;

// Finite upper bounds for LifeOS API calls (Debug and Release). A timeout ends in the existing
// "unable to reach LifeOS" / Retry state; there is no automatic retry.
public static class ApiTimeouts
{
	// Every LifeOS API call. The Production API (Render Free) sleeps after 15 idle minutes; waking it,
	// plus the database waking, can take about a minute.
	public static readonly TimeSpan Default = TimeSpan.FromSeconds(90);

	// PROD-AI-001: only the Nutrition calls that reach the AI service (Estimate, Analyze day, lazy close).
	// Two sequential Render Free cold starts can stack: the API wakes (about 60 s), then waits up to its
	// NutritionAi:TimeoutSeconds (Production: 120 s) for the AI service to wake and Groq to answer. 210 s
	// lets the API's own clean "unavailable" answer arrive before the app gives up.
	public static readonly TimeSpan NutritionAi = TimeSpan.FromSeconds(210);
}
