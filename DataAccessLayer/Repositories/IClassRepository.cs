using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);

    Task<Class?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default);

    Task<Class?> LockByIdAsync(long id, CancellationToken ct = default);
}
