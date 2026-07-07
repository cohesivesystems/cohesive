using System.Globalization;

namespace Cohesive.Prelude;

/// <summary>
/// Extension methods for <see cref="DateTime"/>.
/// </summary>
public static class DateTimeExtensions
{
    extension(DateTime value)
    {
        /// <summary>
        /// Returns the <see cref="DateTime"/> as a UTC <see cref="DateTimeOffset"/>, converting local time and unspecified kinds to UTC.
        /// </summary>
        /// <returns>A UTC <see cref="DateTimeOffset"/> representing the same instant as the given <see cref="DateTime"/>.</returns>
        public DateTimeOffset ToDateTimeOffsetUtc()
        {
            if (value == default)
                return default;

            return value.Kind switch
            {
                DateTimeKind.Utc => new(value),
                DateTimeKind.Local => new(value.ToUniversalTime(), TimeSpan.Zero),
                _ => new(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            };
        }
    }
    
    extension(DateTime)
    {
        /// <summary>
        /// Parses an ISO 8601 datetime string into a UTC <see cref="DateTime"/>. If the given date string specifies an offset, it will be converted to UTC.
        /// </summary>
        /// <param name="str">A string containing the characters that represent a date and time to convert.</param>
        /// <returns>A <see cref="DateTime"/> with Utc kind.</returns>
        /// <exception cref="FormatException">Invalid ISO 8601 UTC datetime.</exception>
        /// <remarks>
        /// Expected formats:
        /// <ul>
        /// <li>yyyy-MM-ddTHH:mm:ssK</li>
        /// <li>yyyy-MM-ddTHH:mm:ss</li>
        /// <li>yyyy-MM-ddTHH:mm</li>
        /// <li>yyyy-MM-dd</li>
        /// </ul>
        /// </remarks>
        public static DateTime ParseIso8601Utc(ReadOnlySpan<char> str) =>
            DateTimeOffset.ParseIso8601Utc(str).UtcDateTime;

        /// <summary>
        /// Parses an ISO 8601 datetime string into a UTC <see cref="DateTime"/>. If the given date string specifies an offset, it will be converted to UTC.
        /// </summary>
        /// <param name="str">A string containing the characters that represent a date and time to convert.</param>
        /// <param name="dt"></param>
        /// <returns>True if the string was successfully parsed into a UTC <see cref="DateTime"/>.</returns>
        /// <remarks>
        /// Expected formats:
        /// <ul>
        /// <li>yyyy-MM-ddTHH:mm:ssK</li>
        /// <li>yyyy-MM-ddTHH:mm:ss</li>
        /// <li>yyyy-MM-ddTHH:mm</li>
        /// <li>yyyy-MM-dd</li>
        /// </ul>
        /// </remarks>
        public static bool TryParseIso8601Utc(ReadOnlySpan<char> str, out DateTime dt)
        {
            if (DateTimeOffset.TryParseIso8601Utc(str, out var dto))
            {
                dt = dto.UtcDateTime;
                return true;
            }
            dt = default;
            return false;
        }
    }
}

/// <summary>
/// Extension methods for <see cref="DateTimeOffset"/>.
/// </summary>
public static class DateTimeOffsetExtensions
{
    static readonly string[] Iso8601Formats =
    [
        // with offset
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.fffffffK",

        // without offset (assumed UTC)
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff",
        
        // without seconds or hours and minutes
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd",
    ];
    
    extension(DateTimeOffset)
    {
        /// <summary>
        /// Parses an ISO 8601 datetime string into a UTC <see cref="DateTimeOffset"/>. If the given date string specifies an offset, it will be converted to UTC.
        /// </summary>
        /// <param name="str">A string containing the characters that represent a date and time to convert.</param>
        /// <returns>A <see cref="DateTimeOffset"/> with UTC offset.</returns>
        /// <exception cref="FormatException">Invalid ISO 8601 UTC datetime.</exception>
        /// <remarks>
        /// Expected formats:
        /// <ul>
        /// <li>yyyy-MM-ddTHH:mm:ssK</li>
        /// <li>yyyy-MM-ddTHH:mm:ss</li>
        /// <li>yyyy-MM-ddTHH:mm</li>
        /// <li>yyyy-MM-dd</li>
        /// </ul>
        /// </remarks>
        public static DateTimeOffset ParseIso8601Utc(ReadOnlySpan<char> str) => 
            DateTimeOffset.TryParseIso8601Utc(str: str, out var dto) ? dto : throw new FormatException($"Invalid ISO 8601 UTC datetime: '{str}'");

        /// <summary>
        /// Parses an ISO 8601 datetime string into a UTC <see cref="DateTimeOffset"/>. If the given date string specifies an offset, it will be converted to UTC.
        /// </summary>
        /// <param name="str">A string containing the characters that represent a date and time to convert.</param>
        /// <param name="dto">The value to populate with the parsed datetime.</param>
        /// <returns>True if the string was successfully parsed into a UTC <see cref="DateTimeOffset"/>.</returns>
        /// <remarks>
        /// Expected formats:
        /// <ul>
        /// <li>yyyy-MM-ddTHH:mm:ssK</li>
        /// <li>yyyy-MM-ddTHH:mm:ss</li>
        /// <li>yyyy-MM-ddTHH:mm</li>
        /// <li>yyyy-MM-dd</li>
        /// </ul>
        /// </remarks>
        public static bool TryParseIso8601Utc(ReadOnlySpan<char> str, out DateTimeOffset dto) => DateTimeOffset.TryParseExact(
            input: str, 
            formats: Iso8601Formats, 
            formatProvider: CultureInfo.InvariantCulture, 
            styles: DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, 
            out dto
            );
    }
}