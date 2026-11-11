using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class IdempotencyRepository : GenericRepository<IdempotencyRecord>, IIdempotencyRepository
{
    public IdempotencyRepository(DbSet<IdempotencyRecord> dbSet)
        : base(dbSet)
    {
    }

    public async Task<IdempotencyRecord?> FindAsync(
        long userId,
        string scope,
        string keyHash,
        string key,
        CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(
            x => x.UserId == userId && x.Scope == scope && x.KeyHash == keyHash && x.Key == key,
            ct);
    }
}
