using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.App.Services.Finance;

// Advisory preview only. A frozen submission's key is retained for exact transport retries.
public sealed class ReconciliationInput
{
    public string ActualBalance { get; set; } = "";
    public string? Note { get; set; }
    private ReconcileAccountRequest? _submitted;
    private Guid _requestId;

    public bool TryPreview(decimal? previousBalance, out decimal difference)
    {
        difference = 0;
        if (previousBalance is null || !DecimalText.TryParse(ActualBalance, true, out var observed)) return false;
        try { difference = observed - previousBalance.Value; return true; }
        catch (OverflowException) { return false; }
    }

    public bool TryBuildRequest(out ReconcileAccountRequest? request, out Guid requestId)
    {
        request = null;
        requestId = Guid.Empty;
        if (!DecimalText.TryParse(ActualBalance, true, out var observed)) return false;
        var normalizedNote = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        var next = new ReconcileAccountRequest(observed, normalizedNote);
        if (_submitted != next)
        {
            _submitted = next;
            _requestId = Guid.CreateVersion7();
        }
        request = _submitted;
        requestId = _requestId;
        return true;
    }
}
