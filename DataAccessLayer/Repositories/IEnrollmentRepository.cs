using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IEnrollmentRepository : IGenericRepository<Enrollment>
{
    Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default);

    Task<Enrollment?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default);

    Task<List<long>> ListIdsByUserIgnoringFiltersAsync(long userId, CancellationToken ct = default);

    Task<int> SumActiveSeatsAsync(long classId, CancellationToken ct = default);
}
