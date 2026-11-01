using System.Diagnostics.CodeAnalysis;

namespace VakifAtlas.SharedKernel;

/// <summary>
/// Encapsulates either a successful value of type <typeparamref name="T"/> or an <see cref="ApiError"/>.
/// Used across application workflows as an alternative to throwing exceptions for expected failures.
/// </summary>
/// <typeparam name="T">The type of the underlying success value.</typeparam>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(ApiError error)
    {
        _value = default;
        Error = error;
    }

    /// <summary>
    /// Gets the failure details if the operation failed; otherwise, <see langword="null"/>.
    /// </summary>
    public ApiError? Error { get; }

    /// <summary>
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>
    /// Gets the success value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when attempting to access the value on a failed result.
    /// </exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure: {Error.Code}");

    /// <summary>
    /// Implicitly converts a success value into a successful <see cref="Result{T}"/>.
    /// </summary>
    /// <param name="value">The success value to wrap.</param>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>
    /// Implicitly converts an <see cref="ApiError"/> into a failed <see cref="Result{T}"/>.
    /// </summary>
    /// <param name="error">The error payload to wrap.</param>
    public static implicit operator Result<T>(ApiError error) => new(error);

    /// <summary>
    /// Executes a delegate matching the current outcome and returns the computed result.
    /// </summary>
    /// <typeparam name="TOut">The return type of the projection functions.</typeparam>
    /// <param name="success">Function invoked with the value if the result is successful.</param>
    /// <param name="failure">Function invoked with the error if the result failed.</param>
    /// <returns>The value returned by the executed function.</returns>
    public TOut Match<TOut>(Func<T, TOut> success, Func<ApiError, TOut> failure) =>
        IsSuccess ? success(_value!) : failure(Error);
}

/// <summary>
/// Represents a void type for operations that produce no return value.
/// </summary>
public readonly record struct Unit
{
    /// <summary>
    /// Gets the singleton representation of a unit value.
    /// </summary>
    public static readonly Unit Value;
}
