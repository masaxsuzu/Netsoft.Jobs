namespace Netsoft.Jobs.E2E.Tests;

/// <summary>
/// Home 画面のブラウザ越しの検証。実 Kestrel + 実 Chromium で、
/// 登録 → 実行 → 完了 / キャンセル / 検証エラーの流れを見る。
/// </summary>
/// <remarks>
/// 全テストが同じアプリを共有するため、各テストは一意な Job 名を使い、
/// 自分の名前を含む行だけを見る。他のテストの行が一覧にあっても壊れない。
/// </remarks>
[Collection(E2ECollection.Name)]
public sealed class HomePageE2ETests
{
    private readonly E2EFixture _fixture;

    public HomePageE2ETests(E2EFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task 登録したJobはリロードなしで完了まで進む()
    {
        IPage page = await _fixture.Browser.OpenInteractiveAsync(_fixture.BaseUrl);
        string name = UniqueJobName("complete");

        await page.FillAsync("#job-name", name);

        // 種類は選択式。選べること自体が「選択肢が API から取れている」ことの証拠で、
        // 取れていなければ subtasks の option が無く、ここで時間切れになる。
        // 2 サブタスク × 1 秒。境界（1 つ目の完了 → 2 つ目の開始）を実プロセスで 1 回通す。
        await page.SelectOptionAsync("#job-type", "subtasks");
        await page.FillAsync("#job-parameters", "2 1");
        await page.ClickAsync("button[type=submit]");

        ILocator row = RowFor(page, name);
        await Expect(row).ToBeVisibleAsync();

        // ここでリロードしないことが検証の核心。状態の変化はサーバ側の実行エンジンが
        // 起こし、変更通知 → 回路の再描画（push 更新）だけで画面に届くはずである。
        await Expect(row).ToContainTextAsync("完了");

        await page.CloseAsync();
    }

    [Fact]
    public async Task 実行中のJobをキャンセルするとCancelledになりボタンが無効になる()
    {
        IPage page = await _fixture.Browser.OpenInteractiveAsync(_fixture.BaseUrl);
        string name = UniqueJobName("cancel");

        // テストがキャンセルを押すまで確実に実行中でいられるよう、長めに待つ Job にする。
        await page.FillAsync("#job-name", name);
        await page.SelectOptionAsync("#job-type", "subtasks");
        await page.FillAsync("#job-parameters", "1 60");
        await page.ClickAsync("button[type=submit]");

        ILocator row = RowFor(page, name);
        await Expect(row).ToContainTextAsync("Running");

        ILocator cancelButton = row.GetByRole(AriaRole.Button, new() { Name = "キャンセル" });
        await cancelButton.ClickAsync();

        // Running → 中止要求中 → 中止済み と進む。途中状態は速すぎて見えないことが
        // あるため、終端だけを待つ。
        await Expect(row).ToContainTextAsync("中止済み");
        await Expect(cancelButton).ToBeDisabledAsync();

        await page.CloseAsync();
    }

    [Fact]
    public async Task 未入力で登録すると項目ごとのエラーが出て一覧は増えない()
    {
        IPage page = await _fixture.Browser.OpenInteractiveAsync(_fixture.BaseUrl);

        // 行数はテスト実行中、登録以外では変わらない（実行エンジンは状態を変えるだけ）。
        // 直列実行なので、ここで取った件数が「増えていない」ことの基準にできる。
        ILocator rows = page.Locator("tbody tr");
        int rowsBefore = await rows.CountAsync();

        await page.ClickAsync("button[type=submit]");

        await Expect(page.Locator("p.error:has-text(\"名前を入力してください。\")")).ToBeVisibleAsync();
        await Expect(page.Locator("p.error:has-text(\"Job の種類を入力してください。\")")).ToBeVisibleAsync();
        await Expect(rows).ToHaveCountAsync(rowsBefore);

        await page.CloseAsync();
    }

    /// <summary>
    /// アプリを共有する他のテストと衝突しないよう、Job 名を一意にする。
    /// </summary>
    private static string UniqueJobName(string purpose) => $"e2e-{purpose}-{Guid.NewGuid():N}";

    /// <summary>
    /// 自分の Job の行だけを指すロケータ。名前が一意なので 1 行に定まる。
    /// </summary>
    private static ILocator RowFor(IPage page, string name) => page.Locator($"tr:has-text(\"{name}\")");
}
