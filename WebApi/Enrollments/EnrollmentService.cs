using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Caching;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using WebApi.Data;
using WebApi.Time;

namespace WebApi.Enrollments;

public class EnrollmentService
{
    public const string Scope = "enrollment";

    private const string ActiveStatus = "active";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly JsonSerializerOptions _json;

    public EnrollmentService(IUnitOfWork unitOfWork, ICacheService cache, IOptions<JsonOptions> jsonOptions)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _json = jsonOptions.Value.JsonSerializerOptions;
    }

    public async Task<EnrollmentCreateResult> CreateAsync(
        long userId,
        string? idempotencyKey,
        CreateEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || request.Seats < 1)
        {
            return EnrollmentCreateResult.BadRequest();
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
            var outcome = await UnitOfWorkTransaction.ExecuteAsync(
                _unitOfWork,
                token => BookAsync(userId, idempotencyKey, keyHash, requestHash, request, token),
                cancellationToken);

            if (outcome.Created)
            {
                await InvalidateUserCardsAsync(userId, cancellationToken);
            }

            return outcome.Result;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _unitOfWork.DbContext.ChangeTracker.Clear();
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

    public async Task<EnrollmentCardResponse?> GetAsync(
        long userId,
        long enrollmentId,
        CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetAsync<CachedCard>(CacheKey(enrollmentId), cancellationToken);

        if (cached is not null)
        {
            return cached.UserId == userId ? cached.Card : null;
        }

        var enrollment = await _unitOfWork.Enrollment.GetByIdIgnoringFiltersAsync(enrollmentId, cancellationToken);

        if (enrollment is null || enrollment.UserId != userId)
        {
            return null;
        }

        var pass = (await _unitOfWork.Pass.GetByUserAsync(userId, cancellationToken))!;
        var card = new EnrollmentCardResponse
        {
            Id = enrollment.Id,
            Status = enrollment.Status,
            Seats = enrollment.Seats,
            CreatedAt = enrollment.CreatedAt,
            Class = new EnrollmentCardClass
            {
                Id = enrollment.Class.Id,
                Title = enrollment.Class.Title,
                StartsAt = enrollment.Class.StartsAt,
                DurationMinutes = enrollment.Class.DurationMinutes,
                IsCancelled = enrollment.Class.IsCancelled,
                Room = new EnrollmentCardRoom
                {
                    Id = enrollment.Class.Room.Id,
                    Name = enrollment.Class.Room.Name
                }
            },
            Pass = new EnrollmentCardPass
            {
                RemainingVisits = pass.RemainingVisits,
                ValidUntil = pass.ValidUntil
            }
        };

        await _cache.SetAsync(
            CacheKey(enrollmentId),
            new CachedCard { UserId = userId, Card = card },
            ct: cancellationToken);

        return card;
    }

    private async Task<TransactionOutcome<BookingOutcome>> BookAsync(
        long userId,
        string idempotencyKey,
        string keyHash,
        string requestHash,
        CreateEnrollmentRequest request,
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
            return Rollback(EnrollmentCreateResult.NotFound());
        }

        var now = DateTime.UtcNow;

        // решение: несколько причин сразу проверяются в порядке class-cancelled, class-started,
        // pass-expired, capacity-exceeded, pass-exhausted.
        if (scheduled.IsCancelled)
        {
            return Rollback(EnrollmentCreateResult.Conflict("class-cancelled"));
        }

        if (scheduled.StartsAt <= now)
        {
            return Rollback(EnrollmentCreateResult.Conflict("class-started"));
        }

        var pass = (await _unitOfWork.Pass.LockByUserIdAsync(userId, cancellationToken))!;

        if (pass.ValidUntil < DateOnly.FromDateTime(now))
        {
            return Rollback(EnrollmentCreateResult.Conflict("pass-expired"));
        }

        var taken = await _unitOfWork.Enrollment.SumActiveSeatsAsync(scheduled.Id, cancellationToken);

        if (taken + request.Seats > scheduled.Capacity)
        {
            return Rollback(EnrollmentCreateResult.Conflict("capacity-exceeded"));
        }

        if (pass.RemainingVisits < request.Seats)
        {
            return Rollback(EnrollmentCreateResult.Conflict("pass-exhausted"));
        }

        var createdAt = UtcTime.Truncate(now);
        var enrollment = await _unitOfWork.Enrollment.CreateAsync(new Enrollment
        {
            UserId = userId,
            ClassId = scheduled.Id,
            Seats = request.Seats,
            Status = ActiveStatus,
            CreatedAt = createdAt
        }, cancellationToken);

        pass.RemainingVisits -= request.Seats;
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var body = JsonSerializer.Serialize(new EnrollmentCreatedResponse
        {
            Id = enrollment.Id,
            ClassId = enrollment.ClassId,
            Seats = enrollment.Seats,
            Status = enrollment.Status,
            CreatedAt = enrollment.CreatedAt
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

        return new TransactionOutcome<BookingOutcome>(true, new BookingOutcome(EnrollmentCreateResult.Created(body), true));
    }

    private async Task InvalidateUserCardsAsync(long userId, CancellationToken cancellationToken)
    {
        var ids = await _unitOfWork.Enrollment.ListIdsByUserIgnoringFiltersAsync(userId, cancellationToken);

        foreach (var id in ids)
        {
            await _cache.RemoveAsync(CacheKey(id), cancellationToken);
        }
    }

    // решение: ключ карточки — enrollment-card:{id}. В значении лежит и владелец,
    // иначе чужой запрос по тому же id отдал бы карточку из кэша.
    private static string CacheKey(long enrollmentId) => $"enrollment-card:{enrollmentId}";

    private static EnrollmentCreateResult FromExisting(IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash == requestHash)
        {
            return EnrollmentCreateResult.Created(record.ResponseBody);
        }

        // решение: тот же ключ и другой request_hash дают 409 idempotency-mismatch,
        // сохранённые бронь и ответ не меняются.
        return EnrollmentCreateResult.Conflict("idempotency-mismatch");
    }

    private static TransactionOutcome<BookingOutcome> Rollback(EnrollmentCreateResult result)
    {
        return new TransactionOutcome<BookingOutcome>(false, new BookingOutcome(result, false));
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private readonly record struct BookingOutcome(EnrollmentCreateResult Result, bool Created);

    private sealed class CachedCard
    {
        public long UserId { get; set; }

        public EnrollmentCardResponse Card { get; set; } = null!;
    }
}
