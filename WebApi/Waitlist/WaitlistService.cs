using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using WebApi.Data;
using WebApi.Time;

namespace WebApi.Waitlist;

public class WaitlistService
{
    public const string Scope = "waitlist";

    private const string WaitingStatus = "waiting";
    private const string CancelledStatus = "cancelled";
    private const string EnrolledStatus = "enrolled";
    private const string WaitlistConstraint = "IX_waitlist_entries_user_id_class_id";

    private readonly IUnitOfWork _unitOfWork;
    private readonly JsonSerializerOptions _json;

    public WaitlistService(IUnitOfWork unitOfWork, IOptions<JsonOptions> jsonOptions)
    {
        _unitOfWork = unitOfWork;
        _json = jsonOptions.Value.JsonSerializerOptions;
    }

    public async Task<WaitlistJoinResult> JoinAsync(
        long userId,
        string? idempotencyKey,
        JoinWaitlistRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || request.Seats < 1)
        {
            return WaitlistJoinResult.BadRequest();
        }

        var keyHash = Sha256(idempotencyKey);
        var requestHash = Sha256($"{request.ClassId}:{request.Seats}");
        var existing = await _unitOfWork.Idempotency.FindAsync(
            userId,
            Scope,
            keyHash,
            idempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            return FromExisting(existing, requestHash);
        }

        try
        {
            return await UnitOfWorkTransaction.ExecuteAsync(
                _unitOfWork,
                token => JoinAsync(userId, idempotencyKey, keyHash, requestHash, request, token),
                cancellationToken);
        }
        catch (DbUpdateException exception) when (FindPostgres(exception) is { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            _unitOfWork.DbContext.ChangeTracker.Clear();

            if (postgres.ConstraintName == WaitlistConstraint)
            {
                return WaitlistJoinResult.Conflict("already-waiting");
            }

            var winner = await _unitOfWork.Idempotency.FindAsync(
                userId,
                Scope,
                keyHash,
                idempotencyKey,
                cancellationToken);

            if (winner is not null)
            {
                return FromExisting(winner, requestHash);
            }

            throw;
        }
    }

    public async Task<WaitlistLeaveResult> LeaveAsync(
        long userId,
        long entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await _unitOfWork.Waitlist.GetByIdIgnoringFiltersAsync(entryId, cancellationToken);

        if (entry is null || entry.UserId != userId)
        {
            return WaitlistLeaveResult.NotFound();
        }

        // решение: запись со статусом enrolled для этого маршрута — 404.
        // Отмена брони и выход из очереди остаются разными маршрутами.
        if (entry.Status == EnrolledStatus)
        {
            return WaitlistLeaveResult.NotFound();
        }

        if (entry.Status == CancelledStatus)
        {
            return WaitlistLeaveResult.Ok(new WaitlistLeftResponse { Id = entry.Id, Status = CancelledStatus });
        }

        return await UnitOfWorkTransaction.ExecuteAsync(
            _unitOfWork,
            token => LeaveAsync(userId, entry, token),
            cancellationToken);
    }

    private async Task<TransactionOutcome<WaitlistJoinResult>> JoinAsync(
        long userId,
        string idempotencyKey,
        string keyHash,
        string requestHash,
        JoinWaitlistRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await _unitOfWork.Idempotency.FindAsync(
            userId,
            Scope,
            keyHash,
            idempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            return Rollback(FromExisting(existing, requestHash));
        }

        var scheduled = await _unitOfWork.Class.LockByIdAsync(request.ClassId, cancellationToken);

        if (scheduled is null)
        {
            return Rollback(WaitlistJoinResult.NotFound());
        }

        var now = DateTime.UtcNow;

        // решение: несколько причин проверяются в порядке class-cancelled, class-started,
        // seats-available, already-waiting.
        if (scheduled.IsCancelled)
        {
            return Rollback(WaitlistJoinResult.Conflict("class-cancelled"));
        }

        if (scheduled.StartsAt <= now)
        {
            return Rollback(WaitlistJoinResult.Conflict("class-started"));
        }

        var taken = await _unitOfWork.Enrollment.SumActiveSeatsAsync(scheduled.Id, cancellationToken);

        if (scheduled.Capacity - taken >= request.Seats)
        {
            return Rollback(WaitlistJoinResult.Conflict("seats-available"));
        }

        if (await _unitOfWork.Waitlist.FindWaitingAsync(userId, scheduled.Id, cancellationToken) is not null)
        {
            return Rollback(WaitlistJoinResult.Conflict("already-waiting"));
        }

        var createdAt = UtcTime.Truncate(now);
        var entry = await _unitOfWork.Waitlist.CreateAsync(new WaitlistEntry
        {
            UserId = userId,
            ClassId = scheduled.Id,
            Seats = request.Seats,
            Status = WaitingStatus,
            CreatedAt = createdAt
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var position = await _unitOfWork.Waitlist.WaitingPositionAsync(scheduled.Id, entry.CreatedAt, entry.Id, cancellationToken);
        var body = JsonSerializer.Serialize(new WaitlistJoinedResponse
        {
            Id = entry.Id,
            ClassId = entry.ClassId,
            Seats = entry.Seats,
            Position = position,
            CreatedAt = entry.CreatedAt
        }, _json);

        await _unitOfWork.Idempotency.CreateAsync(new IdempotencyRecord
        {
            UserId = userId,
            Scope = Scope,
            Key = idempotencyKey,
            KeyHash = keyHash,
            RequestHash = requestHash,
            StatusCode = StatusCodes.Status201Created,
            ResponseBody = body,
            CreatedAt = createdAt
        }, cancellationToken);

        return new TransactionOutcome<WaitlistJoinResult>(true, WaitlistJoinResult.Created(body));
    }

    private async Task<TransactionOutcome<WaitlistLeaveResult>> LeaveAsync(
        long userId,
        WaitlistEntry entry,
        CancellationToken cancellationToken)
    {
        await _unitOfWork.Waitlist.LockByIdAsync(entry.Id, cancellationToken);
        await _unitOfWork.DbContext.Entry(entry).ReloadAsync(cancellationToken);

        if (entry.UserId != userId || entry.Status == EnrolledStatus)
        {
            return new TransactionOutcome<WaitlistLeaveResult>(false, WaitlistLeaveResult.NotFound());
        }

        if (entry.Status == CancelledStatus)
        {
            return new TransactionOutcome<WaitlistLeaveResult>(
                false,
                WaitlistLeaveResult.Ok(new WaitlistLeftResponse { Id = entry.Id, Status = CancelledStatus }));
        }

        entry.Status = CancelledStatus;
        return new TransactionOutcome<WaitlistLeaveResult>(
            true,
            WaitlistLeaveResult.Ok(new WaitlistLeftResponse { Id = entry.Id, Status = CancelledStatus }));
    }

    private static WaitlistJoinResult FromExisting(IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash == requestHash)
        {
            return WaitlistJoinResult.Created(record.ResponseBody);
        }

        // решение: тот же ключ и другой request_hash дают 409 idempotency-mismatch,
        // сохранённая запись очереди и ответ не меняются.
        return WaitlistJoinResult.Conflict("idempotency-mismatch");
    }

    private static TransactionOutcome<WaitlistJoinResult> Rollback(WaitlistJoinResult result)
    {
        return new TransactionOutcome<WaitlistJoinResult>(false, result);
    }

    private static PostgresException? FindPostgres(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
