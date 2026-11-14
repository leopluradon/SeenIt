namespace SeenIt.UI.Services;

/// <summary>
/// "Are you sure?" in SeenIt's own dialog. The browser's confirm() can't be relied on in the app's
/// WebView, and looks out of place on the website.
/// </summary>
public class Confirm
{
    public record Request(string Title, string Message, string Yes, bool Danger, TaskCompletionSource<bool> Answer);
    public Request? Current { get; private set; }
    public event Action? Changed;

    public Task<bool> AskAsync(string title, string message, string yes = "OK", bool danger = false)
    {
        Current?.Answer.TrySetResult(false);
        Current = new Request(title, message, yes, danger, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        Changed?.Invoke();
        return Current.Answer.Task;
    }

    public void Answer(bool yes)
    {
        if (Current is not { } r) return;
        Current = null;
        Changed?.Invoke();
        r.Answer.TrySetResult(yes);
    }
}
