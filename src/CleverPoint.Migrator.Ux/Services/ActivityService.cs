namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// App-wide activity state shown in the footer. Idle shows "Ready"; during a
/// run the wizard sets a live message so the footer reflects what's happening.
/// </summary>
public class ActivityService
{
    public event Action? OnChange;

    public bool IsBusy { get; private set; }
    public string Message { get; private set; } = "Ready";

    public void SetBusy(string message)
    {
        IsBusy = true;
        Message = message;
        OnChange?.Invoke();
    }

    public void SetIdle(string message = "Ready")
    {
        IsBusy = false;
        Message = message;
        OnChange?.Invoke();
    }
}
