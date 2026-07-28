using Xunit;

namespace Netsoft.Jobs.Core.Tests;

/// <summary>
/// 開発サイクルの疎通確認。ビルド・テスト・CI が繋がっていることだけを確かめる。
/// 実装が入ったら削除してよい。
/// </summary>
public sealed class BuildSmokeTests
{
    [Fact]
    public void テストが実行される()
    {
        Assert.True(true);
    }
}
