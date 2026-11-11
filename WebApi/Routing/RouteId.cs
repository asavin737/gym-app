using System.Globalization;

namespace WebApi.Routing;

public static class RouteId
{
    public static bool TryParse(string? value, out long id)
    {
        id = 0;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var start = 0;

        if (value[0] == '-')
        {
            if (value.Length == 1)
            {
                return false;
            }

            start = 1;
        }

        for (var i = start; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id);
    }
}
