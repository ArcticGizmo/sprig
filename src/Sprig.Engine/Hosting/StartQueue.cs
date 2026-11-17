namespace Sprig.Engine.Hosting;

/// <summary>
/// Turns at building and starting, shared by every workspace the host runs, for a machine that struggles when many apps
/// build and start together. With no <see cref="Limit"/> every turn is given at once; with one, that many go at a time
/// and the rest wait, earliest asked first.
/// </summary>
public sealed class StartQueue
{
    readonly Lock _gate = new();
    readonly List<Turn> _waiting = [];
    int _going;
    int? _limit;

    /// <summary>How many may build and start at once; null for no limit. A change applies to turns not yet given.</summary>
    public int? Limit
    {
        get { lock (_gate) return _limit; }
        set
        {
            lock (_gate)
            {
                _limit = value is < 1 ? 1 : value;
                Give();
            }
        }
    }

    public int Going { get { lock (_gate) return _going; } }

    public int Waiting { get { lock (_gate) return _waiting.Count; } }

    /// <summary>A place in the queue, ahead of any waiting turn with a later <paramref name="order"/>. Hand it back with
    /// <see cref="Leave"/> whether or not it was given.</summary>
    /// <param name="order">When it was asked to start, so apps go in the order they were asked for (what they depend on
    /// first) rather than in whichever order they got here.</param>
    public Turn Join(long order)
    {
        lock (_gate)
        {
            var turn = new Turn(order);
            var at = _waiting.FindIndex(t => t.Order > order);
            _waiting.Insert(at < 0 ? _waiting.Count : at, turn);
            Give();
            return turn;
        }
    }

    /// <summary>Done with a turn (it's ready, or settled, or gave up waiting); the next in line gets it. Safe to call
    /// more than once.</summary>
    public void Leave(Turn turn)
    {
        lock (_gate)
        {
            if (turn.Left) return;
            turn.Left = true;
            if (turn.Given.Task.IsCompleted) _going--;
            else _waiting.Remove(turn);
            Give();
        }
    }

    void Give()
    {
        while (_waiting.Count > 0 && (_limit is not { } limit || _going < limit))
        {
            var next = _waiting[0];
            _waiting.RemoveAt(0);
            _going++;
            next.Given.TrySetResult();
        }
    }

    public sealed class Turn(long order)
    {
        internal long Order { get; } = order;
        internal TaskCompletionSource Given { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Left { get; set; }

        /// <summary>Completes once it's this one's turn.</summary>
        public Task Ready => Given.Task;
    }
}
