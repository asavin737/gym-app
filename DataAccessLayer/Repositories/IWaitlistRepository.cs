using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<WaitlistEntry?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default);

    Task<WaitlistEntry?> FindWaitingAsync(long userId, long classId, CancellationToken ct = default);

    Task<WaitlistEntry?> LockByIdAsync(long id, CancellationToken ct = default);

    Task<List<WaitlistEntry>> LockWaitingAsync(long classId, CancellationToken ct = default);

    Task<int> WaitingPositionAsync(long classId, DateTime createdAt, long id, CancellationToken ct = default);
}
