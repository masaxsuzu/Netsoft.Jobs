using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.RegisterJob;

/// <summary>
/// Job を登録する。検証して採番し、待機中の Job を保存する。
/// </summary>
/// <remarks>
/// HTTP エンドポイントと画面（Blazor）の両方がこのクラスを直接呼ぶ。
/// ロジックをここに集めておかないと、画面から使うたびに HTTP を経由することになる。
/// </remarks>
public sealed class RegisterJobHandler
{
    private readonly IJobStore _store;
    private readonly IJobIdFactory _idFactory;
    private readonly TimeProvider _timeProvider;

    public RegisterJobHandler(IJobStore store, IJobIdFactory idFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(idFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _idFactory = idFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 登録する。入力が不正なら保存せず、項目単位のエラーを返す。
    /// </summary>
    public async Task<Result<JobDto>> HandleAsync(RegisterJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        IReadOnlyList<ValidationError> errors = Validate(command);
        if (errors.Count > 0)
        {
            // 1 件でも不正なら保存へ進まない。中途半端に採番だけ進めても捨てるしかない。
            return Result<JobDto>.Failure(errors);
        }

        Job job = Job.Create(
            _idFactory.Create(),
            command.Name,
            command.JobType,
            command.Parameters,
            _timeProvider.GetUtcNow());

        await _store.AddAsync(job, cancellationToken);

        return Result<JobDto>.Success(JobDto.From(job));
    }

    /// <summary>
    /// 入力を検証する。
    /// </summary>
    /// <remarks>
    /// 最初の 1 件で打ち切らずすべて集めるのは、画面が全項目のエラーを一度に出せるようにするため。
    /// </remarks>
    private static IReadOnlyList<ValidationError> Validate(RegisterJobCommand command)
    {
        List<ValidationError> errors = [];

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            errors.Add(new ValidationError("name", "名前を入力してください。"));
        }

        if (string.IsNullOrWhiteSpace(command.JobType))
        {
            errors.Add(new ValidationError("jobType", "Job の種類を入力してください。"));
        }

        // Parameters は空文字を許す（引数を取らない Job がある）。
        // null だけは「項目そのものが無い」ことを意味するので拒否する。
        if (command.Parameters is null)
        {
            errors.Add(new ValidationError("parameters", "パラメータを指定してください。空文字は指定できます。"));
        }

        return errors;
    }
}
