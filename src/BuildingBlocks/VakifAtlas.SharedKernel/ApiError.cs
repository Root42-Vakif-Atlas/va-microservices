namespace VakifAtlas.SharedKernel;

/// <summary>
/// Represents a structured API error response containing an HTTP status code,
/// a machine-readable error code, and an optional retry interval.
/// </summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Code">The machine-readable application error code.</param>
/// <param name="RetryAfterSeconds">Optional number of seconds the client must wait before retrying.</param>
public sealed record ApiError(int Status, string Code, int? RetryAfterSeconds = null)
{
    /// <summary>Creates a 400 Bad Request error.</summary>
    /// <param name="code">The specific error code; defaults to <see cref="ErrorCodes.InvalidRequest"/>.</param>
    public static ApiError BadRequest(string code = ErrorCodes.InvalidRequest) => new(400, code);

    /// <summary>Creates a 401 Unauthorized error.</summary>
    /// <param name="code">The specific error code; defaults to <see cref="ErrorCodes.Unauthorized"/>.</param>
    public static ApiError Unauthorized(string code = ErrorCodes.Unauthorized) => new(401, code);

    /// <summary>Creates a 403 Forbidden error.</summary>
    /// <param name="code">The specific error code.</param>
    /// <param name="retryAfterSeconds">Optional duration in seconds before retrying.</param>
    public static ApiError Forbidden(string code, int? retryAfterSeconds = null) => new(403, code, retryAfterSeconds);

    /// <summary>Creates a 404 Not Found error.</summary>
    public static ApiError NotFound() => new(404, ErrorCodes.NotFound);

    /// <summary>Creates a 409 Conflict error.</summary>
    /// <param name="code">The specific conflict error code.</param>
    public static ApiError Conflict(string code) => new(409, code);

    /// <summary>
    /// Creates a 412 Precondition Failed error. Indicates the <c>If-Match</c> header
    /// does not match the target resource's current version (concurrency conflict).
    /// </summary>
    public static ApiError PreconditionFailed() => new(412, ErrorCodes.PreconditionFailed);

    /// <summary>
    /// Creates a 428 Precondition Required error. Indicates the mutating request
    /// lacks the required <c>If-Match</c> header to prevent lost updates.
    /// </summary>
    public static ApiError PreconditionRequired() => new(428, ErrorCodes.PreconditionRequired);

    /// <summary>Creates a 422 Unprocessable Entity error for moderation policy violations.</summary>
    public static ApiError ModerationRejected() => new(422, ErrorCodes.ModerationRejected);

    /// <summary>Creates a 423 Locked error for temporarily locked user accounts.</summary>
    /// <param name="retryAfterSeconds">The lockout duration in seconds remaining.</param>
    public static ApiError Locked(int retryAfterSeconds) =>
        new(423, ErrorCodes.AccountLocked, retryAfterSeconds);

    /// <summary>Creates a 429 Too Many Requests error when rate limits are exceeded.</summary>
    /// <param name="retryAfterSeconds">The duration in seconds the caller must wait before retrying.</param>
    public static ApiError TooManyRequests(int retryAfterSeconds) =>
        new(429, ErrorCodes.TooManyRequests, retryAfterSeconds);
}

/// <summary>Standard contract error codes (<c>backend-api-map.md</c> → Error codes).</summary>
public static class ErrorCodes
{
    /// <summary>The incoming request payload or structure is invalid.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The pagination cursor token is malformed or invalid.</summary>
    public const string InvalidCursor = "invalid_cursor";

    /// <summary>The requested entity or resource was not found.</summary>
    public const string NotFound = "not_found";

    /// <summary>Authentication credentials are missing or invalid.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>Rate limit threshold has been exceeded.</summary>
    public const string TooManyRequests = "too_many_requests";

    /// <summary>Submitted content violates automated or manual moderation guidelines.</summary>
    public const string ModerationRejected = "moderation_rejected";

    /// <summary>An unexpected server-side error occurred.</summary>
    public const string ServerError = "server_error";

    /// <summary>Supplied username, email, or password does not match.</summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>Operation requires an email address that has been verified.</summary>
    public const string EmailNotVerified = "email_not_verified";

    /// <summary>The user account is temporarily or permanently locked.</summary>
    public const string AccountLocked = "account_locked";

    /// <summary>Verification or one-time code is invalid.</summary>
    public const string InvalidCode = "invalid_code";

    /// <summary>Verification or one-time code has expired.</summary>
    public const string CodeExpired = "code_expired";

    /// <summary>The provided password does not meet complexity requirements.</summary>
    public const string WeakPassword = "weak_password";

