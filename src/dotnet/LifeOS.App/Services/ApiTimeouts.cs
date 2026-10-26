namespace LifeOS.App.Services;

// Finite upper bounds for LifeOS API calls (Debug and Release). A timeout ends in the existing
// "unable to reach LifeOS" / Retry state; there is no automatic retry.
public static class ApiTimeouts
{
	// Every LifeOS API call. The Production API (Render Free) sleeps after 15 idle minutes; waking it,
	// plus the database waking, can take about a minute.
	public static readonly TimeSpan Default = TimeSpan.FromSeconds(90);

	// PROD-AI-001: only the calls that reach the AI service: Nutrition Estimate, Analyze day and lazy
	// close, (AI-001) generating a weekly review's AI Insights and (AI-002) asking for a suggested action.
	// Two sequential Render Free cold starts can stack: the API wakes (about 60 s), then waits up to its
	// NutritionAi:TimeoutSeconds (Production: 120 s) for the AI service to wake and Groq to answer. 210 s
	// lets the API's own clean "unavailable" answer arrive before the app gives up.
	public static readonly TimeSpan NutritionAi = TimeSpan.FromSeconds(210);

	// JRN-003: only the Journal Memory calls (Ask Journal: status, bounded sync, ask). Ask is two bounded
	// API → AI-service calls (query embedding, then the answer: up to 120 s each in Production) after a
	// possible API wake (about 60 s); one sync of 3 entries is at most 3 embedding requests (about 53 s
	// each). AI-006.1 §6: clients must allow about 300 s.
	public static readonly TimeSpan JournalMemory = TimeSpan.FromSeconds(300);

	// AI-007 F4: only LifeOS Assistant send and retry, which wait for the assistant's turn. The API may need to
	// wake (about 60 s), then bounds the AI service call by AssistantAi:TimeoutSeconds (at most 75 s, inside
	// its 90 s turn lease) and stores the outcome. A timeout here leaves the message pending: sending it again
	// reuses its key, and the stored turn is shown after a reload.
	public static readonly TimeSpan Assistant = TimeSpan.FromSeconds(180);
}
