using Sprig.Engine.Hosting;

namespace Sprig.Tests.Hosting;

public class StartQueueTests
{
    [Fact]
    public void Without_a_limit_every_turn_is_given_at_once()
    {
        var queue = new StartQueue();

        var turns = Enumerable.Range(1, 5).Select(i => queue.Join(i)).ToArray();

        Assert.All(turns, t => Assert.True(t.Ready.IsCompleted));
        Assert.Equal(5, queue.Going);
    }

    [Fact]
    public void One_at_a_time_gives_the_next_turn_only_when_the_last_is_handed_back()
    {
        var queue = new StartQueue { Limit = 1 };

        var first = queue.Join(1);
        var second = queue.Join(2);

        Assert.True(first.Ready.IsCompleted);
        Assert.False(second.Ready.IsCompleted);
        queue.Leave(first);
        Assert.True(second.Ready.IsCompleted);
        Assert.Equal(1, queue.Going);
    }

    [Fact]
    public void Waiting_turns_go_in_the_order_they_were_asked_for_not_the_order_they_joined()
    {
        var queue = new StartQueue { Limit = 1 };
        var going = queue.Join(1);

        var later = queue.Join(7);
        var earlier = queue.Join(3);
        queue.Leave(going);

        Assert.True(earlier.Ready.IsCompleted);
        Assert.False(later.Ready.IsCompleted);
    }

    [Fact]
    public void A_turn_given_up_while_waiting_leaves_the_queue_and_handing_one_back_twice_counts_once()
    {
        var queue = new StartQueue { Limit = 1 };
        var going = queue.Join(1);
        var gaveUp = queue.Join(2);
        var next = queue.Join(3);

        queue.Leave(gaveUp);
        queue.Leave(going);
        queue.Leave(going);

        Assert.False(gaveUp.Ready.IsCompleted);
        Assert.True(next.Ready.IsCompleted);
        Assert.Equal(1, queue.Going);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public void Lifting_the_limit_lets_every_waiting_turn_go()
    {
        var queue = new StartQueue { Limit = 1 };
        queue.Join(1);
        var waiting = new[] { queue.Join(2), queue.Join(3) };

        queue.Limit = null;

        Assert.All(waiting, t => Assert.True(t.Ready.IsCompleted));
    }
}
