using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Data;

namespace WebApi.Tests;

[TestClass]
public class Phase2Tests
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
    public async Task CancelledClassAndEnrollmentStayHiddenUntilFiltersAreIgnored()
    {
        var (roomId, classId, userId) = await CreateClassAsync(cancelled: true);
        long enrollmentId = 0;
        long waitlistId = 0;

        try
        {
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var enrollment = await unitOfWork.Enrollment.CreateAsync(new Enrollment
                {
                    UserId = userId,
                    ClassId = classId,
                    Seats = 2,
                    Status = "active",
                    CreatedAt = DateTime.UtcNow
                });
                var entry = await unitOfWork.Waitlist.CreateAsync(new WaitlistEntry
                {
                    UserId = userId,
                    ClassId = classId,
                    Seats = 1,
                    Status = "waiting",
                    CreatedAt = DateTime.UtcNow
                });
                await unitOfWork.SaveChangesAsync();
                enrollmentId = enrollment.Id;
                waitlistId = entry.Id;
            }

            await using var readScope = _factory.Services.CreateAsyncScope();
            var read = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Assert.IsNull(await read.Class.GetByIdAsync(classId));
            var visibleClass = await read.Class.GetByIdIgnoringFiltersAsync(classId);
            Assert.IsNotNull(visibleClass);
            Assert.IsTrue(visibleClass.IsCancelled);

            var hiddenEnrollment = await read.Enrollment.Query()
                .Include(x => x.Class)
                .FirstOrDefaultAsync(x => x.Id == enrollmentId);
            Assert.IsNull(hiddenEnrollment);
            var visibleEnrollment = await read.Enrollment.GetByIdIgnoringFiltersAsync(enrollmentId);
            Assert.IsNotNull(visibleEnrollment);
            Assert.IsNotNull(visibleEnrollment.Class);
            Assert.IsTrue(visibleEnrollment.Class.IsCancelled);
            Assert.AreEqual(classId, visibleEnrollment.ClassId);

            var hiddenEntry = await read.Waitlist.Query()
                .Include(x => x.Class)
                .FirstOrDefaultAsync(x => x.Id == waitlistId);
            Assert.IsNull(hiddenEntry);
            var visibleEntry = await read.Waitlist.GetByIdIgnoringFiltersAsync(waitlistId);
            Assert.IsNotNull(visibleEntry);
            Assert.AreEqual(classId, visibleEntry.ClassId);
        }
        finally
        {
            await DeleteRoomAsync(roomId);
        }
    }

    [TestMethod]
    public async Task SumActiveSeatsCountsOnlyActiveBookings()
    {
        var (roomId, classId, userId) = await CreateClassAsync(cancelled: false);
        long emptyClassId = 0;

        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var emptyClass = await unitOfWork.Class.CreateAsync(new Class
            {
                RoomId = roomId,
                Title = "Phase2 empty",
                StartsAt = DateTime.UtcNow.AddDays(3),
                DurationMinutes = 45,
                Capacity = 8,
                IsCancelled = false
            });
            await unitOfWork.Enrollment.CreateAsync(Booking(userId, classId, 2, "active"));
            await unitOfWork.Enrollment.CreateAsync(Booking(userId, classId, 3, "active"));
            await unitOfWork.Enrollment.CreateAsync(Booking(userId, classId, 4, "cancelled"));
            await unitOfWork.SaveChangesAsync();
            emptyClassId = emptyClass.Id;

            Assert.AreEqual(5, await unitOfWork.Enrollment.SumActiveSeatsAsync(classId));
            Assert.AreEqual(0, await unitOfWork.Enrollment.SumActiveSeatsAsync(emptyClassId));
        }
        finally
        {
            await DeleteRoomAsync(roomId);
        }
    }

    [TestMethod]
    public async Task LockReadsCancelledClassAndTheSamePass()
    {
        var (roomId, classId, userId) = await CreateClassAsync(cancelled: true);

        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await UnitOfWorkTransaction.ExecuteAsync(unitOfWork, async cancellationToken =>
            {
                var lockedClass = await unitOfWork.Class.LockByIdAsync(classId, cancellationToken);
                Assert.IsNotNull(lockedClass);
                Assert.IsTrue(lockedClass.IsCancelled);
                Assert.AreEqual(EntityState.Unchanged, unitOfWork.DbContext.Entry(lockedClass).State);

                var lockedPass = await unitOfWork.Pass.LockByUserIdAsync(userId, cancellationToken);
                var pass = await unitOfWork.Pass.GetByUserAsync(userId, cancellationToken);
                Assert.IsNotNull(lockedPass);
                Assert.IsNotNull(pass);
                Assert.AreEqual(pass.Id, lockedPass.Id);
                Assert.AreEqual(EntityState.Unchanged, unitOfWork.DbContext.Entry(lockedPass).State);

                return new TransactionOutcome<bool>(true, true);
            });
        }
        finally
        {
            await DeleteRoomAsync(roomId);
        }
    }

    [TestMethod]
    public async Task IdempotencyRowFollowsTheTransaction()
    {
        var userId = await FirstUserIdAsync();
        var discardedKey = $"discard-{Guid.NewGuid():N}";
        var discardedHash = new string('b', 64);
        var keptKey = $"keep-{Guid.NewGuid():N}";
        var keptHash = new string('c', 64);

        try
        {
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await UnitOfWorkTransaction.ExecuteAsync(unitOfWork, async cancellationToken =>
                {
                    await unitOfWork.Idempotency.CreateAsync(
                        NewRecord(userId, discardedKey, discardedHash),
                        cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return new TransactionOutcome<bool>(false, false);
                });
            }

            Assert.IsNull(await FindRecordAsync(userId, "enrollment", discardedHash, discardedKey));

            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await UnitOfWorkTransaction.ExecuteAsync(unitOfWork, async cancellationToken =>
                {
                    await unitOfWork.Idempotency.CreateAsync(
                        NewRecord(userId, keptKey, keptHash),
                        cancellationToken);
                    return new TransactionOutcome<bool>(true, true);
                });
            }

            var stored = await FindRecordAsync(userId, "enrollment", keptHash, keptKey);
            Assert.IsNotNull(stored);
            Assert.AreEqual(201, stored.StatusCode);
            Assert.AreEqual("{}", stored.ResponseBody);
            Assert.IsNull(await FindRecordAsync(userId, "enrollment", keptHash, "other-key"));
            Assert.IsNull(await FindRecordAsync(userId, "waitlist", keptHash, keptKey));
        }
        finally
        {
            await DeleteIdempotencyAsync(userId, discardedHash);
            await DeleteIdempotencyAsync(userId, keptHash);
        }
    }

    [TestMethod]
    public async Task SecondWaitingEntryForTheSameUserAndClassRollsBack()
    {
        var (roomId, classId, userId) = await CreateClassAsync(cancelled: false);

        try
        {
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.Waitlist.CreateAsync(Waiting(userId, classId, "waiting"));
                await unitOfWork.Waitlist.CreateAsync(Waiting(userId, classId, "cancelled"));
                await unitOfWork.SaveChangesAsync();
            }

            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await Assert.ThrowsExceptionAsync<DbUpdateException>(async () =>
                {
                    await UnitOfWorkTransaction.ExecuteAsync<bool>(unitOfWork, async cancellationToken =>
                    {
                        await unitOfWork.Waitlist.CreateAsync(
                            Waiting(userId, classId, "waiting"),
                            cancellationToken);
                        await unitOfWork.SaveChangesAsync(cancellationToken);
                        return new TransactionOutcome<bool>(true, true);
                    });
                });
            }

            await using var readScope = _factory.Services.CreateAsyncScope();
            var read = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var statuses = await read.DbContext.Set<WaitlistEntry>()
                .IgnoreQueryFilters()
                .Where(x => x.ClassId == classId && x.UserId == userId)
                .Select(x => x.Status)
                .ToListAsync();

            CollectionAssert.AreEquivalent(new[] { "waiting", "cancelled" }, statuses);
        }
        finally
        {
            await DeleteRoomAsync(roomId);
        }
    }

    [TestMethod]
    public async Task SeedKeepsExistingVolumesAndAddsWaitingEntries()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;

        Assert.AreEqual(10, await db.Set<Room>().CountAsync());
        Assert.AreEqual(205, await db.Set<User>().CountAsync());
        Assert.AreEqual(200, await db.Set<Class>().IgnoreQueryFilters().CountAsync());
        Assert.IsTrue(await db.Set<Enrollment>().IgnoreQueryFilters().AnyAsync());

        var waiting = await db.Set<WaitlistEntry>()
            .IgnoreQueryFilters()
            .Where(x => x.Status == "waiting")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(3)
            .ToListAsync();

        Assert.AreEqual(3, waiting.Count);
        Assert.AreEqual(1, waiting.Select(x => x.ClassId).Distinct().Count());
        Assert.AreEqual(3, waiting.Select(x => x.UserId).Distinct().Count());
        Assert.IsTrue(waiting.All(x => x.Seats == 1));
        Assert.IsTrue(waiting[0].CreatedAt < waiting[1].CreatedAt);
        Assert.IsTrue(waiting[1].CreatedAt < waiting[2].CreatedAt);

        var classId = waiting[0].ClassId;
        var capacity = await db.Set<Class>()
            .IgnoreQueryFilters()
            .Where(x => x.Id == classId)
            .Select(x => x.Capacity)
            .SingleAsync();

        Assert.AreEqual(capacity, await unitOfWork.Enrollment.SumActiveSeatsAsync(classId));
    }

    [TestMethod]
    public async Task MigrationAddsScheduleCapacityAndQueueIndexes()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var definitions = await unitOfWork.DbContext.Database
            .SqlQueryRaw<string>("""
                SELECT indexdef AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public'
                """)
            .ToListAsync();

        Assert.IsTrue(definitions.Any(Defines("public.classes", "starts_at")));
        Assert.IsTrue(definitions.Any(Defines("public.classes", "room_id")));
        Assert.IsTrue(definitions.Any(Defines("public.enrollments", "class_id")));
        Assert.IsTrue(definitions.Any(Defines("public.enrollments", "class_id, status")));
        Assert.IsTrue(definitions.Any(Defines("public.enrollments", "user_id")));
        Assert.IsTrue(definitions.Any(Defines("public.passes", "user_id")));
        Assert.IsTrue(definitions.Any(Defines("public.waitlist_entries", "class_id, status, created_at, id")));
        Assert.IsTrue(definitions.Any(x =>
            x.Contains("public.waitlist_entries", StringComparison.Ordinal)
            && x.Contains("UNIQUE", StringComparison.Ordinal)
            && x.Contains("waiting", StringComparison.Ordinal)));
    }

    private static Func<string, bool> Defines(string table, string columns) =>
        definition => definition.Contains(table, StringComparison.Ordinal)
            && definition.Contains($"({columns})", StringComparison.Ordinal);

    private static async Task<(long RoomId, long ClassId, long UserId)> CreateClassAsync(bool cancelled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var userId = await unitOfWork.DbContext.Set<User>().OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var room = await unitOfWork.Room.CreateAsync(new Room { Name = $"phase2-{Guid.NewGuid():N}" });
        var scheduledClass = await unitOfWork.Class.CreateAsync(new Class
        {
            Room = room,
            Title = "Phase2",
            StartsAt = DateTime.UtcNow.AddDays(2),
            DurationMinutes = 60,
            Capacity = 10,
            IsCancelled = cancelled
        });
        await unitOfWork.SaveChangesAsync();

        return (room.Id, scheduledClass.Id, userId);
    }

    private static Enrollment Booking(long userId, long classId, int seats, string status)
    {
        return new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            CancelledAt = status == "cancelled" ? DateTime.UtcNow : null
        };
    }

    private static WaitlistEntry Waiting(long userId, long classId, string status)
    {
        return new WaitlistEntry
        {
            UserId = userId,
            ClassId = classId,
            Seats = 1,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static IdempotencyRecord NewRecord(long userId, string key, string keyHash)
    {
        return new IdempotencyRecord
        {
            UserId = userId,
            Scope = "enrollment",
            Key = key,
            KeyHash = keyHash,
            RequestHash = new string('a', 64),
            StatusCode = 201,
            ResponseBody = "{}",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static async Task<long> FirstUserIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.DbContext.Set<User>().OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    }

    private static async Task<IdempotencyRecord?> FindRecordAsync(
        long userId,
        string scopeName,
        string keyHash,
        string key)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.Idempotency.FindAsync(userId, scopeName, keyHash, key);
    }

    private static async Task DeleteIdempotencyAsync(long userId, string keyHash)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rows = await unitOfWork.DbContext.Set<IdempotencyRecord>()
            .Where(x => x.UserId == userId && x.KeyHash == keyHash)
            .ToListAsync();

        foreach (var row in rows)
        {
            unitOfWork.Idempotency.Delete(row);
        }

        if (rows.Count > 0)
        {
            await unitOfWork.SaveChangesAsync();
        }
    }

    private static async Task DeleteRoomAsync(long roomId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var room = await unitOfWork.Room.GetByIdAsync(roomId);

        if (room == null)
        {
            return;
        }

        unitOfWork.Room.Delete(room);
        await unitOfWork.SaveChangesAsync();
    }
}
