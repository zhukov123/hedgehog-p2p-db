using Hedgehog.Metadata.Sqlite;
using Microsoft.Data.Sqlite;

namespace Hedgehog.Head;

public sealed record OutboxDispatchOptions(
    string WorkerId,
    TimeSpan LeaseDuration,
    int MaxItems,
    string? DestinationNodeId = null,
    string? Topic = null);

public sealed record OutboxDispatchMessage(
    string OutboxId,
    string Workflow,
    string? DestinationNodeId,
    string Topic,
    byte[] Payload,
    string IdempotencyKey,
    int AttemptCount);

public sealed record OutboxDispatchResult(
    int Claimed,
    int Delivered,
    int Failed);

public interface IOutboxPublisher
{
    Task PublishAsync(OutboxDispatchMessage message, CancellationToken cancellationToken = default);
}

public sealed class HeadOutboxDispatcher
{
    private readonly SqliteConnection connection;
    private readonly ISqliteMetadataWorkflowStore workflowStore;
    private readonly IOutboxPublisher publisher;
    private readonly TimeProvider timeProvider;

    public HeadOutboxDispatcher(
        SqliteConnection connection,
        ISqliteMetadataWorkflowStore workflowStore,
        IOutboxPublisher publisher,
        TimeProvider? timeProvider = null)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OutboxDispatchResult> DispatchOnceAsync(
        OutboxDispatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorkerId))
        {
            throw new ArgumentException("Worker id is required.", nameof(options));
        }

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Lease duration must be positive.");
        }

        if (options.MaxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max items must be positive.");
        }

        var claimed = await workflowStore.ClaimOutboxAsync(
            connection,
            new SqliteClaimOutboxRequest(
                options.WorkerId,
                timeProvider.GetUtcNow(),
                options.LeaseDuration,
                options.MaxItems,
                options.DestinationNodeId,
                options.Topic),
            cancellationToken).ConfigureAwait(false);

        var delivered = 0;
        var failed = 0;
        foreach (var item in claimed.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await publisher.PublishAsync(
                    new OutboxDispatchMessage(
                        item.OutboxId,
                        item.Workflow,
                        item.DestinationNodeId,
                        item.Topic,
                        item.Payload,
                        item.IdempotencyKey,
                        item.AttemptCount),
                    cancellationToken).ConfigureAwait(false);

                var acknowledged = await workflowStore.AcknowledgeOutboxAsync(
                    connection,
                    new SqliteAcknowledgeOutboxRequest(
                        item.OutboxId,
                        options.WorkerId,
                        item.ClaimedUntil,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);

                if (acknowledged.Delivered)
                {
                    delivered++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }

        return new OutboxDispatchResult(claimed.Events.Count, delivered, failed);
    }
}
