using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class Phase7Tests
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
    public async Task RefundsAtLeastTwoHoursAheadAndRepeatsTheStoredAmount()
    {
        var world = await Scenario.CreateWorldAsync(_factory, remainingVisits: 10, startsAt: DateTime.UtcNow.AddHours(3));
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(token, world, 2, "ahead");
            var first = await CancelAsync(token, enrollmentId);
            await MoveClassAsync(world.ClassId, DateTime.UtcNow.AddMinutes(-5), cancelled: false);
            var second = await CancelAsync(token, enrollmentId);

            AssertCancel(await first.Content.ReadAsStringAsync(), enrollmentId, 2);
            Assert.AreEqual(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
            Assert.AreEqual(10, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
            var stored = await Scenario.EnrollmentAsync(_factory, enrollmentId);
            Assert.AreEqual("cancelled", stored.Status);
            Assert.AreEqual(2, stored.Seats);
            Assert.AreEqual(2, stored.RefundedVisits);
            Assert.AreEqual(0, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task FreesSeatsWithoutARefundInsideTwoHours()
    {
        var world = await Scenario.CreateWorldAsync(_factory, remainingVisits: 10, startsAt: DateTime.UtcNow.AddHours(3));
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(token, world, 2, "soon");
            await MoveClassAsync(world.ClassId, DateTime.UtcNow.AddMinutes(30), cancelled: false);
            var first = await CancelAsync(token, enrollmentId);
            await MoveClassAsync(world.ClassId, DateTime.UtcNow.AddHours(5), cancelled: false);
            var second = await CancelAsync(token, enrollmentId);

            AssertCancel(await first.Content.ReadAsStringAsync(), enrollmentId, 0);
            AssertCancel(await second.Content.ReadAsStringAsync(), enrollmentId, 0);
            Assert.AreEqual(8, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
            Assert.AreEqual(0, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task StartedBookingIsRejectedUntilTheClubCancelsTheClass()
    {
        var world = await Scenario.CreateWorldAsync(_factory, remainingVisits: 10, startsAt: DateTime.UtcNow.AddHours(3));
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(token, world, 2, "started");
            await MoveClassAsync(world.ClassId, DateTime.UtcNow.AddMinutes(-1), cancelled: false);
            var rejected = await CancelAsync(token, enrollmentId);

            Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
            using (var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("class-started", document.RootElement.GetProperty("reason").GetString());
            }

            Assert.AreEqual(8, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
            Assert.AreEqual("active", (await Scenario.EnrollmentAsync(_factory, enrollmentId)).Status);

            await MoveClassAsync(world.ClassId, DateTime.UtcNow.AddMinutes(-1), cancelled: true);
            var accepted = await CancelAsync(token, enrollmentId);
            AssertCancel(await accepted.Content.ReadAsStringAsync(), enrollmentId, 2);
            Assert.AreEqual(10, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
        }
        finally
        {
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task PromotesFittingWaitersAndChargesEachOnce()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 2, remainingVisits: 10, startsAt: DateTime.UtcNow.AddHours(4));
        var first = await Scenario.AddUserAsync(_factory, remainingVisits: 5);
        var second = await Scenario.AddUserAsync(_factory, remainingVisits: 5);
        try
        {
            var owner = await Scenario.TokenAsync(_factory, world.UserId);
            await BookAsync(owner, world, 2, "full");
            var front = await JoinAsync(await Scenario.TokenAsync(_factory, first), world.ClassId, 1, "front");
            var back = await JoinAsync(await Scenario.TokenAsync(_factory, second), world.ClassId, 1, "back");
            var response = await CancelAsync(owner, await OnlyEnrollmentAsync(world.UserId, world.ClassId));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(10, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
            Assert.AreEqual(4, await Scenario.RemainingVisitsAsync(_factory, first));
            Assert.AreEqual(4, await Scenario.RemainingVisitsAsync(_factory, second));
            Assert.AreEqual(2, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
            await AssertEnrolledAsync(front);
            await AssertEnrolledAsync(back);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, second);
            await Scenario.DeleteUserAsync(_factory, first);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task DoesNotLeapfrogAWaiterWhoNeedsMoreSeatsThanAreFree()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 4, remainingVisits: 10, startsAt: DateTime.UtcNow.AddHours(4));
        var stayer = await Scenario.AddUserAsync(_factory, remainingVisits: 10);
        var head = await Scenario.AddUserAsync(_factory, remainingVisits: 6);
        var tail = await Scenario.AddUserAsync(_factory, remainingVisits: 6);
        try
        {
            await BookAsync(await Scenario.TokenAsync(_factory, stayer), world, 3, "stay");
            var owner = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(owner, world, 1, "owner");
            var headId = await JoinAsync(await Scenario.TokenAsync(_factory, head), world.ClassId, 2, "head");
            var tailId = await JoinAsync(await Scenario.TokenAsync(_factory, tail), world.ClassId, 1, "tail");
            var response = await CancelAsync(owner, enrollmentId);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("waiting", (await Scenario.WaitlistAsync(_factory, headId))!.Status);
            Assert.AreEqual("waiting", (await Scenario.WaitlistAsync(_factory, tailId))!.Status);
            Assert.AreEqual(6, await Scenario.RemainingVisitsAsync(_factory, head));
            Assert.AreEqual(6, await Scenario.RemainingVisitsAsync(_factory, tail));
            Assert.AreEqual(3, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, tail);
            await Scenario.DeleteUserAsync(_factory, head);
            await Scenario.DeleteUserAsync(_factory, stayer);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task SkipsAnUnusablePassAndPromotesTheNextWaiter()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 2, remainingVisits: 4, startsAt: DateTime.UtcNow.AddHours(4));
        var expired = await Scenario.AddUserAsync(_factory, remainingVisits: 5, validUntil: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        var ready = await Scenario.AddUserAsync(_factory, remainingVisits: 5);
        try
        {
            var owner = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(owner, world, 2, "full");
            var expiredId = await JoinAsync(await Scenario.TokenAsync(_factory, expired), world.ClassId, 1, "expired");
            await JoinAsync(await Scenario.TokenAsync(_factory, ready), world.ClassId, 1, "ready");
            Assert.AreEqual(HttpStatusCode.OK, (await CancelAsync(owner, enrollmentId)).StatusCode);

            Assert.AreEqual("waiting", (await Scenario.WaitlistAsync(_factory, expiredId))!.Status);
            Assert.AreEqual(5, await Scenario.RemainingVisitsAsync(_factory, expired));
            Assert.AreEqual(4, await Scenario.RemainingVisitsAsync(_factory, ready));
            Assert.AreEqual(1, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, ready);
            await Scenario.DeleteUserAsync(_factory, expired);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task PromotesAgainstTheFreeSeatsLeftAfterTheCancellation()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 4, remainingVisits: 10, startsAt: DateTime.UtcNow.AddHours(4));
        var stayer = await Scenario.AddUserAsync(_factory, remainingVisits: 10);
        var waiter = await Scenario.AddUserAsync(_factory, remainingVisits: 6);
        try
        {
            await BookAsync(await Scenario.TokenAsync(_factory, stayer), world, 1, "stay");
            var owner = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(owner, world, 1, "owner");
            var entryId = await JoinAsync(await Scenario.TokenAsync(_factory, waiter), world.ClassId, 3, "three");
            Assert.AreEqual(HttpStatusCode.OK, (await CancelAsync(owner, enrollmentId)).StatusCode);

            await AssertEnrolledAsync(entryId);
            Assert.AreEqual(3, await Scenario.RemainingVisitsAsync(_factory, waiter));
            Assert.AreEqual(4, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, waiter);
            await Scenario.DeleteUserAsync(_factory, stayer);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task ClubCancellationRefundsFullyAndDoesNotPromote()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 2, remainingVisits: 8, startsAt: DateTime.UtcNow.AddHours(4));
        var waiter = await Scenario.AddUserAsync(_factory, remainingVisits: 5);
        try
        {
            var owner = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(owner, world, 2, "full");
            var entryId = await JoinAsync(await Scenario.TokenAsync(_factory, waiter), world.ClassId, 1, "wait");
            await MoveClassAsync(world.ClassId, DateTime.UtcNow.AddMinutes(-20), cancelled: true);
            var response = await CancelAsync(owner, enrollmentId);

            AssertCancel(await response.Content.ReadAsStringAsync(), enrollmentId, 2);
            Assert.AreEqual(8, await Scenario.RemainingVisitsAsync(_factory, world.UserId));
            Assert.AreEqual(5, await Scenario.RemainingVisitsAsync(_factory, waiter));
            Assert.AreEqual("waiting", (await Scenario.WaitlistAsync(_factory, entryId))!.Status);
            Assert.AreEqual(0, await Scenario.ActiveSeatsAsync(_factory, world.ClassId));
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, waiter);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task ADepartedWaiterIsNotPromotedLater()
    {
        var world = await Scenario.CreateWorldAsync(_factory, capacity: 1, remainingVisits: 4, startsAt: DateTime.UtcNow.AddHours(4));
        var departed = await Scenario.AddUserAsync(_factory, remainingVisits: 3);
        var staying = await Scenario.AddUserAsync(_factory, remainingVisits: 3);
        try
        {
            var owner = await Scenario.TokenAsync(_factory, world.UserId);
            var enrollmentId = await BookAsync(owner, world, 1, "full");
            var left = await JoinAsync(await Scenario.TokenAsync(_factory, departed), world.ClassId, 1, "left");
            Assert.AreEqual(HttpStatusCode.OK, (await Scenario.SendAsync(_client, HttpMethod.Post, $"/enrollments/waitlist/{left}/cancel", await Scenario.TokenAsync(_factory, departed))).StatusCode);
            var kept = await JoinAsync(await Scenario.TokenAsync(_factory, staying), world.ClassId, 1, "kept");
            Assert.AreEqual(HttpStatusCode.OK, (await CancelAsync(owner, enrollmentId)).StatusCode);

            Assert.AreEqual("cancelled", (await Scenario.WaitlistAsync(_factory, left))!.Status);
            Assert.AreEqual(3, await Scenario.RemainingVisitsAsync(_factory, departed));
            await AssertEnrolledAsync(kept);
            Assert.AreEqual(2, await Scenario.RemainingVisitsAsync(_factory, staying));
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, staying);
            await Scenario.DeleteUserAsync(_factory, departed);
            await DeleteAsync(world);
        }
    }

    [TestMethod]
    public async Task PromotionRefreshesTheWaiterCardAndAForeignCancelIsHidden()
    {
        var booked = await Scenario.CreateWorldAsync(_factory, capacity: 4, remainingVisits: 9, startsAt: DateTime.UtcNow.AddHours(5));
        var queue = await Scenario.CreateWorldAsync(_factory, capacity: 1, remainingVisits: 4, startsAt: DateTime.UtcNow.AddHours(5));
        var stranger = await Scenario.AddUserAsync(_factory);
        try
        {
            var waiterToken = await Scenario.TokenAsync(_factory, booked.UserId);
            var cardId = await BookAsync(waiterToken, booked, 1, "card");
            Assert.AreEqual(HttpStatusCode.OK, (await Scenario.SendAsync(_client, HttpMethod.Get, $"/enrollments/{cardId}", waiterToken)).StatusCode);
            var owner = await Scenario.TokenAsync(_factory, queue.UserId);
            var enrollmentId = await BookAsync(owner, queue, 1, "full");
            await JoinAsync(waiterToken, queue.ClassId, 1, "queue");
            Assert.AreEqual(HttpStatusCode.NotFound, (await CancelAsync(await Scenario.TokenAsync(_factory, stranger), enrollmentId)).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Scenario.SendAsync(_client, HttpMethod.Post, "/enrollments/nope/cancel", owner)).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await CancelAsync(owner, enrollmentId)).StatusCode);

            var card = await Scenario.SendAsync(_client, HttpMethod.Get, $"/enrollments/{cardId}", waiterToken);
            using var document = JsonDocument.Parse(await card.Content.ReadAsStringAsync());
            Assert.AreEqual(7, document.RootElement.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, stranger);
            await DeleteAsync(queue);
            await DeleteAsync(booked);
        }
    }

    private static void AssertCancel(string json, long enrollmentId, int refunded)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "id", "status", "refundedVisits" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(enrollmentId, root.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", root.GetProperty("status").GetString());
        Assert.AreEqual(refunded, root.GetProperty("refundedVisits").GetInt32());
    }

    private static async Task AssertEnrolledAsync(long entryId)
    {
        var entry = (await Scenario.WaitlistAsync(_factory, entryId))!;
        Assert.AreEqual("enrolled", entry.Status);
        Assert.IsNotNull(entry.EnrollmentId);
        var enrollment = await Scenario.EnrollmentAsync(_factory, entry.EnrollmentId.Value);
        Assert.AreEqual("active", enrollment.Status);
        Assert.AreEqual(entry.Seats, enrollment.Seats);
        Assert.AreEqual(entry.UserId, enrollment.UserId);
    }

    private static async Task<long> OnlyEnrollmentAsync(long userId, long classId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        return await db.Set<Enrollment>().Where(x => x.UserId == userId && x.ClassId == classId).Select(x => x.Id).SingleAsync();
    }

    private static async Task MoveClassAsync(long classId, DateTime startsAt, bool cancelled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var scheduled = await db.Set<Class>().IgnoreQueryFilters().SingleAsync(x => x.Id == classId);
        scheduled.StartsAt = startsAt;
        scheduled.IsCancelled = cancelled;
        await db.SaveChangesAsync();
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

    private static async Task<long> JoinAsync(string token, long classId, int seats, string key)
    {
        var response = await Scenario.SendAsync(
            _client,
            HttpMethod.Post,
            "/enrollments/waitlist",
            token,
            $$"""{"classId":{{classId}},"seats":{{seats}}}""",
            key);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> CancelAsync(string token, long enrollmentId)
    {
        return Scenario.SendAsync(_client, HttpMethod.Post, $"/enrollments/{enrollmentId}/cancel", token);
    }

    private static async Task DeleteAsync(Scenario.World world)
    {
        await Scenario.DeleteUserAsync(_factory, world.UserId);
        await Scenario.DeleteRoomAsync(_factory, world.RoomId);
    }
}
