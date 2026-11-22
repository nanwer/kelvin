namespace Kelvin.Core;

/// <summary>What a fan in target mode is doing, for the UI.</summary>
public sealed record TargetStatus(double Temp, double Target, bool CannotHold);

/// <summary>
/// Target mode for one fan: keep a temperature under a limit with the fan as slow as that allows.
///
/// It is a small PI controller. One part reacts at once to how far the temperature is from the
/// target; the other slowly learns the speed that holds it there. When the PC is cool the learned
/// speed drifts down to the fan's floor, so an idle PC is as quiet as it can be. When a load
/// arrives the fan rises as the temperature nears the target and settles at whatever speed holds
/// it. The gains are deliberately gentle: every PC responds differently to its fans, and a slow,
/// steady controller is quiet on all of them where a fast one would hunt up and down on some.
///
/// It never overrides safety: the critical-temperature override in <see cref="FanEngine"/> still
/// forces full speed, and a lost sensor still hands the fan back to the BIOS.
/// </summary>
public sealed class TargetControl
{
    /// <summary>Aim this far below the target, so "under 70°C" settles just under it, not on it.</summary>
    public const double Margin = 2;
    /// <summary>Immediate response: percent of fan speed per °C away from the aim.</summary>
    public const double Gain = 4;
    /// <summary>How fast the learned speed moves, in percent per second per °C. Up is faster than down.</summary>
    public const double RiseRate = 0.15, FallRate = 0.08;
    /// <summary>At full speed and still over the target for this long means the target can't be held.</summary>
    public const double CannotHoldAfterSeconds = 60;

    private double _learned;
    private bool _started;
    private double _secondsOver;

    /// <summary>True when the fan has been at full speed for a while and the temperature is still over the target.</summary>
    public bool CannotHold => _secondsOver >= CannotHoldAfterSeconds;

    /// <summary>The fan speed (percent) for this moment.</summary>
    /// <param name="temp">The temperature being held down, already averaged over short spikes.</param>
    /// <param name="target">The temperature to stay under.</param>
    /// <param name="floor">The slowest the fan may go.</param>
    /// <param name="current">The speed the fan is at now, used as the starting point.</param>
    /// <param name="dt">Seconds since the last step.</param>
    public double Step(double temp, double target, double floor, double current, double dt)
    {
        floor = Math.Clamp(floor, 0, 100);
        if (!_started)
        {
            _learned = Math.Clamp(double.IsFinite(current) ? current : floor, floor, 100);
            _started = true;
        }

        var error = temp - (target - Margin);
        var output = Math.Clamp(_learned + Gain * error, floor, 100);

        // Learn upwards only while there is still speed to give; otherwise the learned value would
        // keep climbing at full speed and take minutes to come back down afterwards.
        if (error > 0) { if (output < 100) _learned += RiseRate * error * dt; }
        else _learned += FallRate * error * dt;
        _learned = Math.Clamp(_learned, floor, 100);

        output = Math.Clamp(_learned + Gain * error, floor, 100);
        _secondsOver = output >= 100 && temp > target ? _secondsOver + dt : 0;
        return output;
    }
}
