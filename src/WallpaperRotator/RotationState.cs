using System.Text.Json;

namespace WallpaperRotator;

public sealed class RotationState
{
    public DateOnly? LastSuccessfulDailyRotation { get; set; }
    public DateTimeOffset? LastSuccessfulRotation { get; set; }
    public List<string> BlockedIds { get; set; } = [];
}

public sealed class RotationStateStore(AppPaths paths, DiagnosticLog log)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<RotationState> LoadAsync()
    {
        await gate.WaitAsync();
        try { return await LoadUnsafeAsync(); }
        finally { gate.Release(); }
    }

    /// <summary>Reads, changes and writes the state under one lock so concurrent updates never overwrite each other.</summary>
    public async Task<RotationState> UpdateAsync(Action<RotationState> update)
    {
        await gate.WaitAsync();
        try
        {
            var state = await LoadUnsafeAsync();
            update(state);
            paths.EnsureCreated();
            await AtomicFile.WriteAllTextAsync(paths.State, JsonSerializer.Serialize(state, JsonOptions));
            return state;
        }
        finally { gate.Release(); }
    }

    private async Task<RotationState> LoadUnsafeAsync()
    {
        if (!File.Exists(paths.State)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<RotationState>(await File.ReadAllTextAsync(paths.State), JsonOptions) ?? new();
            state.BlockedIds ??= [];
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write("Rotation state was invalid and was reset.", ex);
            return new();
        }
    }
}

public sealed class DailyRotationCoordinator(RotationStateStore states, Func<DateTime> now)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> TryRunAsync(bool paused, Func<CancellationToken, Task<bool>> rotate, CancellationToken cancellationToken = default)
    {
        if (paused || !await gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            var today = DateOnly.FromDateTime(now());
            var state = await states.LoadAsync();
            if (state.LastSuccessfulDailyRotation == today) return false;
            if (!await rotate(cancellationToken)) return false;
            await states.UpdateAsync(s => s.LastSuccessfulDailyRotation = today);
            return true;
        }
        finally { gate.Release(); }
    }
}

public static class RotationTiming
{
    /// <summary>A failed automatic rotation is retried after this delay instead of waiting for the full interval.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>How often the daily schedule checks for a new calendar day while the computer stays awake.</summary>
    public static readonly TimeSpan DailyCheckInterval = TimeSpan.FromMinutes(15);

    public static TimeSpan GetIntervalDelay(DateTimeOffset? lastSuccessfulRotation, TimeSpan interval, DateTimeOffset now)
    {
        if (lastSuccessfulRotation is null) return TimeSpan.Zero;
        var remaining = lastSuccessfulRotation.Value + interval - now;
        if (remaining < TimeSpan.Zero) return TimeSpan.Zero;
        // A clock moved backwards must not postpone the next rotation beyond one interval.
        return remaining > interval ? interval : remaining;
    }
}
