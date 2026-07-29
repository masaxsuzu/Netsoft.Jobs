namespace Netsoft.Jobs.Features;

/// <summary>
/// 機能ハンドラの実行結果。成功なら値を、失敗なら項目単位のエラーを持つ。
/// </summary>
/// <remarks>
/// 入力の不正を例外にしないのは、それが想定内の分岐だから。例外にすると
/// 呼び出し側が catch で握りつぶしやすく、どの項目が悪いのかも型から読み取れない。
/// Domain の <see cref="Domain.JobTransitionResult"/> が遷移の拒否を結果で返すのと同じ考え方で、
/// この型は登録以降の機能（実行・一覧・キャンセル）でも共通に使う。
/// </remarks>
/// <typeparam name="T">成功したときに返す値の型。</typeparam>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, IReadOnlyList<ValidationError> errors)
    {
        IsSuccess = isSuccess;
        _value = value;
        Errors = errors;
    }

    /// <summary>成功したか。</summary>
    public bool IsSuccess { get; }

    /// <summary>失敗したか。</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>失敗の理由。成功した場合は空。</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>
    /// 成功したときの値。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 失敗した結果から読もうとした場合。失敗時に値は存在しないので、
    /// null を返して呼び出し側に判定を委ねるより、誤用として即座に落とす。
    /// </exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("失敗した結果には値がありません。IsSuccess を確認してください。");

    /// <summary>成功した結果を作る。</summary>
    public static Result<T> Success(T value) => new(true, value, []);

    /// <summary>失敗した結果を作る。</summary>
    /// <exception cref="ArgumentException">理由が 1 つも無い場合。理由の無い失敗は利用者に何も説明できない。</exception>
    public static Result<T> Failure(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException("失敗には理由が必要です。", nameof(errors));
        }

        return new Result<T>(false, default, errors);
    }

    /// <summary>失敗した結果を作る。</summary>
    public static Result<T> Failure(params ValidationError[] errors) =>
        Failure((IReadOnlyList<ValidationError>)errors);
}
