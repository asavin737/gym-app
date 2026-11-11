using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IIdempotencyRepository : IGenericRepository<IdempotencyRecord>
{
    Task<IdempotencyRecord?> FindAsync(
        long userId,
        string scope,
        string keyHash,
        string key,
        CancellationToken ct = default);
}
