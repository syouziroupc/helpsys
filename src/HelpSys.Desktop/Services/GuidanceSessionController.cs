namespace HelpSys.Services;

public enum GuidanceSessionState
{
    Idle,
    Capturing,
    Planning,
    Presenting,
    AwaitingUserAction,
    Verifying,
    Clarifying,
    Stopped
}

public sealed class GuidanceSessionController
{
    private readonly object _gate = new();
    private long _generation;
    private GuidanceSessionState _state = GuidanceSessionState.Idle;

    public GuidanceSessionState State
    {
        get { lock (_gate) return _state; }
    }

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    public long BeginOperation(GuidanceSessionState initialState = GuidanceSessionState.Capturing)
    {
        lock (_gate)
        {
            _generation++;
            _state = initialState;
            return _generation;
        }
    }

    public long Invalidate(GuidanceSessionState nextState)
    {
        lock (_gate)
        {
            _generation++;
            _state = nextState;
            return _generation;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate) return generation == _generation;
    }

    public bool TryTransition(long generation, GuidanceSessionState nextState)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;
            if (!IsAllowed(_state, nextState)) return false;
            _state = nextState;
            return true;
        }
    }

    public bool TryTransitionCurrent(GuidanceSessionState nextState)
    {
        lock (_gate)
        {
            if (!IsAllowed(_state, nextState)) return false;
            _state = nextState;
            return true;
        }
    }

    private static bool IsAllowed(GuidanceSessionState from, GuidanceSessionState to)
    {
        if (from == to) return true;
        if (to is GuidanceSessionState.Idle or GuidanceSessionState.Stopped) return true;

        return from switch
        {
            GuidanceSessionState.Idle => to == GuidanceSessionState.Capturing,
            GuidanceSessionState.Capturing => to is GuidanceSessionState.Planning or GuidanceSessionState.Clarifying,
            GuidanceSessionState.Planning => to is GuidanceSessionState.Presenting or GuidanceSessionState.Clarifying or GuidanceSessionState.Capturing,
            GuidanceSessionState.Presenting => to is GuidanceSessionState.AwaitingUserAction or GuidanceSessionState.Clarifying,
            GuidanceSessionState.AwaitingUserAction => to is GuidanceSessionState.Verifying or GuidanceSessionState.Capturing or GuidanceSessionState.Clarifying,
            GuidanceSessionState.Verifying => to is GuidanceSessionState.AwaitingUserAction or GuidanceSessionState.Capturing or GuidanceSessionState.Clarifying,
            GuidanceSessionState.Clarifying => to == GuidanceSessionState.Capturing,
            GuidanceSessionState.Stopped => to == GuidanceSessionState.Capturing,
            _ => false
        };
    }
}
