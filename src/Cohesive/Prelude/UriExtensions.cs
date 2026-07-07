namespace Cohesive.Prelude;

/// <summary>
/// Extensions for <see cref="Uri"/>.
/// </summary>
public static class UriExtensions
{
    extension(Uri)
    {
        /// <summary>
        /// Combines two URI path segments with a single forward slash.
        /// </summary>
        /// <param name="segment1">The first segment to combine.</param>
        /// <param name="segment2">The second segment to combine.</param>
        /// <returns>The combined path.</returns>
        public static string CombineSegments(string? segment1, string? segment2)
        {
            var first = segment1.AsSpan().Trim('/');
            var second = segment2.AsSpan().Trim('/');

            if (first.IsEmpty)
                return second.IsEmpty ? string.Empty : second.ToString();

            if (second.IsEmpty)
                return first.ToString();

            Span<char> initialBuffer = stackalloc char[Math.Min(first.Length + second.Length + 1, 256)];
            var builder = new ValueStringBuilder(initialBuffer);
            builder.Append(first);
            builder.Append('/');
            builder.Append(second);
            return builder.ToString();
        }

        /// <summary>
        /// Combines URI path segments with a single forward slash.
        /// </summary>
        /// <param name="segments">The segments to combine.</param>
        /// <returns>The combined path.</returns>
        public static string CombineSegments(ReadOnlySpan<string?> segments)
        {
            var totalLength = 0;
            var segmentCount = 0;

            foreach (var segment in segments)
            {
                var trimmedSegment = segment.AsSpan().Trim('/');
                if (trimmedSegment.IsEmpty)
                    continue;

                totalLength += trimmedSegment.Length;
                segmentCount++;
            }

            if (segmentCount == 0)
                return string.Empty;

            Span<char> initialBuffer = stackalloc char[Math.Min(totalLength + segmentCount - 1, 256)];
            var builder = new ValueStringBuilder(initialBuffer);
            var hasWrittenSegment = false;

            foreach (var segment in segments)
            {
                var trimmedSegment = segment.AsSpan().Trim('/');
                if (trimmedSegment.IsEmpty)
                    continue;

                if (hasWrittenSegment)
                    builder.Append('/');

                builder.Append(trimmedSegment);
                hasWrittenSegment = true;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Creates an absolute URI by binding a route template relative to a base URI.
        /// </summary>
        /// <param name="baseUri">The absolute base URI.</param>
        /// <param name="routeTemplate">The route template to bind.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri CreateRouteUri(Uri baseUri, RouteTemplate routeTemplate) =>
            Uri.CreateRouteUri<object?>(baseUri, routeTemplate, null);

        /// <summary>
        /// Creates an absolute URI by binding a route template relative to a base URI.
        /// </summary>
        /// <param name="baseUri">The absolute base URI.</param>
        /// <param name="routeTemplate">The route template to bind.</param>
        /// <param name="values">The object whose public properties provide route values. Null is treated as an empty value set.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri CreateRouteUri(Uri baseUri, RouteTemplate routeTemplate, object? values)
        {
            ArgumentNullException.ThrowIfNull(routeTemplate);
            return Uri.CreateRouteUriFromBoundRoute(baseUri, routeTemplate.Bind(values));
        }

        /// <summary>
        /// Creates an absolute URI by binding a route template relative to a base URI.
        /// </summary>
        /// <param name="baseUri">The absolute base URI.</param>
        /// <param name="routeTemplate">The route template to bind.</param>
        /// <param name="values">The route values keyed by parameter name. Null is treated as an empty value set.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri CreateRouteUri<TValue>(Uri baseUri, RouteTemplate routeTemplate, IReadOnlyDictionary<string, TValue>? values)
        {
            ArgumentNullException.ThrowIfNull(routeTemplate);
            return Uri.CreateRouteUriFromBoundRoute(baseUri, routeTemplate.Bind(values));
        }

        /// <summary>
        /// Creates an absolute URI by parsing and binding a route template relative to a base URI.
        /// </summary>
        /// <param name="baseUri">The absolute base URI.</param>
        /// <param name="routeTemplate">The route template to parse and bind.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri CreateRouteUri(Uri baseUri, string routeTemplate) =>
            Uri.CreateRouteUri(baseUri, RouteTemplate.Parse(routeTemplate));

        /// <summary>
        /// Creates an absolute URI by parsing and binding a route template relative to a base URI.
        /// </summary>
        /// <param name="baseUri">The absolute base URI.</param>
        /// <param name="routeTemplate">The route template to parse and bind.</param>
        /// <param name="values">The object whose public properties provide route values. Null is treated as an empty value set.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri CreateRouteUri(Uri baseUri, string routeTemplate, object? values) =>
            Uri.CreateRouteUri(baseUri, RouteTemplate.Parse(routeTemplate), values);

        /// <summary>
        /// Creates an absolute URI by parsing and binding a route template relative to a base URI.
        /// </summary>
        /// <param name="baseUri">The absolute base URI.</param>
        /// <param name="routeTemplate">The route template to parse and bind.</param>
        /// <param name="values">The route values keyed by parameter name. Null is treated as an empty value set.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri CreateRouteUri<TValue>(Uri baseUri, string routeTemplate, IReadOnlyDictionary<string, TValue>? values) =>
            Uri.CreateRouteUri(baseUri, RouteTemplate.Parse(routeTemplate), values);

        static Uri CreateRouteUriFromBoundRoute(Uri baseUri, string boundRoute)
        {
            ArgumentNullException.ThrowIfNull(baseUri);
            ArgumentNullException.ThrowIfNull(boundRoute);

            if (!baseUri.IsAbsoluteUri)
                throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));

            var fragmentIndex = boundRoute.IndexOf('#', StringComparison.Ordinal);
            var beforeFragment = fragmentIndex >= 0 ? boundRoute.AsSpan(0, fragmentIndex) : boundRoute.AsSpan();
            var fragment = fragmentIndex >= 0 ? boundRoute.AsSpan(fragmentIndex + 1) : [];
            var queryIndex = beforeFragment.IndexOf('?');
            var routePath = queryIndex >= 0 ? beforeFragment[..queryIndex] : beforeFragment;
            var query = queryIndex >= 0 ? beforeFragment[(queryIndex + 1)..] : [];
            var path = Uri.CombineSegments(baseUri.AbsolutePath, routePath.ToString());
            var authority = baseUri.GetLeftPart(UriPartial.Authority);
            var estimatedLength = authority.Length + path.Length + query.Length + fragment.Length + 3;
            Span<char> initialBuffer = stackalloc char[Math.Min(estimatedLength, 512)];
            var builder = new ValueStringBuilder(initialBuffer);
            builder.Append(authority);
            builder.Append('/');
            builder.Append(path);

            if (!query.IsEmpty)
            {
                builder.Append('?');
                builder.Append(query);
            }

            if (!fragment.IsEmpty)
            {
                builder.Append('#');
                builder.Append(fragment);
            }

            return new(builder.ToString(), UriKind.Absolute);
        }
    }
}
