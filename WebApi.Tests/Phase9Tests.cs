using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class Phase9Tests
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
    public async Task UserIsForbiddenAndAdminReceivesTheSummary()
    {
        var admin = await Scenario.CreateWorldAsync(_factory, role: "admin", startsAt: new DateTime(2031, 3, 1, 10, 0, 0, DateTimeKind.Utc), capacity: 10);
        var user = await Scenario.AddUserAsync(_factory);
        var roomB = await Scenario.CreateWorldAsync(_factory, startsAt: new DateTime(2031, 3, 1, 11, 0, 0, DateTimeKind.Utc), capacity: 4);
        try
        {
            await Scenario.AddClassAsync(_factory, admin.RoomId, new DateTime(2031, 3, 1, 18, 0, 0, DateTimeKind.Utc), capacity: 5, title: "Evening");
            await Scenario.AddClassAsync(_factory, admin.RoomId, new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc), capacity: 100, cancelled: true);
            await Scenario.AddClassAsync(_factory, admin.RoomId, new DateTime(2031, 3, 2, 10, 0, 0, DateTimeKind.Utc), capacity: 7);
            await Scenario.AddEnrollmentAsync(_factory, admin.UserId, admin.ClassId, 3);
            await Scenario.AddEnrollmentAsync(_factory, admin.UserId, admin.ClassId, 4, "cancelled");
            var secondDay = await FindClassAsync(admin.RoomId, new DateTime(2031, 3, 2, 10, 0, 0, DateTimeKind.Utc));
            await Scenario.AddEnrollmentAsync(_factory, user, secondDay, 2);
            await Scenario.AddEnrollmentAsync(_factory, user, roomB.ClassId, 1);

            var forbidden = await GetAsync(await Scenario.TokenAsync(_factory, user), "from=2031-03-01&to=2031-03-02");
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

            var json = await ReadAsync(await Scenario.TokenAsync(_factory, admin.UserId), "from=2031-03-01&to=2031-03-03");
            var items = json.GetProperty("items");
            Assert.AreEqual(3, items.GetArrayLength());
            AssertRow(items[0], "2031-03-01", admin.RoomId, admin.RoomName, 2, 15, 3);
            AssertRow(items[1], "2031-03-01", roomB.RoomId, roomB.RoomName, 1, 4, 1);
            AssertRow(items[2], "2031-03-02", admin.RoomId, admin.RoomName, 1, 7, 2);

            var sameDay = await ReadAsync(await Scenario.TokenAsync(_factory, admin.UserId), "from=2031-03-02&to=2031-03-02");
            Assert.AreEqual(1, sameDay.GetProperty("items").GetArrayLength());
            Assert.AreEqual("2031-03-02", sameDay.GetProperty("items")[0].GetProperty("date").GetString());
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, user);
            await DeleteAsync(roomB);
            await DeleteAsync(admin);
        }
    }

    [TestMethod]
    public async Task LateUtcClassStaysOnItsOwnCalendarDate()
    {
        var startsAt = new DateTime(2031, 4, 1, 23, 30, 0, DateTimeKind.Utc);
        var world = await Scenario.CreateWorldAsync(_factory, role: "admin", startsAt: startsAt, capacity: 6);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var sameDay = await ReadAsync(token, $"from=2031-04-01&to=2031-04-01&roomId={world.RoomId}");
            var nextDay = await ReadAsync(token, $"from=2031-04-02&to=2031-04-02&roomId={world.RoomId}");

            Assert.AreEqual(1, sameDay.GetProperty("items").GetArrayLength());
            Assert.AreEqual("2031-04-01", sameDay.GetProperty("items")[0].GetProperty("date").GetString());
            Assert.AreEqual(6, sameDay.GetProperty("items")[0].GetProperty("totalCapacity").GetInt32());
            Assert.AreEqual(0, nextDay.GetProperty("items").GetArrayLength());
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task UnknownRoomIsEmptyAndBadDatesAreRejected()
    {
        var admin = await Scenario.CreateWorldAsync(_factory, role: "admin");
        var user = await Scenario.AddUserAsync(_factory);
        try
        {
            var token = await Scenario.TokenAsync(_factory, admin.UserId);
            var empty = await ReadAsync(token, "from=2031-05-01&to=2031-05-01&roomId=9223372036854775807");
            Assert.AreEqual(0, empty.GetProperty("items").GetArrayLength());
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2031-05-01")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2031-05-02&to=2031-05-01")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2031-05-01T00:00:00Z&to=2031-05-02")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2031-05-01&to=2031-05-02&roomId=abc")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await GetAsync(await Scenario.TokenAsync(_factory, user), "from=2031-05-01&to=2031-05-01")).StatusCode);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, user);
            await DeleteAsync(admin);
        }
    }

    private static void AssertRow(JsonElement item, string date, long roomId, string roomName, int classes, int capacity, int booked)
    {
        CollectionAssert.AreEquivalent(
            new[] { "date", "room", "classesCount", "totalCapacity", "bookedSeats" },
            item.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(date, item.GetProperty("date").GetString());
        Assert.AreEqual(roomId, item.GetProperty("room").GetProperty("id").GetInt64());
        Assert.AreEqual(roomName, item.GetProperty("room").GetProperty("name").GetString());
        Assert.AreEqual(classes, item.GetProperty("classesCount").GetInt32());
        Assert.AreEqual(capacity, item.GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(booked, item.GetProperty("bookedSeats").GetInt32());
    }

    private static async Task<long> FindClassAsync(long roomId, DateTime startsAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        return await db.Set<Class>().Where(x => x.RoomId == roomId && x.StartsAt == startsAt).Select(x => x.Id).SingleAsync();
    }

    private static async Task<JsonElement> ReadAsync(string token, string query)
    {
        var response = await GetAsync(token, query);
        var json = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> GetAsync(string token, string query)
    {
        return Scenario.SendAsync(_client, HttpMethod.Get, $"/analytics/load?{query}", token);
    }

    private static async Task DeleteAsync(Scenario.World world)
    {
        await Scenario.DeleteUserAsync(_factory, world.UserId);
        await Scenario.DeleteRoomAsync(_factory, world.RoomId);
    }
}
