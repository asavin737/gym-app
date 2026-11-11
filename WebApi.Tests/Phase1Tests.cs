using System.Reflection;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApi.Controllers;
using WebApi.Data;
using WebApi.Routing;
using WebApi.Time;

namespace WebApi.Tests;

[TestClass]
public class Phase1Tests
{
    private static ApiApplicationFactory _factory = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _factory = new ApiApplicationFactory();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _factory.Dispose();
    }

    [TestMethod]
    public void RouteIdAcceptsIntegerAndRejectsNonInteger()
    {
        Assert.IsTrue(RouteId.TryParse("1", out var positive));
        Assert.AreEqual(1L, positive);
        Assert.IsTrue(RouteId.TryParse("-1", out var negative));
        Assert.AreEqual(-1L, negative);
        Assert.IsFalse(RouteId.TryParse("abc", out _));
        Assert.IsFalse(RouteId.TryParse("1.5", out _));
        Assert.IsFalse(RouteId.TryParse(" 1", out _));
        Assert.IsFalse(RouteId.TryParse("", out _));
    }

    [TestMethod]
    public void RequestTimeTreatsMissingOffsetAsUtcAndRejectsDateWithTime()
    {
        Assert.IsTrue(RequestTime.TryParseInstant("2026-09-22T10:15:30", out var withoutOffset));
        Assert.AreEqual(new DateTime(2026, 9, 22, 10, 15, 30, DateTimeKind.Utc), withoutOffset);

        Assert.IsTrue(RequestTime.TryParseInstant("2026-09-22T10:15:30+03:00", out var withOffset));
        Assert.AreEqual(new DateTime(2026, 9, 22, 7, 15, 30, DateTimeKind.Utc), withOffset);

        Assert.IsFalse(RequestTime.TryParseInstant("", out _));
        Assert.IsFalse(RequestTime.TryParseInstant("not-a-date", out _));

        Assert.IsFalse(RequestTime.TryParseDate("2026-09-22T00:00:00", out _));
        Assert.IsTrue(RequestTime.TryParseDate("2026-09-22", out var date));
        Assert.AreEqual(new DateOnly(2026, 9, 22), date);
    }

    [TestMethod]
    public void TruncateDropsSubMicrosecondTicksAndKeepsUtc()
    {
        var value = new DateTime(2026, 9, 22, 10, 15, 30, DateTimeKind.Utc).AddTicks(11);

        var truncated = UtcTime.Truncate(value);

        Assert.AreEqual(DateTimeKind.Utc, truncated.Kind);
        Assert.AreEqual(0, truncated.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.AreEqual(value.Ticks - 1, truncated.Ticks);
    }

    [TestMethod]
    public void ControllersRequireJwtAndOnlyAnalyticsRequiresAdmin()
    {
        AssertController(typeof(EnrollmentsController), "enrollments", admin: false);
        AssertController(typeof(ScheduleController), "schedule", admin: false);
        AssertController(typeof(AnalyticsController), "analytics", admin: true);
    }

    [TestMethod]
    public void ConflictProblemWritesReasonBesideStatus()
    {
        var result = new ProblemProbe().ExposeConflict("capacity-exceeded");
        var details = (ProblemDetails)result.Value!;

        Assert.AreEqual(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.AreEqual("application/problem+json", result.ContentTypes.Single());
        Assert.AreEqual("capacity-exceeded", details.Extensions["reason"]);

        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var json = JsonSerializer.Serialize(details, options);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("capacity-exceeded", document.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual("Conflict", document.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(409, document.RootElement.GetProperty("status").GetInt32());
        Assert.IsFalse(document.RootElement.TryGetProperty("extensions", out _));
    }

    [TestMethod]
    public async Task RollbackRemovesRoomSavedInsideTheTransaction()
    {
        var name = NewRoomName();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await UnitOfWorkTransaction.ExecuteAsync(unitOfWork, async cancellationToken =>
            {
                await unitOfWork.GetRepository<Room>().CreateAsync(new Room { Name = name }, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return new TransactionOutcome<bool>(false, false);
            });
        }

        Assert.IsFalse(await RoomExists(name));
    }

    [TestMethod]
    public async Task ExceptionRollsBackRoomSavedInsideTheTransaction()
    {
        var name = NewRoomName();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            {
                await UnitOfWorkTransaction.ExecuteAsync<bool>(unitOfWork, async cancellationToken =>
                {
                    await unitOfWork.GetRepository<Room>().CreateAsync(new Room { Name = name }, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    throw new InvalidOperationException("fail");
                });
            });
        }

        Assert.IsFalse(await RoomExists(name));
    }

    [TestMethod]
    public async Task CommitPersistsRoomUntilASeparateSaveDeletesIt()
    {
        var name = NewRoomName();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await UnitOfWorkTransaction.ExecuteAsync(unitOfWork, async cancellationToken =>
            {
                await unitOfWork.GetRepository<Room>().CreateAsync(new Room { Name = name }, cancellationToken);
                return new TransactionOutcome<bool>(true, true);
            });
        }

        Assert.IsTrue(await RoomExists(name));

        await using var cleanup = _factory.Services.CreateAsyncScope();
        var cleanupUnitOfWork = cleanup.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var room = await cleanupUnitOfWork.DbContext.Set<Room>().SingleAsync(x => x.Name == name);
        cleanupUnitOfWork.GetRepository<Room>().Delete(room);
        await cleanupUnitOfWork.SaveChangesAsync();

        Assert.IsFalse(await RoomExists(name));
    }

    private static void AssertController(Type controller, string route, bool admin)
    {
        Assert.IsTrue(controller.IsSubclassOf(typeof(BaseApiController)));

        var routeAttribute = controller.GetCustomAttribute<RouteAttribute>();
        Assert.IsNotNull(routeAttribute);
        Assert.AreEqual(route, routeAttribute.Template);

        var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(authorize);
        Assert.AreEqual(admin ? "admin" : null, authorize.Roles);
        Assert.AreEqual(0, controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Length);
    }

    private static string NewRoomName() => $"phase1-{Guid.NewGuid():N}";

    private static async Task<bool> RoomExists(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.DbContext.Set<Room>().AnyAsync(x => x.Name == name);
    }

    private sealed class ProblemProbe : BaseApiController
    {
        public ObjectResult ExposeConflict(string reason) => ConflictProblem(reason);
    }
}
