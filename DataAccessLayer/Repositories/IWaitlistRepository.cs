using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<WaitlistEntry?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default);
}
