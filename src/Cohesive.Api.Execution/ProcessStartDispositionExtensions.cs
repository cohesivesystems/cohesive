using Cohesive.Execution;

namespace Cohesive.Api.Execution;

internal static class ProcessStartDispositionExtensions
{
    internal static ApiResultKind ToApiResultKind(this ProcessStartDisposition disposition, ApiResultKind success = ApiResultKind.Success) =>
        disposition switch
        {
            ProcessStartDisposition.Accepted or ProcessStartDisposition.Replayed => success,
            ProcessStartDisposition.CommandIdentityConflict or ProcessStartDisposition.IdempotencyConflict
                or ProcessStartDisposition.InstanceConflict => ApiResultKind.Conflict,
            ProcessStartDisposition.QuotaExceeded => ApiResultKind.CapacityExceeded,
            ProcessStartDisposition.QuotaPolicyMismatch => ApiResultKind.ServiceUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported process start disposition.")
        };
}
