using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 決められた順に <see cref="JobId"/> を返す採番器。
/// </summary>
/// <remarks>
/// 採番を固定できないと「保存された Job の Id が採番された値である」ことを言えない。
/// 用意した分を使い切ったら例外にするのは、テストが気づかないうちに
/// 想定より多く採番していた場合に黙って通らないようにするため。
/// </remarks>
public sealed class StubJobIdFactory : IJobIdFactory
{
    private readonly Queue<JobId> _ids;

    public StubJobIdFactory(params string[] ids) =>
        _ids = new Queue<JobId>(ids.Select(JobId.From));

    /// <inheritdoc />
    public JobId Create() => _ids.Count > 0
        ? _ids.Dequeue()
        : throw new InvalidOperationException("用意した JobId を使い切りました。");
}
