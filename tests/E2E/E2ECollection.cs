namespace Netsoft.Jobs.E2E.Tests;

/// <summary>
/// E2E テストを 1 コレクションへまとめて直列化する。
/// </summary>
/// <remarks>
/// 全テストが 1 つのアプリと 1 つのブラウザを共有するため。並列に同じ画面を
/// 操作すると、互いのフォーム入力や一覧の行を壊して結果が不安定になる。
/// </remarks>
[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string Name = "E2E";
}
