namespace NovaWallet.Application.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    BusinessRule,
    Forbidden,
}

public sealed record AppError(ErrorKind Kind, string Code, string Message)
{
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; init; }

    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}

public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, AppError? error)
    {
        _value = value;
        Error = error;
    }

    public AppError? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(AppError error) => new(default, error);
}
