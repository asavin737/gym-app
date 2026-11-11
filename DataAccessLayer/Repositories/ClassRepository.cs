using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
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
}
