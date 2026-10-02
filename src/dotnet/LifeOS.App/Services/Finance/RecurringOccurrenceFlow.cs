using System.Globalization;
using LifeOS.Contracts.Finance.Recurring;

namespace LifeOS.App.Services.Finance;

// Presentation state shared by both recurring surfaces. The API decides financial validity.
public sealed class RecurringOccurrenceFlow(RecurringOccurrenceResponse occurrence)
{
    public RecurringOccurrenceResponse Occurrence { get; } = occurrence;
    public bool CanConfirm => !Completed && Occurrence.Status == "Due";
    public bool CanSkip => !Completed && Occurrence.Status is "Due" or "Projected";
    public bool Reviewing { get; private set; }
    public bool Busy { get; private set; }
    public bool Completed { get; private set; }
    public string? Error { get; private set; }
    public string Amount { get; set; } = "";
    public string LocalDateTime { get; set; } = "";
    public string Note { get; set; } = "";

    public void Review()
    {
        if (Busy || !CanConfirm) return;
        Amount = Occurrence.ExpectedAmount.ToString("0.####", CultureInfo.InvariantCulture);
        LocalDateTime = Occurrence.ScheduledDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T12:00";
        Note = Occurrence.Note ?? "";
        Error = null; Reviewing = true;
    }

    public void Cancel() { if (!Busy) { Reviewing = false; Error = null; } }

    public bool TryConfirmation(TimeZoneInfo zone, out ConfirmRecurringRequest? request)
    {
        request = null; Error = null;
        if (!DecimalText.TryParse(Amount, false, out var amount) || amount <= 0)
        { Error = "Enter a positive actual amount."; return false; }
        // HTML datetime-local may return minutes, seconds or fractional seconds. No zone is
        // embedded: convert the entered date using its own zone offset, not today's offset.
        string[] formats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if (!DateTime.TryParseExact(LocalDateTime, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        { Error = "Enter a valid local transaction date and time."; return false; }
        if (!LocalDateTimeConverter.TryToUtc(local, zone, out var utc, out var error))
        { Error = error; return false; }
        request = new(amount, Note, utc); return true;
    }

    public Task<bool> ConfirmAsync(RecurringApiClient api, TimeZoneInfo zone)
    {
        if (Busy || !Reviewing || !CanConfirm || !TryConfirmation(zone, out var request)) return Task.FromResult(false);
        return SubmitAsync(() => api.ActAsync(Occurrence, "confirm", request));
    }

    public Task<bool> SkipAsync(RecurringApiClient api) => Busy || !CanSkip
        ? Task.FromResult(false) : SubmitAsync(() => api.ActAsync(Occurrence, "skip"));

    private async Task<bool> SubmitAsync(Func<Task<ApiResult<RecurringActionResponse>>> send)
    {
        Busy = true; Error = null;
        try
        {
            var result = await send();
            if (!result.IsSuccess) { Error = string.Join(" ", result.Errors); return false; }
            Completed = true; Reviewing = false; return true;
        }
        catch (Exception)
        {
            // Last-resort UI boundary: an unexpected client failure must not strand the button.
            Error = "The recurring action could not finish. Try again; confirmation retries do not create duplicates.";
            return false;
        }
        finally { Busy = false; }
    }

    public void RefreshFailed() => Error = "The movement was saved, but this view could not refresh. Retry refresh.";
}
