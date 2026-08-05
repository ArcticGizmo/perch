namespace Perch.Data;

/// <summary>
/// A trailing API failure at the tail of a session's transcript — Claude Code writes each failed request
/// as a synthetic assistant record carrying <c>isApiErrorMessage: true</c> and the HTTP
/// <c>apiErrorStatus</c> (e.g. 529 Overloaded, 429 rate-limited, 500 server error). Perch surfaces it as
/// the first-class <see cref="SessionStatus.ApiError"/> state rather than letting the failure's
/// busy→idle flip masquerade as a successful "done". Detail payload for the overlay row + notification;
/// see <see cref="TranscriptReader.GetLastApiError"/>. Null on any session that didn't end on an error.
/// </summary>
public sealed record ApiFailure(
    int Status,     // the HTTP status of the failed request (529, 429, 500, …); 0 if the field was absent
    string Message  // the human-readable error text Claude Code wrote ("API Error: 529 Overloaded. …")
);
