using System.Net.Http.Headers;
using System.Text;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;

namespace WebApi.Tests;

internal static class Scenario
{
    public static async Task<World> CreateWorldAsync(
        ApiApplicationFactory factory,
        int capacity = 8,
        int remainingVisits = 10,
        DateTime? startsAt = null,
        DateOnly? validUntil = null,
        bool cancelled = false,
        string role = "user")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var user = new User
        {
            Email = $"phase-{Guid.NewGuid():N}@club.test",
            PasswordHash = "phase",
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
        var room = new Room { Name = $"phase-{Guid.NewGuid():N}" };
        db.Set<User>().Add(user);
        db.Set<Room>().Add(room);
        await db.SaveChangesAsync();

        db.Set<Pass>().Add(new Pass
        {
            UserId = user.Id,
            RemainingVisits = remainingVisits,
            ValidUntil = validUntil ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });
        var scheduled = new Class
        {
            RoomId = room.Id,
            Title = "Phase",
            StartsAt = startsAt ?? DateTime.UtcNow.AddDays(2),
            DurationMinutes = 60,
            Capacity = capacity,
            IsCancelled = cancelled
        };
        db.Set<Class>().Add(scheduled);
        await db.SaveChangesAsync();

        return new World(user.Id, room.Id, scheduled.Id, room.Name, remainingVisits);
    }

    public static async Task<long> AddUserAsync(
        ApiApplicationFactory factory,
        int remainingVisits = 10,
        DateOnly? validUntil = null,
        string role = "user")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var user = new User
        {
            Email = $"phase-{Guid.NewGuid():N}@club.test",
            PasswordHash = "phase",
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
        db.Set<User>().Add(user);
        await db.SaveChangesAsync();
        db.Set<Pass>().Add(new Pass
        {
            UserId = user.Id,
            RemainingVisits = remainingVisits,
            ValidUntil = validUntil ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    public static async Task<long> AddClassAsync(
        ApiApplicationFactory factory,
        long roomId,
        DateTime startsAt,
        int capacity = 8,
        bool cancelled = false,
        string title = "Phase")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var scheduled = new Class
        {
            RoomId = roomId,
            Title = title,
            StartsAt = startsAt,
            DurationMinutes = 45,
            Capacity = capacity,
            IsCancelled = cancelled
        };
        db.Set<Class>().Add(scheduled);
        await db.SaveChangesAsync();
        return scheduled.Id;
    }

    public static async Task<long> AddEnrollmentAsync(
        ApiApplicationFactory factory,
        long userId,
        long classId,
        int seats,
        string status = "active")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var enrollment = new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CancelledAt = status == "cancelled" ? DateTime.UtcNow.AddHours(-2) : null,
            RefundedVisits = status == "cancelled" ? 0 : null
        };
        db.Set<Enrollment>().Add(enrollment);
        await db.SaveChangesAsync();
        return enrollment.Id;
    }

    public static async Task<string> TokenAsync(ApiApplicationFactory factory, long userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var user = await unitOfWork.GetRepository<User>().GetByIdAsync(userId);
        return scope.ServiceProvider.GetRequiredService<AccessTokenService>().Create(user!).AccessToken;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string token,
        string? json = null,
        string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(method, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null)
        {
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(message);
    }

    public static async Task<int> RemainingVisitsAsync(ApiApplicationFactory factory, long userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        return await db.Set<Pass>().Where(x => x.UserId == userId).Select(x => x.RemainingVisits).SingleAsync();
    }

    public static async Task<Enrollment> EnrollmentAsync(ApiApplicationFactory factory, long enrollmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        return await db.Set<Enrollment>().IgnoreQueryFilters().SingleAsync(x => x.Id == enrollmentId);
    }

    public static async Task<int> ActiveSeatsAsync(ApiApplicationFactory factory, long classId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        return await db.Set<Enrollment>()
            .IgnoreQueryFilters()
            .Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (int?)x.Seats) ?? 0;
    }

    public static async Task<WaitlistEntry?> WaitlistAsync(ApiApplicationFactory factory, long entryId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        return await db.Set<WaitlistEntry>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == entryId);
    }

    public static async Task DeleteUserAsync(ApiApplicationFactory factory, long userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var user = await unitOfWork.GetRepository<User>().GetByIdAsync(userId);

        if (user is null)
        {
            return;
        }

        unitOfWork.GetRepository<User>().Delete(user);
        await unitOfWork.SaveChangesAsync();
    }

    public static async Task DeleteRoomAsync(ApiApplicationFactory factory, long roomId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var room = await unitOfWork.Room.GetByIdAsync(roomId);

        if (room is null)
        {
            return;
        }

        unitOfWork.Room.Delete(room);
        await unitOfWork.SaveChangesAsync();
    }

    internal sealed record World(long UserId, long RoomId, long ClassId, string RoomName, int RemainingVisits);
}
