using Agw.Agents.Execution.Steering;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class SteerInboxTests
{
    [Fact]
    public void Constructor_NonPositiveLimit_Throws()
    {
        var exception = Assert.Throws<AgwException>(() => new SteerInbox(0));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Offer_NewSteer_IsAcceptedAndTakenOnce()
    {
        var inbox = new SteerInbox(8);
        var steerId = Guid.CreateVersion7();

        Assert.Equal(SteerOfferResult.Accepted, inbox.Offer(steerId, Input("first")));

        var taken = Assert.Single(inbox.TakePending());
        Assert.Equal(steerId, taken.SteerId);
        Assert.Empty(inbox.TakePending());
    }

    [Fact]
    public void Offer_SameSteerIdAgain_IsDuplicateAndNotTakenTwice()
    {
        var inbox = new SteerInbox(8);
        var steerId = Guid.CreateVersion7();
        inbox.Offer(steerId, Input("first"));

        Assert.Equal(SteerOfferResult.Duplicate, inbox.Offer(steerId, Input("first")));

        Assert.Single(inbox.TakePending());
        Assert.Equal(SteerOfferResult.Duplicate, inbox.Offer(steerId, Input("first")));
        Assert.Empty(inbox.TakePending());
    }

    [Fact]
    public void Offer_AtLimit_ReturnsLimitExceededUntilTaken()
    {
        var inbox = new SteerInbox(2);
        inbox.Offer(Guid.CreateVersion7(), Input("1"));
        inbox.Offer(Guid.CreateVersion7(), Input("2"));

        Assert.Equal(SteerOfferResult.LimitExceeded, inbox.Offer(Guid.CreateVersion7(), Input("3")));

        inbox.TakePending();
        Assert.Equal(SteerOfferResult.Accepted, inbox.Offer(Guid.CreateVersion7(), Input("3")));
    }

    [Fact]
    public void TakePendingOrClose_Empty_ClosesInbox()
    {
        var inbox = new SteerInbox(8);

        Assert.Empty(inbox.TakePendingOrClose());

        Assert.Equal(SteerOfferResult.Closed, inbox.Offer(Guid.CreateVersion7(), Input("late")));
    }

    [Fact]
    public void TakePendingOrClose_WithPending_KeepsInboxOpen()
    {
        var inbox = new SteerInbox(8);
        inbox.Offer(Guid.CreateVersion7(), Input("1"));

        Assert.Single(inbox.TakePendingOrClose());

        Assert.Equal(SteerOfferResult.Accepted, inbox.Offer(Guid.CreateVersion7(), Input("2")));
    }

    [Fact]
    public void Close_ReturnsPendingAndRejectsLaterOffers()
    {
        var inbox = new SteerInbox(8);
        var steerId = Guid.CreateVersion7();
        inbox.Offer(steerId, Input("1"));

        var returned = Assert.Single(inbox.Close());

        Assert.Equal(steerId, returned.SteerId);
        Assert.Empty(inbox.TakePending());
        Assert.Equal(SteerOfferResult.Closed, inbox.Offer(Guid.CreateVersion7(), Input("late")));
    }

    [Fact]
    public async Task ConcurrentOfferTakeAndClose_EachAcceptedSteerHasExactlyOneOutcome()
    {
        const int attempts = 200;
        for (var round = 0; round < 20; round++)
        {
            var inbox = new SteerInbox(attempts);
            var accepted = new List<Guid>();
            var delivered = new List<Guid>();
            var returned = new List<Guid>();
            var acceptedLock = new Lock();

            var producers = Enumerable
                .Range(0, attempts)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        var steerId = Guid.CreateVersion7();
                        if (inbox.Offer(steerId, Input("x")) == SteerOfferResult.Accepted)
                        {
                            lock (acceptedLock)
                                accepted.Add(steerId);
                        }
                    })
                )
                .ToList();
            var taker = Task.Run(
                () =>
                {
                    for (var i = 0; i < 50; i++)
                    {
                        foreach (var entry in inbox.TakePending())
                            delivered.Add(entry.SteerId);
                        Thread.Yield();
                    }
                },
                TestContext.Current.CancellationToken
            );
            var closer = Task.Run(
                () =>
                {
                    Thread.Sleep(1);
                    foreach (var entry in inbox.Close())
                        returned.Add(entry.SteerId);
                },
                TestContext.Current.CancellationToken
            );

            await Task.WhenAll([.. producers, taker, closer]);

            Assert.Equal(accepted.Count, delivered.Count + returned.Count);
            Assert.Empty(delivered.Intersect(returned));
            Assert.Equal(accepted.Order().ToList(), delivered.Concat(returned).Order().ToList());
        }
    }

    private static AgwUserInput Input(string text) => new() { Contents = [new AgwTextContent { Content = text }] };
}
