namespace Zirrus.Application;

/// <summary>Outcome categories that the API maps to HTTP status codes.</summary>
public enum OperationStatus
{
    /// <summary>Succeeded (200/201/204).</summary>
    Ok,

    /// <summary>The caller lacks permission (403).</summary>
    Forbidden,

    /// <summary>The resource does not exist or must not be disclosed (404).</summary>
    NotFound,

    /// <summary>The request conflicts with current state, e.g. a duplicate name (409).</summary>
    Conflict,

    /// <summary>The input is invalid (400).</summary>
    Invalid,
}

/// <summary>Result of a use case: a status, an optional value and an optional error message.</summary>
/// <typeparam name="T">Value type.</typeparam>
/// <param name="Status">Outcome category.</param>
/// <param name="Value">Value when <see cref="OperationStatus.Ok"/>.</param>
/// <param name="Error">Human-readable reason otherwise.</param>
public sealed record OperationResult<T>(OperationStatus Status, T? Value = default, string? Error = null);

/// <summary>Factory methods for <see cref="OperationResult{T}"/>.</summary>
public static class OperationResult
{
    /// <summary>Successful result.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">Value.</param>
    /// <returns>The result.</returns>
    public static OperationResult<T> Ok<T>(T value) => new(OperationStatus.Ok, value);

    /// <summary>Failed result.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="status">Failure category.</param>
    /// <param name="error">Reason.</param>
    /// <returns>The result.</returns>
    public static OperationResult<T> Fail<T>(OperationStatus status, string error) => new(status, default, error);
}
