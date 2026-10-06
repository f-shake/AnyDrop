namespace AnyDrop.Server;

public static class ErrorCodes
{
    public const string InvalidToken = "invalid_token";
    public const string TokenExpired = "token_expired";
    public const string ScopeDenied = "scope_denied";
    public const string NotFound = "not_found";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string FileTooLarge = "file_too_large";
    public const string RangeNotSatisfiable = "range_not_satisfiable";
    public const string RateLimited = "rate_limited";
    public const string LoginLocked = "login_locked";
    public const string UiNotBuilt = "ui_not_built";
    public const string QuotaExceeded = "quota_exceeded";
    public const string DiskLow = "disk_low";
    public const string IntegrityError = "integrity_error";
    public const string InternalError = "internal_error";
    public const string BadRequest = "bad_request";
    public const string LengthRequired = "length_required";
    public const string IncompleteBody = "incomplete_body";
    public const string InvalidCredentials = "invalid_credentials";
    public const string CsrfFailed = "csrf_failed";
}

public sealed record ApiError(string Code, string Message);

/// <summary>统一错误信封：{"error":{"code","message"}}。</summary>
public sealed record ApiErrorEnvelope(ApiError Error);

/// <summary>可预期的失败：由服务层返回给端点，映射成固定状态码与文案。</summary>
public readonly record struct ApiFailure(int Status, string Code, string Message);

public sealed record ServiceResult<T>(T? Value, ApiFailure? Failure)
{
    public bool Ok => Failure is null;

    public static ServiceResult<T> Success(T value) => new(value, null);

    public static ServiceResult<T> Fail(int status, string code, string message) =>
        new(default, new ApiFailure(status, code, message));

    public static ServiceResult<T> Fail(ApiFailure failure) => new(default, failure);
}

public static class ApiErrors
{
    public static IResult From(ApiFailure failure) => Json(failure.Status, failure.Code, failure.Message);

    public static IResult Json(int status, string code, string message) =>
        Results.Json(new ApiErrorEnvelope(new ApiError(code, message)),
            AppJsonContext.Default.ApiErrorEnvelope, statusCode: status);

    public static IResult NotFound() => Json(404, ErrorCodes.NotFound, "文件不存在或已过期");

    public static IResult FileTooLarge(long maxBytes) =>
        Json(413, ErrorCodes.FileTooLarge, $"文件超过上限（最大 {maxBytes / (1024 * 1024)} MiB）");

    public static IResult BadRequest(string message) => Json(400, ErrorCodes.BadRequest, message);

    public static IResult DiskLow() =>
        Json(507, ErrorCodes.DiskLow, "服务器空间不足，暂时拒绝写入");

    public static IResult IntegrityError() => Json(500, ErrorCodes.IntegrityError, "文件数据校验失败");

    public static IResult ServerError() => Json(500, ErrorCodes.InternalError, "服务内部错误");
}
