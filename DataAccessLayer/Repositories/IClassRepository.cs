using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Queries;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);

    Task<Class?> GetByIdIgnoringFiltersAsync(long id, CancellationToken ct = default);

    Task<Class?> LockByIdAsync(long id, CancellationToken ct = default);

    Task<List<ScheduleRow>> ListScheduleAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        DateTime? afterStartsAt,
        long? afterClassId,
        int take,
        long userId,
        CancellationToken ct = default);

    Task<List<LoadRow>> ListLoadAsync(
        DateTime fromInclusive,
        DateTime toExclusive,
        long? roomId,
        CancellationToken ct = default);
}
