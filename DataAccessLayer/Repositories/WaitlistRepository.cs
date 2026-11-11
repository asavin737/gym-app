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
}
