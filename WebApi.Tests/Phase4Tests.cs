using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Time;

namespace WebApi.Tests;

[TestClass]
public class Phase4Tests
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
    public async Task GetReturnsTheContractCardAndRepeatsCreatedAt()
    {
        var world = await CreateWorldAsync(remainingVisits: 10);
        try
        {
            var token = await TokenAsync(world);
            var created = await PostAsync(token, "card", world, 2);
            var first = await GetAsync(token, created.Id);
            var second = await GetAsync(token, created.Id);

            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual("application/json", first.Content.Headers.ContentType!.MediaType);
            var firstJson = await first.Content.ReadAsStringAsync();
            var secondJson = await second.Content.ReadAsStringAsync();
            AssertCard(firstJson, world, created.Id, "active", 2, 8, created.CreatedAt);
            Assert.AreEqual(CreatedAt(firstJson), CreatedAt(secondJson));
            Assert.AreEqual(created.CreatedAt, CreatedAt(secondJson));
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task GetReturnsCancelledEnrollment()
    {
        var world = await CreateWorldAsync(remainingVisits: 4);
        try
        {
            var enrollmentId = await InsertEnrollmentAsync(world, 3, "cancelled");
            var json = await ReadCardAsync(world, enrollmentId);

            AssertCard(json, world, enrollmentId, "cancelled", 3, 4, CreatedAt(json));
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task GetReturnsEnrollmentOnCancelledClass()
    {
        var world = await CreateWorldAsync(cancelled: true);
        try
        {
            var enrollmentId = await InsertEnrollmentAsync(world, 1, "active");
            using var document = JsonDocument.Parse(await ReadCardAsync(world, enrollmentId));
            var scheduled = document.RootElement.GetProperty("class");

            Assert.AreEqual(world.ClassId, scheduled.GetProperty("id").GetInt64());
            Assert.IsTrue(scheduled.GetProperty("isCancelled").GetBoolean());
            Assert.AreEqual(world.RoomName, scheduled.GetProperty("room").GetProperty("name").GetString());
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task ForeignAndMissingEnrollmentsAreNotFound()
    {
        var owner = await CreateWorldAsync();
        var stranger = await CreateWorldAsync(classId: owner.ClassId);
        try
        {
            var token = await TokenAsync(owner);
            var created = await PostAsync(token, "owned", owner, 1);
            var cached = await GetAsync(token, created.Id);
            Assert.AreEqual(HttpStatusCode.OK, cached.StatusCode);

            var foreign = await GetAsync(await TokenAsync(stranger), created.Id);
            var missing = await GetAsync(token, long.MaxValue);
            var again = await GetAsync(token, created.Id);

            AssertNotFound(foreign);
            AssertNotFound(missing);
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
            Assert.AreEqual(created.Id, JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64());
        }
        finally
        {
            await DeleteWorldAsync(stranger);
            await DeleteWorldAsync(owner);
        }
    }

    [TestMethod]
    public async Task NonNumericIdIsBadRequest()
    {
        var world = await CreateWorldAsync();
        try
        {
            var response = await GetAsync(await TokenAsync(world), "abc");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task DirectEditStaysCachedUntilTheNextCreate()
    {
        var world = await CreateWorldAsync(remainingVisits: 10, capacity: 8);
        try
        {
            var token = await TokenAsync(world);
            var created = await PostAsync(token, "cached", world, 2);
            Assert.AreEqual(8, Visits(await ReadCardAsync(world, created.Id)));

            await SetVisitsAsync(world.UserId, 99);
            Assert.AreEqual(8, Visits(await ReadCardAsync(world, created.Id)));

            var replay = await PostAsync(token, "cached", world, 2);
            Assert.AreEqual(created.Id, replay.Id);
            Assert.AreEqual(8, Visits(await ReadCardAsync(world, created.Id)));
            Assert.AreEqual(99, await RemainingVisitsAsync(world.UserId));

            await PostAsync(token, "fresh", world, 1);
            Assert.AreEqual(98, await RemainingVisitsAsync(world.UserId));
            Assert.AreEqual(98, Visits(await ReadCardAsync(world, created.Id)));
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    [TestMethod]
    public async Task ListIdsIncludesEnrollmentOnCancelledClass()
    {
        var world = await CreateWorldAsync(cancelled: true);
        try
        {
            var enrollmentId = await InsertEnrollmentAsync(world, 2, "active");

            await using var scope = _factory.Services.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var ids = await unitOfWork.Enrollment.ListIdsByUserIgnoringFiltersAsync(world.UserId);

            CollectionAssert.Contains(ids, enrollmentId);
        }
        finally
        {
            await DeleteWorldAsync(world);
        }
    }

    private static async Task<string> ReadCardAsync(World world, long enrollmentId)
    {
        var response = await GetAsync(await TokenAsync(world), enrollmentId);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertCard(
        string json,
        World world,
        long enrollmentId,
        string status,
        int seats,
        int remainingVisits,
        string createdAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "id", "status", "seats", "createdAt", "class", "pass" },
            Names(root));
        Assert.AreEqual(enrollmentId, root.GetProperty("id").GetInt64());
        Assert.AreEqual(status, root.GetProperty("status").GetString());
        Assert.AreEqual(seats, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(createdAt, root.GetProperty("createdAt").GetString());

        var scheduled = root.GetProperty("class");
        CollectionAssert.AreEquivalent(
            new[] { "id", "title", "startsAt", "durationMinutes", "isCancelled", "room" },
            Names(scheduled));
        Assert.AreEqual(world.ClassId, scheduled.GetProperty("id").GetInt64());
        Assert.AreEqual(world.Title, scheduled.GetProperty("title").GetString());
        Assert.AreEqual(world.DurationMinutes, scheduled.GetProperty("durationMinutes").GetInt32());
        Assert.AreEqual(world.Cancelled, scheduled.GetProperty("isCancelled").GetBoolean());

        var room = scheduled.GetProperty("room");
        CollectionAssert.AreEquivalent(new[] { "id", "name" }, Names(room));
        Assert.AreEqual(world.OwnedRoomId, room.GetProperty("id").GetInt64());
        Assert.AreEqual(world.RoomName, room.GetProperty("name").GetString());

        var pass = root.GetProperty("pass");
        CollectionAssert.AreEquivalent(new[] { "remainingVisits", "validUntil" }, Names(pass));
        Assert.AreEqual(remainingVisits, pass.GetProperty("remainingVisits").GetInt32());
        var validUntil = pass.GetProperty("validUntil").GetString();
        Assert.AreEqual(world.ValidUntil.ToString("yyyy-MM-dd"), validUntil);
        Assert.IsFalse(validUntil!.Contains('T'));
    }

    private static string[] Names(JsonElement element)
    {
        return element.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static string CreatedAt(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("createdAt").GetString()!;
    }

    private static int Visits(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32();
    }

    private static void AssertNotFound(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Assert.IsFalse(document.RootElement.TryGetProperty("reason", out _));
    }

    private static async Task<CreatedBooking> PostAsync(string token, string key, World world, int seats)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent(
                $$"""{"classId":{{world.ClassId}},"seats":{{seats}}}""",
                Encoding.UTF8,
                "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var response = await _client.SendAsync(message);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return new CreatedBooking(root.GetProperty("id").GetInt64(), root.GetProperty("createdAt").GetString()!);
    }

    private static async Task<HttpResponseMessage> GetAsync(string token, long id)
    {
        return await GetAsync(token, id.ToString());
    }

    private static async Task<HttpResponseMessage> GetAsync(string token, string id)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, $"/enrollments/{id}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private static async Task<long> InsertEnrollmentAsync(World world, int seats, string status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var enrollment = await unitOfWork.Enrollment.CreateAsync(new Enrollment
        {
            UserId = world.UserId,
            ClassId = world.ClassId,
            Seats = seats,
            Status = status,
            CreatedAt = UtcTime.Truncate(DateTime.UtcNow),
            CancelledAt = status == "cancelled" ? DateTime.UtcNow : null
        });
        await unitOfWork.SaveChangesAsync();

        return enrollment.Id;
    }

    private static async Task SetVisitsAsync(long userId, int remainingVisits)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var pass = await db.Set<Pass>().SingleAsync(x => x.UserId == userId);
        pass.RemainingVisits = remainingVisits;
        await db.SaveChangesAsync();
    }

    private static async Task<int> RemainingVisitsAsync(long userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;

        return await db.Set<Pass>().Where(x => x.UserId == userId).Select(x => x.RemainingVisits).SingleAsync();
    }

    private static async Task<World> CreateWorldAsync(
        int capacity = 8,
        int remainingVisits = 10,
        bool cancelled = false,
        long? classId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var user = new User
        {
            Email = $"phase4-{Guid.NewGuid():N}@club.test",
            PasswordHash = "phase4",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        db.Set<User>().Add(user);

        var startsAt = DateTime.UtcNow.AddDays(2);
        var validUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var roomName = $"phase4-{Guid.NewGuid():N}";
        Room? ownedRoom = null;

        if (classId is null)
        {
            ownedRoom = new Room { Name = roomName };
            db.Set<Room>().Add(ownedRoom);
        }

        await db.SaveChangesAsync();
        db.Set<Pass>().Add(new Pass
        {
            UserId = user.Id,
            RemainingVisits = remainingVisits,
            ValidUntil = validUntil
        });

        long scheduledClassId;

        if (classId is null)
        {
            var scheduled = new Class
            {
                RoomId = ownedRoom!.Id,
                Title = "Phase4",
                StartsAt = startsAt,
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

        return new World(
            user.Id,
            ownedRoom?.Id ?? 0,
            scheduledClassId,
            remainingVisits,
            roomName,
            "Phase4",
            startsAt,
            60,
            validUntil,
            cancelled);
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

    private sealed record World(
        long UserId,
        long OwnedRoomId,
        long ClassId,
        int RemainingVisits,
        string RoomName,
        string Title,
        DateTime StartsAt,
        int DurationMinutes,
        DateOnly ValidUntil,
        bool Cancelled);

    private sealed record CreatedBooking(long Id, string CreatedAt);
}
