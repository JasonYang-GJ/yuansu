namespace ScreenGuide.Skills.Windows;

/// <summary>
/// Waits only for the receipt of one already-issued focus action. Each read must
/// revalidate the same bound input; this helper never invokes or focuses a control.
/// </summary>
internal static class BoundSearchFocusWait
{
    internal static bool Run(Func<bool> readIsFocused, Action validateBoundary,
        TimeProvider? clock = null, Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(readIsFocused);
        ArgumentNullException.ThrowIfNull(validateBoundary);
        clock ??= TimeProvider.System;
        wait ??= Thread.Sleep;
        var started = clock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(1);
        var interval = TimeSpan.FromMilliseconds(25);

        // Synchronous COM calls cannot be interrupted here. Their elapsed time and
        // boundary checks consume this same deadline; a late result cannot succeed.
        while (clock.GetElapsedTime(started) < budget)
        {
            validateBoundary();
            if (clock.GetElapsedTime(started) >= budget) return false;
            var focused = readIsFocused();
            validateBoundary();
            var remaining = budget - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;
            if (focused) return true;
            wait(remaining < interval ? remaining : interval);
        }

        return false;
    }
}
