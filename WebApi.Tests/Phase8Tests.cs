using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class Phase8Tests
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
    public async Task PeriodIncludesTheStartExcludesTheEndAndHidesCancelledClasses()
    {
        var from = new DateTime(2030, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(5);
        var world = await Scenario.CreateWorldAsync(_factory, startsAt: from, capacity: 10);
        Scenario.World? other = null;
        try
        {
            var inside = await Scenario.AddClassAsync(_factory, world.RoomId, from.AddHours(2), capacity: 10, title: "Inside");
            await Scenario.AddClassAsync(_factory, world.RoomId, to, title: "End");
            await Scenario.AddClassAsync(_factory, world.RoomId, from.AddHours(-1), title: "Before");
            await Scenario.AddClassAsync(_factory, world.RoomId, from.AddHours(1), cancelled: true, title: "Cancelled");
            other = await Scenario.CreateWorldAsync(_factory, startsAt: from.AddHours(1));
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            await BookAsync(token, world.ClassId, 2, "mine");
            await BookAsync(token, world.ClassId, 1, "mine-again");
            await Scenario.AddEnrollmentAsync(_factory, world.UserId, world.ClassId, 5, "cancelled");
            await Scenario.AddEnrollmentAsync(_factory, other.UserId, inside, 9);

            var json = await ReadAsync(token, from, to, world.RoomId, limit: 20);
            var items = json.GetProperty("items");
            Assert.AreEqual(2, items.GetArrayLength());
            Assert.AreEqual(world.ClassId, items[0].GetProperty("classId").GetInt64());
            Assert.AreEqual(inside, items[1].GetProperty("classId").GetInt64());
            Assert.AreEqual(JsonValueKind.Null, json.GetProperty("continuationToken").ValueKind);
            Assert.AreEqual(7, items[0].GetProperty("freeSeats").GetInt32());
            Assert.AreEqual(3, items[0].GetProperty("mySeats").GetInt32());
            Assert.AreEqual(1, items[1].GetProperty("freeSeats").GetInt32());
            Assert.AreEqual(0, items[1].GetProperty("mySeats").GetInt32());
            Assert.AreEqual(world.RoomName, items[0].GetProperty("room").GetProperty("name").GetString());
            CollectionAssert.AreEquivalent(
                new[] { "classId", "title", "startsAt", "durationMinutes", "room", "capacity", "freeSeats", "mySeats" },
                items[0].EnumerateObject().Select(property => property.Name).ToArray());

            var empty = await ReadAsync(token, from, to, long.MaxValue, limit: 20);
            Assert.AreEqual(0, empty.GetProperty("items").GetArrayLength());
        }
        finally
        {
            if (other is not null)
            {
                await Scenario.DeleteUserAsync(_factory, other.UserId);
                await Scenario.DeleteRoomAsync(_factory, other.RoomId);
            }

            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task PagesStayOrderedAcrossEqualStartTimesAndEndOnAnEmptyTail()
    {
        var start = new DateTime(2030, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var world = await Scenario.CreateWorldAsync(_factory, startsAt: start);
        try
        {
            var tied = await Scenario.AddClassAsync(_factory, world.RoomId, start, title: "Tied");
            var later = await Scenario.AddClassAsync(_factory, world.RoomId, start.AddHours(2), title: "Later");
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var first = await ReadAsync(token, start, start.AddDays(1), world.RoomId, limit: 1);
            var firstItems = first.GetProperty("items");
            Assert.AreEqual(1, firstItems.GetArrayLength());
            Assert.AreEqual(world.ClassId, firstItems[0].GetProperty("classId").GetInt64());
            var cursor = first.GetProperty("continuationToken").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

            var second = await ReadAsync(token, start, start.AddDays(1), world.RoomId, limit: 1, continuationToken: cursor);
            Assert.AreEqual(tied, second.GetProperty("items")[0].GetProperty("classId").GetInt64());
            var third = await ReadAsync(
                token,
                start,
                start.AddDays(1),
                world.RoomId,
                limit: 1,
                continuationToken: second.GetProperty("continuationToken").GetString());
            Assert.AreEqual(later, third.GetProperty("items")[0].GetProperty("classId").GetInt64());
            Assert.AreEqual(JsonValueKind.Null, third.GetProperty("continuationToken").ValueKind);

            var tail = await ReadAsync(
                token,
                start,
                start.AddDays(1),
                world.RoomId,
                limit: 1,
                continuationToken: await TokenAfterAsync(later));
            Assert.AreEqual(0, tail.GetProperty("items").GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, tail.GetProperty("continuationToken").ValueKind);
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task DefaultPageSizeIsTwenty()
    {
        var start = new DateTime(2030, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var world = await Scenario.CreateWorldAsync(_factory, startsAt: start);
        try
        {
            for (var hour = 1; hour < 21; hour++)
            {
                await Scenario.AddClassAsync(_factory, world.RoomId, start.AddHours(hour));
            }

            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var page = await ReadAsync(token, start, start.AddDays(2), world.RoomId, limit: null);
            Assert.AreEqual(20, page.GetProperty("items").GetArrayLength());
            Assert.AreEqual(JsonValueKind.String, page.GetProperty("continuationToken").ValueKind);
            var next = await ReadAsync(
                token,
                start,
                start.AddDays(2),
                world.RoomId,
                limit: null,
                continuationToken: page.GetProperty("continuationToken").GetString());
            Assert.AreEqual(1, next.GetProperty("items").GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, next.GetProperty("continuationToken").ValueKind);
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task FreeSeatsDoNotFallBelowZero()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 2, startsAt: new DateTime(2030, 4, 1, 12, 0, 0, DateTimeKind.Utc));
        try
        {
            await Scenario.AddEnrollmentAsync(_factory, world.UserId, world.ClassId, 5);
            var page = await ReadAsync(
                await Scenario.TokenAsync(_factory, world.UserId),
                new DateTime(2030, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2030, 4, 2, 0, 0, 0, DateTimeKind.Utc),
                world.RoomId,
                limit: 20);
            Assert.AreEqual(0, page.GetProperty("items")[0].GetProperty("freeSeats").GetInt32());
            Assert.AreEqual(5, page.GetProperty("items")[0].GetProperty("mySeats").GetInt32());
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task BadPeriodLimitRoomAndTokenAreRejected()
    {
        var world = await Scenario.CreateWorldAsync(_factory);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, null)).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2026-09-22T10:00:00Z")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2026-09-22T10:00:00Z&to=2026-09-22T10:00:00Z")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=not-a-date&to=2026-09-23T00:00:00Z")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2026-09-22T00:00:00Z&to=2026-09-23T00:00:00Z&roomId=abc")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2026-09-22T00:00:00Z&to=2026-09-23T00:00:00Z&limit=0")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2026-09-22T00:00:00Z&to=2026-09-23T00:00:00Z&limit=101")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await GetAsync(token, "from=2026-09-22T00:00:00Z&to=2026-09-23T00:00:00Z&continuationToken=%%%")).StatusCode);
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    private static async Task<JsonElement> ReadAsync(
        string token,
        DateTime from,
        DateTime to,
        long roomId,
        int? limit,
        string? continuationToken = null)
    {
        var query = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&roomId={roomId}";

        if (limit is not null)
        {
            query += $"&limit={limit}";
        }

        if (continuationToken is not null)
        {
            query += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
        }

        var response = await GetAsync(token, query);
        var json = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<string> TokenAfterAsync(long classId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataAccessLayer.UnitOfWork.IUnitOfWork>().DbContext;
        var startsAt = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.Set<DataAccessLayer.Entities.Class>().Where(x => x.Id == classId).Select(x => x.StartsAt));
        return WebApi.Schedule.ContinuationToken.Create(startsAt, classId);
    }

    private static Task<HttpResponseMessage> GetAsync(string token, string? query)
    {
        return Scenario.SendAsync(_client, HttpMethod.Get, query is null ? "/schedule" : $"/schedule?{query}", token);
    }

    private static async Task BookAsync(string token, long classId, int seats, string key)
    {
        var response = await Scenario.SendAsync(
            _client,
            HttpMethod.Post,
            "/enrollments",
            token,
            $$"""{"classId":{{classId}},"seats":{{seats}}}""",
            key);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task DeleteAsync(Scenario.World world)
    {
        await Scenario.DeleteUserAsync(_factory, world.UserId);
        await Scenario.DeleteRoomAsync(_factory, world.RoomId);
    }
}
