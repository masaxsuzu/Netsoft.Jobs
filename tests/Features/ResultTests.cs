using Netsoft.Jobs.Features;

namespace Netsoft.Jobs.Features.Tests;

/// <summary>
/// <see cref="Result{T}"/> の成否は理由の有無から導かれる、という約束の縁を固定する。
/// 本体の使われ方は各ハンドラのテストが通しているので、ここには縁だけを置く。
/// </summary>
public sealed class ResultTests
{
    [Fact]
    public void 理由の無い失敗は作れない()
    {
        // 空の理由を許すと「失敗なのに何も直せない」結果ができて、
        // IsSuccess（理由が無い＝成功）の導出と矛盾する。作る時点で弾く契約。
        Assert.Throws<ArgumentException>(() => Result<string>.Failure([]));
    }
}
