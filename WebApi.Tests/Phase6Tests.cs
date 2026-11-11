using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class Phase6Tests
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
    public async Task JoinDoesNotSpendVisitsAndNumbersTheQueueFromOne()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 1, remainingVisits: 10);
        var second = await Scenario.AddUserAsync(_factory, remainingVisits: 4);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            await BookAsync(token, world, 1, "fill");
            var first = await JoinAsync(token, world.ClassId, 1, "wait-a");
            var secondJoin = await JoinAsync(await Scenario.TokenAsync(_factory, second), world.ClassId, 1, "wait-b");

            Assert.AreEqual(1, first.Position);
            Assert.AreEqual(2, secondJoin.Position);
            Assert.AreEqual(world.ClassId, first.ClassId);
            Assert.AreEqual(1, first.Seats);
            Assert.AreEqual(9, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
            Assert.AreEqual(4, await Scenario.RemainingVisitsAsync(_factory, second));
            Assert.AreEqual(1, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
            Assert.AreEqual("waiting", (await Scenario.WaitlistAsync(_factory, first.Id))!.Status);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, second);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task ActiveEnrollmentDoesNotBlockWaitingForMoreSeats()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 3, remainingVisits: 10);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            await BookAsync(token, world, 1, "own");
            var joined = await JoinAsync(token, world.ClassId, 3, "more");

            Assert.AreEqual(1, joined.Position);
            Assert.AreEqual(9, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task JoinRejectsAvailableSeatsASecondEntryAndAClosedClass()
    {
        var open = await Scenario.CreateWorldAsync(_factory, capacity: 5);
        var full = await Scenario.CreateWorldAsync(_factory, capacity: 1, remainingVisits: 5);
        var started = await Scenario.CreateWorldAsync(_factory, capacity: 1, startsAt: DateTime.UtcNow.AddMinutes(-1));
        var cancelled = await Scenario.CreateWorldAsync(_factory, cancelled: true);
        try
        {
            await AssertConflictAsync(open.UserId, open.ClassId, 1, "open", "seats-available");
            var token = await Scenario.TokenAsync(_factory, full.UserId);
            await BookAsync(token, full, 1, "fill");
            await JoinAsync(token, full.ClassId, 1, "first");
            await AssertConflictAsync(full.UserId, full.ClassId, 1, "second", "already-waiting");
            await AssertConflictAsync(started.UserId, started.ClassId, 1, "started", "class-started");
            await AssertConflictAsync(cancelled.UserId, cancelled.ClassId, 1, "cancelled", "class-cancelled");
            Assert.AreEqual(HttpStatusCode.NotFound, (await PostAsync(await Scenario.TokenAsync(_factory, open.UserId), long.MaxValue, 1, "missing")).StatusCode);
            Assert.AreEqual(open.RemainingVisits, await Scenario.RemainingVisitsAsync(_factory, open.UserId));
        }
        finally
        {
            await DeleteAsync(cancelled);
            await DeleteAsync(started);
            await DeleteAsync(full);
            await DeleteAsync(open);
        }
    }

    [TestMethod]
    public async Task ReplayReturnsTheFirstBodyAndRejectsADifferentOne()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 1, remainingVisits: 6);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            await BookAsync(token, world, 1, "fill");
            var firstResponse = await PostAsync(token, world.ClassId, 1, "same-key");
            var firstJson = await firstResponse.Content.ReadAsStringAsync();
            var secondResponse = await PostAsync(token, world.ClassId, 1, "same-key");
            var mismatch = await PostAsync(token, world.ClassId, 2, "same-key");

            Assert.AreEqual(HttpStatusCode.Created, secondResponse.StatusCode);
            using var firstDocument = JsonDocument.Parse(firstJson);
            using var secondDocument = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            Assert.AreEqual(firstDocument.RootElement.GetProperty("id").GetInt64(), secondDocument.RootElement.GetProperty("id").GetInt64());
            Assert.AreEqual(firstDocument.RootElement.GetProperty("position").GetInt32(), secondDocument.RootElement.GetProperty("position").GetInt32());
            Assert.AreEqual(firstDocument.RootElement.GetProperty("createdAt").GetDateTime(), secondDocument.RootElement.GetProperty("createdAt").GetDateTime());
            Assert.AreEqual(HttpStatusCode.Conflict, mismatch.StatusCode);
            using var document = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
            Assert.AreEqual("idempotency-mismatch", document.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(1, await CountWaitingAsync(world.UserId));
            Assert.AreEqual(5, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task BlankKeyAndNonPositiveSeatsAreBadRequest()
    {
        var world = await Scenario.CreateWorldAsync(_factory);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await PostAsync(token, world.ClassId, 1, null)).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await PostAsync(token, world.ClassId, 1, "   ")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await PostAsync(token, world.ClassId, 0, "seats")).StatusCode);
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task LeaveRemovesTheEntryAndARepeatChangesNothing()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 1);
        var behind = await Scenario.AddUserAsync(_factory);
        var later = await Scenario.AddUserAsync(_factory);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            await BookAsync(token, world, 1, "fill");
            var first = await JoinAsync(token, world.ClassId, 1, "front");
            var second = await JoinAsync(await Scenario.TokenAsync(_factory, behind), world.ClassId, 1, "behind");
            var leave = await LeaveAsync(token, first.Id);
            var repeat = await LeaveAsync(token, first.Id);
            var foreign = await LeaveAsync(await Scenario.TokenAsync(_factory, later), second.Id);
            var joined = await JoinAsync(await Scenario.TokenAsync(_factory, later), world.ClassId, 1, "later");

            Assert.AreEqual(HttpStatusCode.OK, leave.StatusCode);
            AssertLeft(await leave.Content.ReadAsStringAsync(), first.Id);
            Assert.AreEqual(await leave.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
            Assert.AreEqual("cancelled", (await Scenario.WaitlistAsync(_factory, first.Id))!.Status);
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.AreEqual("waiting", (await Scenario.WaitlistAsync(_factory, second.Id))!.Status);
            Assert.AreEqual(2, joined.Position);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Scenario.SendAsync(_client, HttpMethod.Post, "/enrollments/waitlist/abc/cancel", token)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await LeaveAsync(token, long.MaxValue)).StatusCode);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, later);
            await Scenario.DeleteUserAsync(_factory, behind);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task LeavingAnEnrolledEntryIsNotFound()
    {
        var world = await Scenario.CreateWorldAsync(_factory);
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataAccessLayer.UnitOfWork.IUnitOfWork>().DbContext;
            var entry = new DataAccessLayer.Entities.WaitlistEntry
            {
                UserId = world.UserId,
                ClassId = world.ClassId,
                Seats = 1,
                Status = "enrolled",
                CreatedAt = DateTime.UtcNow
            };
            db.Set<DataAccessLayer.Entities.WaitlistEntry>().Add(entry);
            await db.SaveChangesAsync();

            var response = await LeaveAsync(await Scenario.TokenAsync(_factory, world.UserId), entry.Id);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual("enrolled", (await Scenario.WaitlistAsync(_factory, entry.Id))!.Status);
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    private static async Task AssertConflictAsync(long userId, long classId, int seats, string key, string reason)
    {
        var response = await PostAsync(await Scenario.TokenAsync(_factory, userId), classId, seats, key);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(reason, document.RootElement.GetProperty("reason").GetString());
    }

    private static async Task<Joined> JoinAsync(string token, long classId, int seats, string key)
    {
        var response = await PostAsync(token, classId, seats, key);
        var json = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "id", "classId", "seats", "position", "createdAt" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        return new Joined(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("classId").GetInt64(),
            root.GetProperty("seats").GetInt32(),
            root.GetProperty("position").GetInt32());
    }

    private static void AssertLeft(string json, long id)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEquivalent(new[] { "id", "status" }, root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(id, root.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", root.GetProperty("status").GetString());
    }

    private static async Task<int> CountWaitingAsync(long userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataAccessLayer.UnitOfWork.IUnitOfWork>().DbContext;
        return await db.Set<DataAccessLayer.Entities.WaitlistEntry>().CountAsync(x => x.UserId == userId && x.Status == "waiting");
    }

    private static Task<HttpResponseMessage> PostAsync(string token, long classId, int seats, string? key)
    {
        return Scenario.SendAsync(
            _client,
            HttpMethod.Post,
            "/enrollments/waitlist",
            token,
            $$"""{"classId":{{classId}},"seats":{{seats}}}""",
            key);
    }

    private static async Task BookAsync(string token, Scenario.World world, int seats, string key)
    {
        var response = await Scenario.SendAsync(
            _client,
            HttpMethod.Post,
            "/enrollments",
            token,
            $$"""{"classId":{{world.ClassId}},"seats":{{seats}}}""",
            key);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> LeaveAsync(string token, long entryId)
    {
        return Scenario.SendAsync(_client, HttpMethod.Post, $"/enrollments/waitlist/{entryId}/cancel", token);
    }

    private static async Task DeleteAsync(Scenario.World world)
    {
        await Scenario.DeleteUserAsync(_factory, world.UserId);
        await Scenario.DeleteRoomAsync(_factory, world.RoomId);
    }

    private sealed record Joined(long Id, long ClassId, int Seats, int Position);
}
