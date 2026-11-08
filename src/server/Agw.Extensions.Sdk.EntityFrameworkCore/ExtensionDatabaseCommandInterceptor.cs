using System.Data.Common;
using System.Runtime.CompilerServices;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public sealed class ExtensionDatabaseCommandInterceptor : DbCommandInterceptor
{
    private readonly ExtensionDatabaseTransactionInterceptor _transactions;
    private readonly ConditionalWeakTable<DbCommand, IDbContextTransaction> _commandTransactions = new();

    public ExtensionDatabaseCommandInterceptor(ExtensionDatabaseTransactionInterceptor transactions)
    {
        _transactions = transactions;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result
    )
    {
        if (!result.HasResult)
        {
            EnsureTransaction(command, eventData);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        if (!result.HasResult)
        {
            await EnsureTransactionAsync(command, eventData, cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result
    )
    {
        if (!result.HasResult)
        {
            EnsureTransaction(command, eventData);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default
    )
    {
        if (!result.HasResult)
        {
            await EnsureTransactionAsync(command, eventData, cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result
    )
    {
        if (!result.HasResult)
        {
            EnsureTransaction(command, eventData);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (!result.HasResult)
        {
            await EnsureTransactionAsync(command, eventData, cancellationToken);
        }

        return result;
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        CompleteTransaction(command);
        return result;
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteTransactionAsync(command, cancellationToken);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        CompleteTransaction(command);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteTransactionAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult DataReaderDisposing(
        DbCommand command,
        DataReaderDisposingEventData eventData,
        InterceptionResult result
    )
    {
        CompleteTransaction(command);
        return result;
    }

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result
    )
    {
        if (!result.IsSuppressed && _commandTransactions.TryGetValue(command, out _))
        {
            await eventData.DataReader.CloseAsync();
            await CompleteTransactionAsync(command, CancellationToken.None);
            return InterceptionResult.Suppress();
        }
        return result;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
    {
        RollbackTransaction(command);
    }

    public override async Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await RollbackTransactionAsync(command, cancellationToken);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        RollbackTransaction(command);
    }

    public override async Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await RollbackTransactionAsync(command, cancellationToken);
    }

    private void EnsureTransaction(DbCommand command, CommandEventData eventData)
    {
        if (_transactions.IsMigration)
        {
            return;
        }
        if (eventData.Context is not ExtensionDbContext context)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        ValidateCommandSource(eventData);

        if (context.Database.CurrentTransaction is { } current)
        {
            command.Transaction = current.GetDbTransaction();
            _transactions
                .ValidateCurrentAsync(current.GetDbTransaction(), context, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return;
        }

        using var readScope =
            eventData.CommandSource == CommandSource.LinqQuery ? _transactions.EnterReadTransaction() : null;
        var transaction = context.Database.BeginTransaction();
        try
        {
            command.Transaction = transaction.GetDbTransaction();
            _commandTransactions.Add(command, transaction);
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    private async Task EnsureTransactionAsync(
        DbCommand command,
        CommandEventData eventData,
        CancellationToken cancellationToken
    )
    {
        if (_transactions.IsMigration)
        {
            return;
        }
        if (eventData.Context is not ExtensionDbContext context)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        ValidateCommandSource(eventData);

        if (context.Database.CurrentTransaction is { } current)
        {
            command.Transaction = current.GetDbTransaction();
            await _transactions.ValidateCurrentAsync(current.GetDbTransaction(), context, cancellationToken);
            return;
        }

        using var readScope =
            eventData.CommandSource == CommandSource.LinqQuery ? _transactions.EnterReadTransaction() : null;
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            command.Transaction = transaction.GetDbTransaction();
            _commandTransactions.Add(command, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private void CompleteTransaction(DbCommand command)
    {
        if (!_commandTransactions.TryGetValue(command, out var transaction))
        {
            return;
        }

        _commandTransactions.Remove(command);
        try
        {
            transaction.Commit();
        }
        finally
        {
            transaction.Dispose();
        }
    }

    private static void ValidateCommandSource(CommandEventData eventData)
    {
        if (eventData.CommandSource is not (CommandSource.LinqQuery or CommandSource.SaveChanges))
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Extension database commands must use ownership-filtered LINQ and SaveChanges."
            );
    }

    private async Task CompleteTransactionAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!_commandTransactions.TryGetValue(command, out var transaction))
        {
            return;
        }

        _commandTransactions.Remove(command);
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    private void RollbackTransaction(DbCommand command)
    {
        if (!_commandTransactions.TryGetValue(command, out var transaction))
        {
            return;
        }

        _commandTransactions.Remove(command);
        try
        {
            transaction.Rollback();
        }
        finally
        {
            transaction.Dispose();
        }
    }

    private async Task RollbackTransactionAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!_commandTransactions.TryGetValue(command, out var transaction))
        {
            return;
        }

        _commandTransactions.Remove(command);
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }
}
