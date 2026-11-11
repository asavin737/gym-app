using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    public EnrollmentRepository(DbSet<Enrollment> dbSet)
        : base(dbSet)
    {
    }

    public async Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<Enrollment?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .IgnoreQueryFilters()
            .Include(x => x.Class)
            .ThenInclude(x => x.Room)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<List<long>> ListIdsByUserIgnoringFiltersAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .IgnoreQueryFilters()
            .Where(x => x.UserId == userId)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task<int> SumActiveSeatsAsync(long classId, CancellationToken ct = default)
    {
        return await _dbSet
            .IgnoreQueryFilters()
            .Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (int?)x.Seats, ct) ?? 0;
    }
}
