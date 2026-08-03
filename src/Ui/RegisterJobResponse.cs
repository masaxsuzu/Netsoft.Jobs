using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui;

/// <summary>
/// 登録 API の応答。登録できたか、入力のどの項目が不正だったかを表す。
/// </summary>
/// <remarks>
/// エラーの形（項目名 → メッセージの配列）は API の ValidationProblem（RFC 9457 の
/// errors）そのままにしてある。画面は項目別表示に使うだけなので、写し替えるほど
/// 別の形が要らない。
/// </remarks>
public sealed class RegisterJobResponse
{
    private static readonly IDictionary<string, string[]> NoErrors =
        new Dictionary<string, string[]>();

    private RegisterJobResponse(JobDto? job, IDictionary<string, string[]> errors)
    {
        Job = job;
        Errors = errors;
    }

    /// <summary>登録された Job。検証に失敗した場合は null。</summary>
    public JobDto? Job { get; }

    /// <summary>項目名をキーにした検証エラー。成功した場合は空。</summary>
    public IDictionary<string, string[]> Errors { get; }

    /// <summary>登録できたか。</summary>
    public bool IsSuccess => Job is not null;

    /// <summary>登録に成功した応答を作る。</summary>
    public static RegisterJobResponse Registered(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new RegisterJobResponse(job, NoErrors);
    }

    /// <summary>検証に失敗した応答を作る。</summary>
    public static RegisterJobResponse Invalid(IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return new RegisterJobResponse(null, errors);
    }
}
