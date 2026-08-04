using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;

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
    private readonly IJobTraceContextStore _traceContexts;
    private readonly ILogger<RegisterJobHandler> _logger;

    public RegisterJobHandler(
        IJobStore store,
        IJobIdFactory idFactory,
        TimeProvider timeProvider,
        IJobTraceContextStore traceContexts,
        ILogger<RegisterJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(idFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(traceContexts);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _idFactory = idFactory;
        _timeProvider = timeProvider;
        _traceContexts = traceContexts;
        _logger = logger;
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

        // JobId で絞ったタイムラインの先頭になる記録。検証エラーはログしない。
        // 400 として応答に出るもので、JobId も採番されていないので絞り込みようがない。
        _logger.LogInformation("Job {JobId} ({JobType}) を登録しました。", job.Id.Value, job.JobType);

        // 登録時の trace context を Job に同乗させる（messaging semantic conventions のパターン）。
        // ASP.NET Core はリクエストごとに Activity を作るので、トレースの購読者がいなくても
        // ここには本物の traceparent が入る。つまりバックエンドを繋いだその日から、
        // それ以降に登録された Job で実行スパンからの Link が意味を持つ。
        if (Activity.Current?.Id is { } traceParent)
        {
            try
            {
                await _traceContexts.SaveAsync(job.Id, traceParent, cancellationToken);
            }
            catch (Exception exception)
            {
                // 観測の失敗で登録を失敗させない。Job は保存済みで、
                // 欠けるのは実行スパンから登録トレースへの Link だけ。
                _logger.LogWarning(
                    exception,
                    "Job {JobId} の trace context を保存できませんでした。実行スパンに Link は付きません。",
                    job.Id.Value);
            }
        }

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
