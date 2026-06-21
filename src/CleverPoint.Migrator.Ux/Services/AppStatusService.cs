namespace CleverPoint.Migrator.Ux.Services;

/// <summary>App-wide top status banner state; any screen can push, the layout renders it.</summary>
public class AppStatusService
{
    public event Action? OnChange;
    public StatusBanner? Current { get; private set; }

    public void Show(StatusIntent intent, string message)
    {
        Current = new StatusBanner(intent, message);
        OnChange?.Invoke();
    }

    public void Clear()
    {
        Current = null;
        OnChange?.Invoke();
    }
}

public enum StatusIntent { Info, Success, Warning, Error }

public record StatusBanner(StatusIntent Intent, string Message);
