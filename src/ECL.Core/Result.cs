// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core;

/// <summary>
/// 一个可预期的失败。Code 稳定、可用于程序判断；Message 面向用户展示。
/// </summary>
public sealed record EclError(string Code, string Message, string? Detail = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Detail) ? Message : $"{Message} ({Detail})";
}

public readonly struct Result
{
    public EclError? Error { get; }

    public bool IsSuccess => Error is null;

    private Result(EclError? error) => Error = error;

    public static Result Ok() => new(null);

    public static Result Fail(string code, string message, string? detail = null) => new(new EclError(code, message, detail));

    public static Result Fail(EclError error) => new(error);
}

public readonly struct Result<T>
{
    public T? Value { get; }

    public EclError? Error { get; }

    public bool IsSuccess => Error is null;

    private Result(T? value, EclError? error)
    {
        Value = value;
        Error = error;
    }

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(string code, string message, string? detail = null) => new(default, new EclError(code, message, detail));

    public static Result<T> Fail(EclError error) => new(default, error);

    public T ValueOrThrow() => IsSuccess
        ? Value!
        : throw new InvalidOperationException($"操作失败：{Error}");
}
