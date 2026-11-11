using DataAccessLayer.UnitOfWork;
using WebApi.Routing;
using WebApi.Time;

namespace WebApi.Analytics;

public class LoadService
{
    private readonly IUnitOfWork _unitOfWork;

    public LoadService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<LoadResult> GetAsync(
        string? from,
        string? to,
        string? roomId,
        CancellationToken cancellationToken = default)
    {
        if (!RequestTime.TryParseDate(from, out var fromDate)
            || !RequestTime.TryParseDate(to, out var toDate)
            || toDate < fromDate)
        {
            return LoadResult.BadRequest();
        }

        long? room = null;

        if (roomId is not null)
        {
            if (!RouteId.TryParse(roomId, out var parsedRoomId))
            {
                return LoadResult.BadRequest();
            }

            room = parsedRoomId;
        }

        var rows = await _unitOfWork.Class.ListLoadAsync(
            fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            room,
            cancellationToken);

        return LoadResult.Ok(new LoadResponse
        {
            Items = rows.Select(row => new LoadItemResponse
            {
                Date = row.Date,
                Room = new LoadRoomResponse
                {
                    Id = row.RoomId,
                    Name = row.RoomName
                },
                ClassesCount = row.ClassesCount,
                TotalCapacity = row.TotalCapacity,
                BookedSeats = row.BookedSeats
            }).ToList()
        });
    }
}

public sealed class LoadResult
{
    private LoadResult(int statusCode, LoadResponse? body)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    public LoadResponse? Body { get; }

    public static LoadResult BadRequest() => new(StatusCodes.Status400BadRequest, null);

    public static LoadResult Ok(LoadResponse body) => new(StatusCodes.Status200OK, body);
}
