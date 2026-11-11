using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Queries;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    public ClassRepository(DbSet<Class> dbSet)
        : base(dbSet)
    {
    }

    public async Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<Class?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<Class?> LockByIdAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .FromSqlInterpolated($"SELECT * FROM classes WHERE id = {id} FOR UPDATE")
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ScheduleRow>> ListScheduleAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        DateTime? afterStartsAt,
        long? afterClassId,
        int take,
        long userId,
        CancellationToken ct = default)
    {
        var query = _dbSet.Where(x => x.StartsAt >= from && x.StartsAt < to);

        if (roomId is not null)
        {
            query = query.Where(x => x.RoomId == roomId);
        }

        if (afterStartsAt is DateTime cursorStarts && afterClassId is long cursorId)
        {
            query = query.Where(x => x.StartsAt > cursorStarts || (x.StartsAt == cursorStarts && x.Id > cursorId));
        }

        return await query
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new ScheduleRow
            {
                ClassId = x.Id,
                Title = x.Title,
                StartsAt = x.StartsAt,
                DurationMinutes = x.DurationMinutes,
                RoomId = x.Room.Id,
                RoomName = x.Room.Name,
                Capacity = x.Capacity,
                BookedSeats = x.Enrollments.Where(enrollment => enrollment.Status == "active").Sum(enrollment => (int?)enrollment.Seats) ?? 0,
                MySeats = x.Enrollments
                    .Where(enrollment => enrollment.Status == "active" && enrollment.UserId == userId)
                    .Sum(enrollment => (int?)enrollment.Seats) ?? 0
            })
            .ToListAsync(ct);
    }

    public async Task<List<LoadRow>> ListLoadAsync(
        DateTime fromInclusive,
        DateTime toExclusive,
        long? roomId,
        CancellationToken ct = default)
    {
        var query = _dbSet.Where(x => x.StartsAt >= fromInclusive && x.StartsAt < toExclusive);

        if (roomId is not null)
        {
            query = query.Where(x => x.RoomId == roomId);
        }

        var perClass = query.Select(x => new
        {
            Day = x.StartsAt.Date,
            x.RoomId,
            RoomName = x.Room.Name,
            x.Capacity,
            Booked = x.Enrollments.Where(enrollment => enrollment.Status == "active").Sum(enrollment => (int?)enrollment.Seats) ?? 0
        });

        var grouped = await perClass
            .GroupBy(x => new { x.Day, x.RoomId, x.RoomName })
            .Select(group => new
            {
                group.Key.Day,
                group.Key.RoomId,
                group.Key.RoomName,
                ClassesCount = group.Count(),
                TotalCapacity = group.Sum(x => x.Capacity),
                BookedSeats = group.Sum(x => x.Booked)
            })
            .OrderBy(x => x.Day)
            .ThenBy(x => x.RoomId)
            .ToListAsync(ct);

        return grouped
            .Select(x => new LoadRow
            {
                Date = DateOnly.FromDateTime(x.Day),
                RoomId = x.RoomId,
                RoomName = x.RoomName,
                ClassesCount = x.ClassesCount,
                TotalCapacity = x.TotalCapacity,
                BookedSeats = x.BookedSeats
            })
            .ToList();
    }
}
