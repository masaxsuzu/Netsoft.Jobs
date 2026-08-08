using System.Net.Http.Json;

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

    /// <summary>
    /// 終端の表示を待つ。**届かなかったときは API から見た状態を添えて投げ直す。**
    /// </summary>
    /// <remarks>
    /// 素の <c>Expect</c> だと、落ちたときに分かるのは「画面がこう見えていた」だけで、
    /// Job が進まなかったのか、進んだのに通知が届かなかったのかを区別できない。
    /// 区別が付かないと、次の一手（エンジンを見るのか通知を見るのか）が決まらない。
    /// </remarks>
    private async Task ExpectTerminalAsync(ILocator row, string text)
    {
        try
        {
            await Expect(row).ToContainTextAsync(text);
        }
        catch (Exception exception)
        {
            // API は「何になったか」、ログは「なぜそうなったか」を持つ。片方だけでは
            // 止まった場所が分からないので、2 つ揃えて出す。
            throw new InvalidOperationException(
                $"画面が「{text}」に到達しなかった。{Environment.NewLine}"
                + $"API から見た状態:{Environment.NewLine}{await _fixture.DescribeJobsAsync()}{Environment.NewLine}"
                + $"サービスのログ（末尾）:{Environment.NewLine}{_fixture.ReadRecentAppOutput()}",
                exception);
        }
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
        await ExpectTerminalAsync(row, "完了");

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
        await Expect(row).ToContainTextAsync("実行中");

        ILocator cancelButton = row.GetByRole(AriaRole.Button, new() { Name = "キャンセル" });
        await cancelButton.ClickAsync();

        // Running → 中止要求中 → 中止済み と進む。途中状態は速すぎて見えないことが
        // あるため、終端だけを待つ。
        await ExpectTerminalAsync(row, "中止済み");
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
    /// 拒否された操作のダイアログは、変更通知が届いても開いたまま。裏の一覧だけが新しくなる。
    /// </summary>
    /// <remarks>
    /// ここだけは実ブラウザでしか確かめられない。<c>&lt;dialog open&gt;</c> の開閉は DOM の
    /// 属性で決まるので、「要素を作り直していないから開いたまま」は Blazor の差分適用の
    /// 振る舞いに依存する。単体テスト（tests/Ui）で見られるのは状態が残ることまでで、
    /// 画面が実際に開いたままかは分からない。
    /// </remarks>
    [Fact]
    public async Task 操作のダイアログは変更通知が来ても開いたままで裏の一覧は更新される()
    {
        IPage page = await _fixture.Browser.OpenInteractiveAsync(_fixture.BaseUrl);
        string name = UniqueJobName("dialog");

        // 長く走る Job にする。ダイアログを開けている間ずっと編集できる状態でいてほしい。
        await page.FillAsync("#job-name", name);
        await page.SelectOptionAsync("#job-type", "subtasks");
        await page.FillAsync("#job-parameters", "1 60");
        await page.ClickAsync("button[type=submit]");

        ILocator row = RowFor(page, name);
        await Expect(row).ToBeVisibleAsync();

        // 読めない値の保存で拒否させる。ボタンが押せる状態のまま確実に失敗する操作はこれだけ
        // （他の操作のボタンは、押せない状態では disabled になっている）。
        await row.Locator("input.edit-parameters").FillAsync("読めない値");
        await row.GetByRole(AriaRole.Button, new() { Name = "保存" }).ClickAsync();

        ILocator dialog = page.Locator("dialog.operation-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // 画面の外で起きた変更（他の利用者や実行エンジン）が SSE で届く状況を作る。
        string behind = UniqueJobName("dialog-behind");
        await RegisterViaApiAsync(behind);

        await Expect(RowFor(page, behind)).ToBeVisibleAsync();
        await Expect(dialog).ToBeVisibleAsync();

        // 非モーダルなので Esc では閉じない。閉じる道はこのボタンだけ。
        await dialog.GetByRole(AriaRole.Button, new() { Name = "閉じる" }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();

        // 60 秒の Job を走らせたまま終わらない。テストはアプリを共有しており、
        // 実行の口を塞いだままにすると後続のテストの Job が始まらない（実際に落とした）。
        await row.GetByRole(AriaRole.Button, new() { Name = "キャンセル" }).ClickAsync();
        await ExpectTerminalAsync(row, "中止済み");

        await page.CloseAsync();
    }

    /// <summary>画面を通さずに Job を増やす。変更通知だけで一覧が動くことの材料。</summary>
    /// <remarks>すぐ終わる長さにする。上と同じ理由で、実行の口を長く塞がない。</remarks>
    private async Task RegisterViaApiAsync(string name)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"{_fixture.ApiBaseUrl}/api/jobs",
            new { name, jobType = "subtasks", parameters = "1 1" });

        response.EnsureSuccessStatusCode();
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
