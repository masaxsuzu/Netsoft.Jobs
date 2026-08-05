namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 変更の合図の縁。発火と購読の本流は購読サービスと画面のテストが通している。
/// </summary>
public sealed class JobChangeFeedTests
{
    /// <summary>
    /// 購読者がいなくても発火は安全に空振りする。購読サービスは画面（回路）が
    /// 1 つも繋がっていなくても動き出すので、これは起動直後に毎回通る本番の経路。
    /// </summary>
    [Fact]
    public void 購読者がいない発火は何も起こさない()
    {
        new JobChangeFeed().Publish();
    }
}