    /// <summary>The current password supplied for modification is incorrect.</summary>
    public const string InvalidCurrentPassword = "invalid_current_password";

    /// <summary>Required user consent or legal agreement has not been accepted.</summary>
    public const string ConsentRequired = "consent_required";

    /// <summary>The refresh token is invalid, expired, or revoked.</summary>
    public const string InvalidRefreshToken = "invalid_refresh_token";

    /// <summary>An installation identifier header or property is missing.</summary>
    public const string InstallationIdRequired = "installation_id_required";

    /// <summary>The route calculation payload or geometry is invalid.</summary>
    public const string InvalidRoute = "invalid_route";

    /// <summary>The saved monuments collection payload is invalid.</summary>
    public const string InvalidSavedMonuments = "invalid_saved_monuments";

    /// <summary>The memory submission payload is invalid.</summary>
    public const string InvalidMemory = "invalid_memory";

    /// <summary>Users cannot report their own submissions or content.</summary>
    public const string CannotReportOwn = "cannot_report_own";

    /// <summary>The report or complaint payload is invalid.</summary>
    public const string InvalidReport = "invalid_report";

    /// <summary>The acknowledgement submission is invalid.</summary>
    public const string InvalidAcknowledgement = "invalid_acknowledgement";

    /// <summary>Uploaded image exceeds maximum allowable file size.</summary>
    public const string PhotoTooLarge = "photo_too_large";

    /// <summary>File upload payload, format, or signature is invalid.</summary>
    public const string InvalidUpload = "invalid_upload";

    /// <summary>The temporary upload session or URL has expired.</summary>
    public const string UploadExpired = "upload_expired";

    /// <summary>Data export parameters are invalid.</summary>
    public const string InvalidExport = "invalid_export";

    /// <summary>Maximum allowed export operations quota has been reached.</summary>
    public const string ExportQuotaExceeded = "export_quota_exceeded";

    /// <summary>The feedback payload is invalid.</summary>
    public const string InvalidFeedback = "invalid_feedback";

    /// <summary>Concurrency check failed; resource version does not match.</summary>
    public const string PreconditionFailed = "precondition_failed";

    /// <summary>A concurrency precondition header (<c>If-Match</c>) is required.</summary>
    public const string PreconditionRequired = "precondition_required";

    /// <summary>The supplied idempotency key has already been executed with different parameters.</summary>
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    /// <summary>A request with the supplied idempotency key is currently processing.</summary>
    public const string IdempotencyInProgress = "idempotency_in_progress";

    /// <summary>Multi-factor authentication (MFA) token/code is incorrect.</summary>
    public const string InvalidMfaCode = "invalid_mfa_code";

    /// <summary>Multi-factor authentication (MFA) challenge has expired.</summary>
    public const string MfaExpired = "mfa_expired";

    /// <summary>User account does not have MFA configured or enrolled.</summary>
    public const string MfaNotEnrolled = "mfa_not_enrolled";

    /// <summary>The staff account or assignment is invalid.</summary>
    public const string InvalidStaff = "invalid_staff";

    /// <summary>Cannot demote or remove the system's sole remaining administrator.</summary>
    public const string LastAdmin = "last_admin";

    /// <summary>Users are prohibited from modifying their own administrative status or privileges.</summary>
    public const string CannotModifySelf = "cannot_modify_self";

    /// <summary>The requested state transition is illegal or unrecognized.</summary>
    public const string InvalidTransition = "invalid_transition";

    /// <summary>The moderation or workflow decision value is invalid.</summary>
    public const string InvalidDecision = "invalid_decision";

    /// <summary>The monument payload, coordinates, or identifiers are invalid.</summary>
    public const string InvalidMonument = "invalid_monument";

    /// <summary>A monument with the same identifier or unique attributes already exists.</summary>
    public const string MonumentExists = "monument_exists";

    /// <summary>The association or hierarchy of architectural complex members is invalid.</summary>
    public const string InvalidComplexMembers = "invalid_complex_members";

    /// <summary>A monument without coordinates cannot be used as a route stop.</summary>
    public const string MonumentWithoutLocation = "monument_without_location";

    /// <summary>The catalog import file or its image list is invalid.</summary>
    public const string InvalidImport = "invalid_import";

    /// <summary>A work with the same key already exists.</summary>
    public const string WorkExists = "work_exists";

    /// <summary>A complex with the same key already exists.</summary>
    public const string ComplexExists = "complex_exists";

    /// <summary>Images referenced by hash have not been uploaded to the import store yet.</summary>
    public const string ImportImagesMissing = "import_images_missing";
}
