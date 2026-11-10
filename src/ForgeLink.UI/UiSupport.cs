using System.Globalization;
using ForgeLink.Contracts;
using Microsoft.AspNetCore.Components;

namespace ForgeLink.UI;

/// <summary>Marker for locating this assembly (router AdditionalAssemblies).</summary>
public sealed class UiAssembly;

/// <summary>Location of the shared icon sprite, relative to the document base href (works in Blazor Server and BlazorWebView).</summary>
public static class IconSprite
{
    public const string Path = "_content/ForgeLink.UI/icons.svg";
}

public enum NoticeKind
{
    Error,
    Info,
    Success,
}

public sealed record Notice(string Message, NoticeKind Kind)
{
    public string CssClass => Kind switch
    {
        NoticeKind.Error => "fl-banner fl-banner-error",
        NoticeKind.Info => "fl-banner fl-banner-info",
        _ => "fl-banner fl-banner-success",
    };

    /// <summary>Queued-offline is good news for an operator: the work is saved and will sync.</summary>
    public static Notice From(Exception ex) => ex is QueuedForSyncException q
        ? new Notice(q.Message, NoticeKind.Info)
        : new Notice(ex.Message, NoticeKind.Error);

    public static Notice Ok(string message) => new(message, NoticeKind.Success);
}

public static class Format
{
    public static string Pct(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string Qty(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture);

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var age = DateTime.UtcNow - utc.Value;
        return age.TotalSeconds < 60 ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
    }

    public static string StateClass(string state) => "fl-state-" + state.ToLowerInvariant();

    public static string OeeClass(double oee) => oee >= 0.85 ? "fl-good" : oee >= 0.6 ? "fl-warn" : "fl-bad";

    /// <summary>Plain-language band for OEE so the colour is never the only signal.</summary>
    public static string OeeLabel(double oee) => oee >= 0.85 ? "World class" : oee >= 0.6 ? "Typical" : "Below target";

    /// <summary>CSS width for a 0..1 fraction, clamped and culture-invariant.</summary>
    public static string Width(double fraction) => (Math.Clamp(fraction, 0, 1) * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var local = name.Split('@')[0];
        var parts = local.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0])),
        };
    }
}

/// <summary>
/// Base for live views: loads once, then refreshes on a timer. Polling (rather than a push channel) keeps the same
/// component working in Blazor Server and on a handheld whose Wi-Fi drops in and out of the racking.
/// </summary>
public abstract class PollingComponent : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    [Inject] protected IForgeLinkApi Api { get; set; } = default!;

    [Parameter] public int RefreshSeconds { get; set; } = 10;

    protected Notice? LoadError { get; private set; }
    protected bool Loaded { get; private set; }
    protected DateTime? LastRefreshUtc { get; private set; }

    protected abstract Task LoadAsync(CancellationToken ct);

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
        _ = PollAsync(_cts.Token);
    }

    protected async Task RefreshAsync()
    {
        try
        {
            await LoadAsync(_cts.Token);
            LoadError = null;
            LastRefreshUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LoadError = new Notice($"Live data unavailable: {ex.Message}", NoticeKind.Error);
        }
        Loaded = true;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, RefreshSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
