using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;

namespace WebApi.Tests;

[TestClass]
public class Phase5Tests
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
    public async Task IncreaseSpendsTheDifferenceAndRefreshesTheCachedCard()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 8, remainingVisits: 10);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(token, world, 2, "increase");
            Assert.AreEqual(HttpStatusCode.OK, (await GetAsync(token, enrollmentId)).StatusCode);

            var response = await SeatsAsync(token, enrollmentId, """{"seats":4,"ignored":true}""");
            var json = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType!.MediaType);
            AssertCard(json, world, enrollmentId, "active", 4, 6);
            AssertCard(await (await GetAsync(token, enrollmentId)).Content.ReadAsStringAsync(), world, enrollmentId, "active", 4, 6);
            Assert.AreEqual(4, (await Scenario.EnrollmentAsync(_factory, enrollmentId)).Seats);
            Assert.AreEqual(6, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task DecreaseReturnsVisitsWhenThePassIsExpiredAndEmpty()
    {
        var world = await Scenario.CreateWorldAsync(
            _factory,
            remainingVisits: 0,
            validUntil: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3));
        try
        {
            var enrollmentId = await Scenario.AddEnrollmentAsync(_factory, world.UserId, world.ClassId, 3);
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var response = await SeatsAsync(token, enrollmentId, """{"seats":1}""");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            AssertCard(await response.Content.ReadAsStringAsync(), world, enrollmentId, "active", 1, 2);
            Assert.AreEqual(1, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task SameSeatCountLeavesThePassUntouched()
    {
        var world = await Scenario.CreateWorldAsync(_factory, remainingVisits: 6);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(token, world, 2, "same");
            var response = await SeatsAsync(token, enrollmentId, """{"seats":2}""");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            AssertCard(await response.Content.ReadAsStringAsync(), world, enrollmentId, "active", 2, 4);
            Assert.AreEqual(4, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task SameSeatCountStillRejectsAStartedOrCancelledBooking()
    {
        var started = await Scenario.CreateWorldAsync(_factory, startsAt: DateTime.UtcNow.AddMinutes(-5));
        var cancelledClass = await Scenario.CreateWorldAsync(_factory, cancelled: true);
        var cancelledBooking = await Scenario.CreateWorldAsync(_factory);
        try
        {
            var startedId = await Scenario.AddEnrollmentAsync(_factory, started.UserId, started.ClassId, 2);
            var classId = await Scenario.AddEnrollmentAsync(_factory, cancelledClass.UserId, cancelledClass.ClassId, 2);
            var bookingId = await Scenario.AddEnrollmentAsync(_factory, cancelledBooking.UserId, cancelledBooking.ClassId, 2, "cancelled");

            await AssertConflictAsync(started.UserId, startedId, "class-started");
            await AssertConflictAsync(cancelledClass.UserId, classId, "class-cancelled");
            await AssertConflictAsync(cancelledBooking.UserId, bookingId, "enrollment-cancelled");
            Assert.AreEqual(started.RemainingVisits, await Scenario.RemainingVisitsAsync(_factory, started.UserId));
            Assert.AreEqual(2, (await Scenario.EnrollmentAsync(_factory, startedId)).Seats);
        }
        finally
        {
            await DeleteAsync(cancelledBooking);
            await DeleteAsync(cancelledClass);
            await DeleteAsync(started);
        }
    }

    [TestMethod]
    public async Task CancelledEnrollmentWinsWhenTheClassIsAlsoCancelledAndStarted()
    {
        var world = await Scenario.CreateWorldAsync(_factory, cancelled: true, startsAt: DateTime.UtcNow.AddMinutes(-10));
        try
        {
            var enrollmentId = await Scenario.AddEnrollmentAsync(_factory, world.UserId, world.ClassId, 2, "cancelled");
            await AssertConflictAsync(world.UserId, enrollmentId, "enrollment-cancelled");
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task IncreaseRejectsCapacityPassAndAStartedClass()
    {
        var full = await Scenario.CreateWorldAsync(_factory, capacity: 4, remainingVisits: 10);
        var exhausted = await Scenario.CreateWorldAsync(_factory, capacity: 10, remainingVisits: 1);
        var expired = await Scenario.CreateWorldAsync(
            _factory,
            validUntil: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        var started = await Scenario.CreateWorldAsync(_factory, startsAt: DateTime.UtcNow);
        try
        {
            await AssertConflictAsync(full.UserId, await Scenario.AddEnrollmentAsync(_factory, full.UserId, full.ClassId, 2), "capacity-exceeded", 5);
            await AssertConflictAsync(exhausted.UserId, await Scenario.AddEnrollmentAsync(_factory, exhausted.UserId, exhausted.ClassId, 2), "pass-exhausted", 4);
            await AssertConflictAsync(expired.UserId, await Scenario.AddEnrollmentAsync(_factory, expired.UserId, expired.ClassId, 1), "pass-expired", 2);
            await AssertConflictAsync(started.UserId, await Scenario.AddEnrollmentAsync(_factory, started.UserId, started.ClassId, 1), "class-started", 2);
            Assert.AreEqual(2, await Scenario.ActiveSeatsAsync(_factory, full.ClassId));
            Assert.AreEqual(1, await Scenario.RemainingVisitsAsync(_factory, exhausted.UserId));
        }
        finally
        {
            await DeleteAsync(started);
            await DeleteAsync(expired);
            await DeleteAsync(exhausted);
            await DeleteAsync(full);
        }
    }

    [TestMethod]
    public async Task BadRequestAndNotFoundLeaveTheBookingAlone()
    {
        var world = await Scenario.CreateWorldAsync(_factory);
        var stranger = await Scenario.AddUserAsync(_factory);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(token, world, 1, "guard");

            Assert.AreEqual(HttpStatusCode.BadRequest, (await SeatsAsync(token, enrollmentId, """{"seats":0}""")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Scenario.SendAsync(_client, HttpMethod.Post, $"/enrollments/{enrollmentId}/seats", token, """{}""")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Scenario.SendAsync(_client, HttpMethod.Post, "/enrollments/abc/seats", token, """{"seats":2}""")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await SeatsAsync(token, long.MaxValue, """{"seats":2}""")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await SeatsAsync(await Scenario.TokenAsync(_factory, stranger), enrollmentId, """{"seats":2}""")).StatusCode);
            Assert.AreEqual(1, (await Scenario.EnrollmentAsync(_factory, enrollmentId)).Seats);
            Assert.AreEqual(world.RemainingVisits - 1, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, stranger);
            await DeleteAsync(world);
        }
    }

    private static async Task AssertConflictAsync(long userId, long enrollmentId, string reason, int seats = 2)
    {
        var response = await SeatsAsync(await Scenario.TokenAsync(_factory, userId), enrollmentId, $$"""{"seats":{{seats}}}""");
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(reason, document.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(409, document.RootElement.GetProperty("status").GetInt32());
    }

    private static void AssertCard(string json, Scenario.World world, long enrollmentId, string status, int seats, int visits)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "id", "status", "seats", "createdAt", "class", "pass" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(enrollmentId, root.GetProperty("id").GetInt64());
        Assert.AreEqual(status, root.GetProperty("status").GetString());
        Assert.AreEqual(seats, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(world.ClassId, root.GetProperty("class").GetProperty("id").GetInt64());
        Assert.AreEqual(world.RoomName, root.GetProperty("class").GetProperty("room").GetProperty("name").GetString());
        Assert.AreEqual(visits, root.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    private static async Task<long> BookAsync(string token, Scenario.World world, int seats, string key)
    {
        var response = await Scenario.SendAsync(
            _client,
            HttpMethod.Post,
            "/enrollments",
            token,
            $$"""{"classId":{{world.ClassId}},"seats":{{seats}}}""",
            key);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> SeatsAsync(string token, long enrollmentId, string json)
    {
        return Scenario.SendAsync(_client, HttpMethod.Post, $"/enrollments/{enrollmentId}/seats", token, json);
    }

    private static Task<HttpResponseMessage> GetAsync(string token, long enrollmentId)
    {
        return Scenario.SendAsync(_client, HttpMethod.Get, $"/enrollments/{enrollmentId}", token);
    }

    private static async Task DeleteAsync(Scenario.World world)
    {
        await Scenario.DeleteUserAsync(_factory, world.UserId);
        await Scenario.DeleteRoomAsync(_factory, world.RoomId);
    }
}
