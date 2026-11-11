using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class WaitlistRepository : GenericRepository<WaitlistEntry>, IWaitlistRepository
{
    public WaitlistRepository(DbSet<WaitlistEntry> dbSet)
        : base(dbSet)
    {
    }

    public async Task<WaitlistEntry?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<WaitlistEntry?> FindWaitingAsync(long userId, long classId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(
            x => x.UserId == userId && x.ClassId == classId && x.Status == "waiting",
            ct);
    }

    public async Task<WaitlistEntry?> LockByIdAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .FromSqlInterpolated($"SELECT * FROM waitlist_entries WHERE id = {id} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<WaitlistEntry>> LockWaitingAsync(long classId, CancellationToken ct = default)
    {
        return await _dbSet
            .FromSqlInterpolated($"SELECT * FROM waitlist_entries WHERE class_id = {classId} AND status = 'waiting' ORDER BY created_at, id FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct);
    }

    public async Task<int> WaitingPositionAsync(long classId, DateTime createdAt, long id, CancellationToken ct = default)
    {
        return await _dbSet.CountAsync(
            x => x.ClassId == classId
                 && x.Status == "waiting"
                 && (x.CreatedAt < createdAt || (x.CreatedAt == createdAt && x.Id <= id)),
            ct);
    }
}
