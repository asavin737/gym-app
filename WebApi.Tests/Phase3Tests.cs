using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Data;
using WebApi.Enrollments;

namespace WebApi.Tests;

[TestClass]
public class Phase3Tests
{
    private static ApiApplicationFactory _factory = null!;
    private static HttpClient _client = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task CreateSpendsSeatsAndVisits()
    {
        var world = await CreateWorldAsync(remainingVisits: 10, capacity: 8);
        try
        {
            var before = DateTime.UtcNow;
            var result = await CreateAsync(world, "spend", 2);
            var after = DateTime.UtcNow;

            Assert.AreEqual(StatusCodes.Status201Created, result.StatusCode);
            var created = ReadCreated(result.Body!);
            Assert.AreEqual(world.ClassId, created.ClassId);
            Assert.AreEqual(2, created.Seats);
            Assert.AreEqual("active", created.Status);
            Assert.AreEqual(DateTimeKind.Utc, created.CreatedAt.Kind);
            Assert.IsTrue(created.CreatedAt >= before.AddSeconds(-1) && created.CreatedAt <= after.AddSeconds(1));

            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(1, snapshot.Enrollments);
            Assert.AreEqual(2, snapshot.ActiveSeats);
            Assert.AreEqual(8, snapshot.RemainingVisits);
            Assert.AreEqual(1, snapshot.Keys);
            Assert.AreEqual(0, snapshot.Waitlist);
            Assert.AreEqual(created.CreatedAt, snapshot.StoredCreatedAt);
            Assert.AreEqual(created.Id, snapshot.EnrollmentId);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task RejectsCancelledClassAsConflict()
    {
        var world = await CreateWorldAsync(cancelled: true);
        try
        {
            var result = await CreateAsync(world, "cancelled", 1);

            AssertConflict(result, "class-cancelled");
            Assert.AreNotEqual(StatusCodes.Status404NotFound, result.StatusCode);
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task RejectsStartedClass()
    {
        var world = await CreateWorldAsync(startsAt: DateTime.UtcNow.AddMinutes(-1));
        try
        {
            AssertConflict(await CreateAsync(world, "started", 1), "class-started");
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task RejectsExpiredPass()
    {
        var world = await CreateWorldAsync(validUntil: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        try
        {
            AssertConflict(await CreateAsync(world, "expired", 1), "pass-expired");
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task AllowsPassThatIsValidThroughToday()
    {
        var world = await CreateWorldAsync(
            validUntil: DateOnly.FromDateTime(DateTime.UtcNow),
            startsAt: DateTime.UtcNow.AddDays(40));
        try
        {
            var result = await CreateAsync(world, "today", 1);

            Assert.AreEqual(StatusCodes.Status201Created, result.StatusCode);
            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(1, snapshot.Enrollments);
            Assert.AreEqual(world.RemainingVisits - 1, snapshot.RemainingVisits);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task RejectsWhenCapacityIsExceeded()
    {
        var world = await CreateWorldAsync(capacity: 2, remainingVisits: 10);
        try
        {
            AssertConflict(await CreateAsync(world, "full", 3), "capacity-exceeded");
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task RejectsWhenPassIsExhausted()
    {
        var world = await CreateWorldAsync(capacity: 10, remainingVisits: 1);
        try
        {
            AssertConflict(await CreateAsync(world, "empty-pass", 2), "pass-exhausted");
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task RejectsBlankKeyAndNonPositiveSeats()
    {
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await CreateAsync(0, null, 1, 1)).StatusCode);
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await CreateAsync(0, "", 1, 1)).StatusCode);
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await CreateAsync(0, "   ", 1, 1)).StatusCode);
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await CreateAsync(0, "seats", 1, 0)).StatusCode);
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await CreateAsync(0, "seats", 1, -2)).StatusCode);
    }

    [TestMethod]
    public async Task MissingClassIsNotFound()
    {
        var world = await CreateWorldAsync();
        try
        {
            var result = await CreateAsync(world.UserId, "missing", long.MaxValue, 1);

            Assert.AreEqual(StatusCodes.Status404NotFound, result.StatusCode);
            Assert.IsNull(result.Reason);
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task ReplayReturnsTheFirstBodyWithoutASecondCharge()
    {
        var world = await CreateWorldAsync(remainingVisits: 6);
        try
        {
            var first = await CreateAsync(world, "same", 2);
            var second = await CreateAsync(world, "same", 2);

            Assert.AreEqual(StatusCodes.Status201Created, first.StatusCode);
            Assert.AreEqual(StatusCodes.Status201Created, second.StatusCode);
            var created = ReadCreated(first.Body!);
            var replayed = ReadCreated(second.Body!);
            Assert.AreEqual(created.Id, replayed.Id);
            Assert.AreEqual(created.CreatedAt, replayed.CreatedAt);
            Assert.AreEqual(created.Seats, replayed.Seats);

            await CancelClassAsync(world.ClassId);
            var afterCancel = ReadCreated((await CreateAsync(world, "same", 2)).Body!);
            Assert.AreEqual(created.Id, afterCancel.Id);

            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(1, snapshot.Enrollments);
            Assert.AreEqual(4, snapshot.RemainingVisits);
            Assert.AreEqual(1, snapshot.Keys);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task DifferentBodyKeepsTheFirstBooking()
    {
        var world = await CreateWorldAsync(remainingVisits: 8, capacity: 8);
        try
        {
            var first = await CreateAsync(world, "other", 1);
            var mismatch = await CreateAsync(world, "other", 2);

            Assert.AreEqual(StatusCodes.Status201Created, first.StatusCode);
            AssertConflict(mismatch, "idempotency-mismatch");

            var again = ReadCreated((await CreateAsync(world, "other", 1)).Body!);
            Assert.AreEqual(ReadCreated(first.Body!).Id, again.Id);

            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(1, snapshot.Enrollments);
            Assert.AreEqual(1, snapshot.ActiveSeats);
            Assert.AreEqual(7, snapshot.RemainingVisits);
            Assert.AreEqual(1, snapshot.Keys);
            Assert.AreEqual(0, snapshot.Waitlist);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task SameKeyForAnotherUserCreatesASeparateBooking()
    {
        var firstUser = await CreateWorldAsync();
        var secondUser = await CreateWorldAsync(classId: firstUser.ClassId);
        try
        {
            var first = ReadCreated((await CreateAsync(firstUser, "shared", 1)).Body!);
            var second = ReadCreated((await CreateAsync(secondUser, "shared", 1)).Body!);

            Assert.AreNotEqual(first.Id, second.Id);
            Assert.AreEqual(firstUser.ClassId, first.ClassId);
            Assert.AreEqual(firstUser.ClassId, second.ClassId);
            Assert.AreEqual(1, (await ReadSnapshotAsync(firstUser)).ActiveSeats);
            Assert.AreEqual(1, (await ReadSnapshotAsync(secondUser)).ActiveSeats);
        }
        finally
        {
            await DeleteWorldAsync(secondUser);
            await DeleteWorldAsync(firstUser);
        }
    }

    [TestMethod]
    public async Task ConcurrentReplayKeepsOneBooking()
    {
        var world = await CreateWorldAsync(remainingVisits: 5, capacity: 5);
        try
        {
            var first = CreateAsync(world, "race", 1);
            var second = CreateAsync(world, "race", 1);
            var results = await Task.WhenAll(first, second);

            Assert.IsTrue(results.All(result => result.StatusCode == StatusCodes.Status201Created));
            Assert.AreEqual(ReadCreated(results[0].Body!).Id, ReadCreated(results[1].Body!).Id);

            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(1, snapshot.Enrollments);
            Assert.AreEqual(4, snapshot.RemainingVisits);
            Assert.AreEqual(1, snapshot.Keys);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task SumActiveSeatsSeesSavedBookingAndDropsItOnRollback()
    {
        var world = await CreateWorldAsync();
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await UnitOfWorkTransaction.ExecuteAsync(unitOfWork, async cancellationToken =>
            {
                Assert.AreEqual(0, await unitOfWork.Enrollment.SumActiveSeatsAsync(world.ClassId, cancellationToken));
                await unitOfWork.Enrollment.CreateAsync(new Enrollment
                {
                    UserId = world.UserId,
                    ClassId = world.ClassId,
                    Seats = 2,
                    Status = "active",
                    CreatedAt = DateTime.UtcNow
                }, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                Assert.AreEqual(2, await unitOfWork.Enrollment.SumActiveSeatsAsync(world.ClassId, cancellationToken));
                return new TransactionOutcome<bool>(false, false);
            });

            Assert.AreEqual(0, (await ReadSnapshotAsync(world)).ActiveSeats);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task PostReturnsTheBookingAndIgnoresUnknownFields()
    {
        var world = await CreateWorldAsync(remainingVisits: 9);
        try
        {
            var token = await TokenAsync(world);
            var response = await SendAsync(token, "http-create", $$"""
                {"classId":{{world.ClassId}},"seats":2,"note":"guest"}
                """);

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.IsTrue(response.Content.Headers.ContentType!.MediaType == "application/json");
            var created = ReadCreated(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(world.ClassId, created.ClassId);
            Assert.AreEqual(2, created.Seats);
            Assert.AreEqual("active", created.Status);

            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(created.Id, snapshot.EnrollmentId);
            Assert.AreEqual(7, snapshot.RemainingVisits);
            Assert.AreEqual(2, snapshot.ActiveSeats);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task PostRejectsBadSeatsKeyAndJson()
    {
        var world = await CreateWorldAsync();
        try
        {
            var token = await TokenAsync(world);
            var bodies = new (string? Key, string Json)[]
            {
                ("bad-seats", $$"""{"classId":{{world.ClassId}},"seats":0}"""),
                ("bad-seats-negative", $$"""{"classId":{{world.ClassId}},"seats":-1}"""),
                (null, $$"""{"classId":{{world.ClassId}},"seats":1}"""),
                ("", $$"""{"classId":{{world.ClassId}},"seats":1}"""),
                ("   ", $$"""{"classId":{{world.ClassId}},"seats":1}"""),
                ("missing-class", """{"seats":1}"""),
                ("missing-seats", $$"""{"classId":{{world.ClassId}}}"""),
                ("text-id", """{"classId":"abc","seats":1}"""),
                ("fraction", $$"""{"classId":{{world.ClassId}},"seats":1.5}"""),
                ("broken", "{"),
                ("empty", "")
            };

            foreach (var (key, json) in bodies)
            {
                var response = await SendAsync(token, key, json);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, json);
            }

            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task PostMissingClassIsNotFound()
    {
        var world = await CreateWorldAsync();
        try
        {
            var response = await SendAsync(await TokenAsync(world), "http-missing", """{"classId":9223372036854775807,"seats":1}""");

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            await AssertUntouchedAsync(world);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task PostConflictWritesReasonBesideStatus()
    {
        var world = await CreateWorldAsync(cancelled: true);
        try
        {
            var response = await SendAsync(await TokenAsync(world), "http-cancelled", $$"""
                {"classId":{{world.ClassId}},"seats":1}
                """);

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("class-cancelled", document.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(409, document.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Conflict", document.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(document.RootElement.TryGetProperty("extensions", out _));
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task PostReplaysTheFirstBodyAndRejectsADifferentOne()
    {
        var world = await CreateWorldAsync(remainingVisits: 6, capacity: 6);
        try
        {
            var token = await TokenAsync(world);
            var body = $$"""{"classId":{{world.ClassId}},"seats":2}""";
            var first = ReadCreated(await (await SendAsync(token, "http-replay", body)).Content.ReadAsStringAsync());
            var secondResponse = await SendAsync(token, "http-replay", body);
            Assert.AreEqual(HttpStatusCode.Created, secondResponse.StatusCode);
            var second = ReadCreated(await secondResponse.Content.ReadAsStringAsync());
            Assert.AreEqual(first.Id, second.Id);
            Assert.AreEqual(first.CreatedAt, second.CreatedAt);

            var mismatch = await SendAsync(token, "http-replay", $$"""{"classId":{{world.ClassId}},"seats":3}""");
            Assert.AreEqual(HttpStatusCode.Conflict, mismatch.StatusCode);
            using var document = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
            Assert.AreEqual("idempotency-mismatch", document.RootElement.GetProperty("reason").GetString());

            var snapshot = await ReadSnapshotAsync(world);
            Assert.AreEqual(1, snapshot.Enrollments);
            Assert.AreEqual(2, snapshot.ActiveSeats);
            Assert.AreEqual(4, snapshot.RemainingVisits);
            Assert.AreEqual(1, snapshot.Keys);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    private static async Task<EnrollmentCreateResult> CreateAsync(World world, string key, int seats)
    {
        return await CreateAsync(world.UserId, key, world.ClassId, seats);
    }

    private static async Task<EnrollmentCreateResult> CreateAsync(long userId, string? key, long classId, int seats)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();

        return await service.CreateAsync(userId, key, new CreateEnrollmentRequest
        {
            ClassId = classId,
            Seats = seats
        });
    }

    private static async Task<HttpResponseMessage> SendAsync(string token, string? key, string json)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (key is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await _client.SendAsync(message);
    }

    private static async Task<string> TokenAsync(World world)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var user = await unitOfWork.GetRepository<User>().GetByIdAsync(world.UserId);
        var tokens = scope.ServiceProvider.GetRequiredService<AccessTokenService>();

        return tokens.Create(user!).AccessToken;
    }

    private static void AssertConflict(EnrollmentCreateResult result, string reason)
    {
        Assert.AreEqual(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.AreEqual(reason, result.Reason);
        Assert.IsNull(result.Body);
    }

    private static async Task AssertUntouchedAsync(World world)
    {
        var snapshot = await ReadSnapshotAsync(world);
        Assert.AreEqual(0, snapshot.Enrollments);
        Assert.AreEqual(0, snapshot.ActiveSeats);
        Assert.AreEqual(world.RemainingVisits, snapshot.RemainingVisits);
        Assert.AreEqual(0, snapshot.Keys);
        Assert.AreEqual(0, snapshot.Waitlist);
    }

    private static CreatedBooking ReadCreated(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "id", "classId", "seats", "status", "createdAt" }, names);

        var createdAt = root.GetProperty("createdAt").GetDateTime();

        return new CreatedBooking(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("classId").GetInt64(),
            root.GetProperty("seats").GetInt32(),
            root.GetProperty("status").GetString()!,
            createdAt);
    }

    private static async Task<World> CreateWorldAsync(
        int capacity = 8,
        int remainingVisits = 10,
        DateTime? startsAt = null,
        DateOnly? validUntil = null,
        bool cancelled = false,
        long? classId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var user = new User
        {
            Email = $"phase3-{Guid.NewGuid():N}@club.test",
            PasswordHash = "phase3",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        db.Set<User>().Add(user);

        Room? ownedRoom = null;

        if (classId is null)
        {
            ownedRoom = new Room { Name = $"phase3-{Guid.NewGuid():N}" };
            db.Set<Room>().Add(ownedRoom);
        }

        await db.SaveChangesAsync();

        db.Set<Pass>().Add(new Pass
        {
            UserId = user.Id,
            RemainingVisits = remainingVisits,
            ValidUntil = validUntil ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });

        long scheduledClassId;

        if (classId is null)
        {
            var scheduled = new Class
            {
                RoomId = ownedRoom!.Id,
                Title = "Phase3",
                StartsAt = startsAt ?? DateTime.UtcNow.AddDays(2),
                DurationMinutes = 60,
                Capacity = capacity,
                IsCancelled = cancelled
            };
            db.Set<Class>().Add(scheduled);
            await db.SaveChangesAsync();
            scheduledClassId = scheduled.Id;
        }
        else
        {
            await db.SaveChangesAsync();
            scheduledClassId = classId.Value;
        }

        return new World(user.Id, ownedRoom?.Id ?? 0, scheduledClassId, remainingVisits);
    }

    private static async Task CancelClassAsync(long classId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var scheduled = await db.Set<Class>().IgnoreQueryFilters().SingleAsync(x => x.Id == classId);
        scheduled.IsCancelled = true;
        await db.SaveChangesAsync();
    }

    private static async Task<Snapshot> ReadSnapshotAsync(World world)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var enrollments = await db.Set<Enrollment>()
            .IgnoreQueryFilters()
            .Where(x => x.UserId == world.UserId && x.ClassId == world.ClassId)
            .ToListAsync();
        var pass = await db.Set<Pass>().SingleAsync(x => x.UserId == world.UserId);
        var keys = await db.Set<IdempotencyRecord>().CountAsync(x => x.UserId == world.UserId);
        var waitlist = await db.Set<WaitlistEntry>().IgnoreQueryFilters().CountAsync(x => x.UserId == world.UserId);
        var active = enrollments.Where(x => x.Status == "active").ToList();

        return new Snapshot(
            enrollments.Count,
            active.Sum(x => x.Seats),
            pass.RemainingVisits,
            keys,
            waitlist,
            active.SingleOrDefault()?.Id ?? 0,
            active.SingleOrDefault()?.CreatedAt ?? default);
    }

    private static async Task DeleteWorldAsync(World world)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var user = await unitOfWork.GetRepository<User>().GetByIdAsync(world.UserId);

        if (user != null)
        {
            unitOfWork.GetRepository<User>().Delete(user);
            await unitOfWork.SaveChangesAsync();
        }

        if (world.OwnedRoomId == 0)
        {
            return;
        }

        var room = await unitOfWork.Room.GetByIdAsync(world.OwnedRoomId);

        if (room == null)
        {
            return;
        }

        unitOfWork.Room.Delete(room);
        await unitOfWork.SaveChangesAsync();
    }

    private sealed record World(long UserId, long OwnedRoomId, long ClassId, int RemainingVisits);

    private sealed record CreatedBooking(long Id, long ClassId, int Seats, string Status, DateTime CreatedAt);

    private sealed record Snapshot(
        int Enrollments,
        int ActiveSeats,
        int RemainingVisits,
        int Keys,
        int Waitlist,
        long EnrollmentId,
        DateTime StoredCreatedAt);
}
