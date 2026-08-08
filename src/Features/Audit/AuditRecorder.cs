using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Audit;

/// <summary>
/// 監査ログを書く唯一の場所。
/// </summary>
/// <remarks>
/// <para>
/// <b>書き手を 1 つにしてあるのが、この型の存在理由。</b>各コマンドが自分で書く形だと、
/// 同じ操作で 2 回書く経路や 1 回も書かない経路を、後から足した人が作れてしまう。
/// コマンドは <see cref="Audited{T}"/> で材料を返すだけにして、書くのはここに集める。
/// </para>
/// <para>
/// <b>書き込みに失敗しても、利用者の操作は成功として返す。</b>逆にすると、監査ログの
/// 置き場が壊れている間はキャンセルも一時停止もできなくなる ── 監査のために本業が止まる。
/// ただし黙って捨てると監査が穴だらけでも誰も気づけないので、<see cref="ILogger"/> へ
/// Error で残す。<b>穴が空いたことは、監査ログの外にだけ記録が残る。</b>
/// </para>
/// </remarks>
public sealed class AuditRecorder
{
    private readonly IAuditLogStore _store;
    private readonly ILogger<AuditRecorder> _logger;

    public AuditRecorder(IAuditLogStore store, ILogger<AuditRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 監査ログを書き、コマンドの結果を返す。
    /// </summary>
    /// <remarks>
    /// 結果を返す形にしてあるのは、<b>書かずに結果だけ取り出す道を作らないため</b>。
    /// エンドポイントはこれを通さないと <see cref="Audited{T}.Value"/> を使わずに済ませられる
    /// ── が、そこは型では止まらないので、API を叩いて件数を数えるテストで固定してある。
    /// </remarks>
    public async Task<T> RecordAsync<T>(Audited<T> audited, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audited);

        // 順に書く。並びの第 2 キーは書いた順なので、ここの順序がそのまま
        // 「要求 → 確定」の読み順になる（同じ時刻に並ぶ 2 件を区別できるのはこれだけ）。
        foreach (AuditLog log in audited.Logs)
        {
            await WriteAsync(log, cancellationToken);
        }

        return audited.Value;
    }

    /// <summary>結果を伴わない実施（システムのアクション）を書く。</summary>
    public async Task WriteAsync(AuditLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            await _store.WriteAsync(log, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "監査ログを保存できませんでした。実施者は {Actor}、内容は {Content} です。",
                log.Actor,
                log.Content);
        }
    }
}
