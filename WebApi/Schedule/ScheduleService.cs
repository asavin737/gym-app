using System.Globalization;
using DataAccessLayer.UnitOfWork;
using WebApi.Routing;
using WebApi.Time;

namespace WebApi.Schedule;

public class ScheduleService
{
    private readonly IUnitOfWork _unitOfWork;

    public ScheduleService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<ScheduleResult> GetAsync(
        long userId,
        string? from,
        string? to,
        string? roomId,
        string? limit,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        if (!RequestTime.TryParseInstant(from, out var fromUtc)
            || !RequestTime.TryParseInstant(to, out var toUtc)
            || toUtc <= fromUtc)
        {
            return ScheduleResult.BadRequest();
        }

        long? room = null;

        if (roomId is not null)
        {
            if (!RouteId.TryParse(roomId, out var parsedRoomId))
            {
                return ScheduleResult.BadRequest();
            }

            room = parsedRoomId;
        }

        var pageSize = 20;

        if (limit is not null)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1
                || pageSize > 100)
            {
                return ScheduleResult.BadRequest();
            }
        }

        DateTime? afterStartsAt = null;
        long? afterClassId = null;

        if (continuationToken is not null)
        {
            if (!ContinuationToken.TryParse(continuationToken, out var cursorStarts, out var cursorId))
            {
                return ScheduleResult.BadRequest();
            }

            afterStartsAt = cursorStarts;
            afterClassId = cursorId;
        }

        var rows = await _unitOfWork.Class.ListScheduleAsync(
            fromUtc,
            toUtc,
            room,
            afterStartsAt,
            afterClassId,
            pageSize + 1,
            userId,
            cancellationToken);

        string? nextToken = null;

        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextToken = ContinuationToken.Create(last.StartsAt, last.ClassId);
        }

        return ScheduleResult.Ok(new ScheduleResponse
        {
            Items = rows.Select(row => new ScheduleItemResponse
            {
                ClassId = row.ClassId,
                Title = row.Title,
                StartsAt = row.StartsAt,
                DurationMinutes = row.DurationMinutes,
                Room = new ScheduleRoomResponse
                {
                    Id = row.RoomId,
                    Name = row.RoomName
                },
                Capacity = row.Capacity,
                FreeSeats = Math.Max(0, row.Capacity - row.BookedSeats),
                MySeats = row.MySeats
            }).ToList(),
            ContinuationToken = nextToken
        });
    }
}

public sealed class ScheduleResult
{
    private ScheduleResult(int statusCode, ScheduleResponse? body)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    public ScheduleResponse? Body { get; }

    public static ScheduleResult BadRequest() => new(StatusCodes.Status400BadRequest, null);

    public static ScheduleResult Ok(ScheduleResponse body) => new(StatusCodes.Status200OK, body);
}
