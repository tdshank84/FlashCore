using FlashCore.Abstractions.Models;

namespace FlashCore.Core;

public sealed class DeviceStateMachine
{
    private readonly object _sync = new();
    private DeviceState _state = DeviceState.Disconnected;

    public DeviceState State
    {
        get { lock (_sync) return _state; }
    }

    public void TransitionTo(DeviceState next)
    {
        lock (_sync)
        {
            if (_state == next) return;
            if (!IsAllowed(_state, next))
                throw new InvalidOperationException($"Invalid device state transition: {_state} -> {next}.");
            _state = next;
        }
    }

    public void ForceFaulted()
    {
        lock (_sync)
        {
            if (_state != DeviceState.Disposed) _state = DeviceState.Faulted;
        }
    }

    private static bool IsAllowed(DeviceState current, DeviceState next) => next switch
    {
        DeviceState.Disposed => true,
        DeviceState.Disconnected => current != DeviceState.Disposed,
        DeviceState.Connecting => current is DeviceState.Disconnected or DeviceState.Faulted,
        DeviceState.Connected => current is DeviceState.Connecting or DeviceState.Identified or DeviceState.ProgrammingSession
            or DeviceState.SecurityUnlocked or DeviceState.Verifying or DeviceState.Finalizing or DeviceState.Faulted,
        DeviceState.Identified => current is DeviceState.Connected,
        DeviceState.ProgrammingSession => current is DeviceState.Connected or DeviceState.Identified,
        DeviceState.SecurityUnlocked => current is DeviceState.ProgrammingSession or DeviceState.Connected or DeviceState.Identified,
        DeviceState.Erasing => current is DeviceState.SecurityUnlocked,
        DeviceState.Programming => current is DeviceState.Erasing or DeviceState.SecurityUnlocked or DeviceState.Programming,
        DeviceState.Verifying => current is DeviceState.Connected or DeviceState.Identified or DeviceState.Programming,
        DeviceState.Finalizing => current is DeviceState.Programming or DeviceState.Verifying,
        DeviceState.Faulted => current != DeviceState.Disposed,
        _ => false
    };
}
