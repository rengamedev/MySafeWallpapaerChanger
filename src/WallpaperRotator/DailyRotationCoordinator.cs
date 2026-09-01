using System.Text.Json;

namespace WallpaperRotator;

public sealed class RotationState
{
    public DateOnly? LastSuccessfulDailyRotation { get; set; }
}

public sealed class RotationStateStore(AppPaths paths, DiagnosticLog log)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<RotationState> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(paths.State)) return new();
            try
            {
                return JsonSerializer.Deserialize<RotationState>(await File.ReadAllTextAsync(paths.State), JsonOptions) ?? new();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                log.Write("Rotation state was invalid and was reset.", ex);
                return new();
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(RotationState state)
    {
        await gate.WaitAsync();
        try
        {
            paths.EnsureCreated();
            var temp = $"{paths.State}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temp, paths.State, true);
            }
            finally { try { File.Delete(temp); } catch { } }
        }
        finally { gate.Release(); }
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
            state.LastSuccessfulDailyRotation = today;
            await states.SaveAsync(state);
            return true;
        }
        finally { gate.Release(); }
    }
}
