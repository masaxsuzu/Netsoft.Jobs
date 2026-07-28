# Job 追加ガイド

[Job Execution Engine 設計書](./job-execution-engine.md) §6 の実務向け補足。
新しい Job を追加する人が読む文書であり、エンジン内部を知らなくても書けることを目指す。

参照してよいアセンブリは **`Netsoft.Jobs.Abstractions` だけ**。ここに出てこない型に依存し始めたら、設計が漏れている合図。

---

## 1. 最短経路

```csharp
// Parameters.cs
public sealed record ReportParameters
{
    [Required] public string TargetMonth { get; init; } = "";
}

// Handler.cs
[JobType("monthly-report", DisplayName = "月次レポート生成",
         Capabilities = JobCapabilities.Cancel | JobCapabilities.Retry)]
public sealed class MonthlyReportHandler : IJobHandler<ReportParameters>
{
    public async Task ExecuteAsync(ReportParameters p, IJobExecutionContext ctx)
    {
        await BuildAsync(p.TargetMonth, ctx.CancellationToken);
    }
}

// Program.cs (Host)
services.AddJob<MonthlyReportHandler, ReportParameters>();
```

これだけで、一覧表示・詳細表示・進捗表示・キャンセル・履歴・再実行がすべて動く。UI にもエンジンにも一切手を入れない。

---

## 2. 能力を決める

`Capabilities` は「この Job 型が原理的に何をサポートするか」の宣言。宣言していない操作は API 層で拒否され、Job 実装には届かない。

| 能力 | 宣言してよい条件 |
|---|---|
| `Cancel` | 途中で中断しても、外部状態が壊れないか、`finally` で確実に片付けられる |
| `Pause` | 処理を中断して**任意の時間**放置しても安全な地点が存在する。接続タイムアウト・装置のウォッチドッグに注意 |
| `Retry` | 同じパラメータでもう一度実行しても、結果が壊れない (冪等、または上書きで正しくなる) |

### 判断に迷ったときの原則

**宣言しない方に倒す。** 後から能力を足すのは安全だが、外すのは利用者の操作を奪う変更になる。

### `Pause` を宣言してはいけない典型例

- 装置と開きっぱなしのセッションを持ち、無通信でタイムアウトする
- トランザクションを開いたまま処理を進める (長時間のポーズがロック保持につながる)
- 外部システムが一定時間内の完了を期待している

### `Retry` を宣言してはいけない典型例

- 装置に対する不可逆な操作 (切断・書き込み・物理動作)
- 追記型の外部登録 (2 回実行すると二重登録になる)
- 実行時刻に依存し、後から同じ結果が得られない処理

---

## 3. Checkpoint の置き方

`await ctx.CheckpointAsync()` を呼んだ地点でのみ、一時停止とキャンセルが効く。

### 基本形

```csharp
foreach (var item in items)
{
    await ctx.CheckpointAsync();     // ループ先頭に 1 行
    await ProcessAsync(item, ctx.CancellationToken);
}
```

### 置く場所の指針

| 置く | 置かない |
|---|---|
| ループの先頭 | トランザクションやファイル書込の**途中** |
| 独立した処理単位の境界 | `try` の外に片付け処理がある状態 |
| 長い I/O の前後 | ロックを保持している間 |

**「ここで数時間止まっても、あとで何事もなく続きから再開できるか」** を自問する。答えが No なら、そこは Checkpoint ではない。

### 呼ぶ頻度

- 目安として **1 秒以内に 1 回は通過する**位置に置く。数時間の Job で 10 分に 1 回しか通らないと、キャンセルが 10 分効かない。
- 逆に呼びすぎのコストは無視してよい。要求がないときは同期的に即返る (`ValueTask`、アロケーションなし)。100 万回のループ内で呼んでも問題ない。

### `CheckpointAsync` と `CancellationToken` の使い分け

| | 用途 |
|---|---|
| `await ctx.CheckpointAsync()` | **自分のコードの合間**で中断を確認する。Pause もここで効く |
| `ctx.CancellationToken` | **他人のメソッドに預ける**。`ReadAsync(buf, ctx.CancellationToken)` のように、I/O 呼び出しの引数として渡す |

両方使う。トークンを渡せば長い I/O の途中でもキャンセルが効き、Checkpoint を挟めばそれ以外の地点でも効く。

---

## 4. 進捗を報告する

```csharp
ctx.ReportProgress(JobProgress.Of(
    ratio: (double)done / total,     // 不明なら null
    text : $"{done} / {total} 件",   // 必須。UI が常に表示する
    phase: "取込"));                  // 任意。段階の表示に使う
```

`ratio` が出せない場合は素直に `null` にする。UI は不定プログレスバーに切り替える。無理に推定値を出すと、進捗が戻ったり 99% で止まったりして利用者の信頼を失う。

```csharp
ctx.ReportProgress(JobProgress.Indeterminate("装置応答待ち", phase: "測定"));
```

報告頻度は気にしなくてよい。エンジン側が集約・間引きしてから DB と UI に流す (毎ループ呼んで構わない)。

---

## 5. ログを出す

```csharp
ctx.Log(JobLogLevel.Information, $"{rows} 件を取り込みます");
ctx.Log(JobLogLevel.Warning, $"行 {n} をスキップしました: {reason}");
ctx.Log(JobLogLevel.Error, "変換に失敗しました", ex);
```

- Job 詳細画面に表示され、DB に保存される。
- 1 Job あたりの上限がある (既定 10,000 行)。超えると古い行から落ちる。**ループの毎回でログを出さない。** 進捗は `ReportProgress` の役目。
- 認証情報・個人情報を書かない。ログは保持期間中ずっと残り、エクスポートされうる。

---

## 6. エラーの扱い

| やること | 結果 |
|---|---|
| 例外をそのまま投げる | `Failed`。例外メッセージが `FailureMessage`、スタックトレースが `FailureDetail` になる |
| `JobFailedException` を投げる | `Failed`。利用者向けの短いメッセージを明示的に指定できる |
| `OperationCanceledException` を投げる (トークン発火時) | `Cancelled`。**握りつぶさない** |

```csharp
// 利用者に見せたいメッセージを明示する
throw new JobFailedException($"入力ファイルの形式が不正です (行 {n})", innerException: ex);
```

**キャンセル例外を握りつぶさないこと。** `catch (Exception)` で全部拾うと、キャンセルが `Failed` として記録される。

```csharp
try { await DoAsync(ctx.CancellationToken); }
catch (OperationCanceledException) { throw; }        // ← 必ず再スロー
catch (IOException ex) { throw new JobFailedException("ファイルにアクセスできません", ex); }
```

後始末は `finally` か `await using` に置く。キャンセル・失敗のどちらでも確実に走る。

```csharp
await using var session = await device.OpenAsync(ctx.CancellationToken);
// ここで何が起きても session は閉じられる
```

---

## 7. 一時停止に対応する (`Pause` を宣言した場合のみ)

Checkpoint を置くだけで、その場で処理を止めて待つ形の一時停止は動く。

再開時に何かをやり直す必要がある場合だけ、`IPausableJobHandler` を追加実装する。

```csharp
public sealed class DeviceScanHandler
    : IJobHandler<ScanParameters>, IPausableJobHandler
{
    private IDeviceSession? _session;

    public async Task OnPausingAsync(IJobExecutionContext ctx)
    {
        ctx.Log(JobLogLevel.Information, "装置セッションを解放します");
        if (_session is not null) { await _session.DisposeAsync(); _session = null; }
    }

    public async Task OnResumingAsync(IJobExecutionContext ctx)
    {
        ctx.Log(JobLogLevel.Information, "装置セッションを再確立します");
        _session = await _device.OpenAsync(ctx.CancellationToken);
    }
}
```

呼び出し順序は `OnPausingAsync` → (`Paused` で待機) → `OnResumingAsync` → Checkpoint から復帰。

**注意:** 一時停止はプロセス内の中断であり、永続化されない。アプリを終了すると `Paused` の Job も `Failed` になる。「明日まで止めておく」用途には使えない。

---

## 8. 再実行の可否を実行時に判定する (`Retry` を宣言した場合のみ)

静的な宣言では表現できない条件があるとき、`IJobRetryPolicy<T>` を実装する。

```csharp
public sealed class DeviceScanRetryPolicy : IJobRetryPolicy<ScanParameters>
{
    public RetryDecision CanRetry(JobRetryContext<ScanParameters> ctx) =>
        ctx.OriginalStatus switch
        {
            // 装置に触れる前に落ちたなら安全に再実行できる
            JobStatus.Failed when ctx.OriginalProgress.Phase == "準備"
                => RetryDecision.Allow(),
            JobStatus.Failed
                => RetryDecision.Deny("装置操作を開始済みのため再実行できません。装置状態を確認してください。"),
            _   => RetryDecision.Deny("正常終了した測定は再実行できません。"),
        };
}
```

拒否理由は UI にそのまま表示される。**利用者が次に何をすべきかが分かる文章**を書く。

---

## 9. 依存を注入する

ハンドラは DI コンテナから解決される。Job 1 回の実行ごとにスコープが作られるため、`Scoped` な依存を安全に受け取れる。

```csharp
public sealed class ImportHandler : IJobHandler<ImportParameters>
{
    private readonly ICsvReader _reader;
    private readonly IMasterRepository _master;
    public ImportHandler(ICsvReader reader, IMasterRepository master)
        => (_reader, _master) = (reader, master);
}
```

- `IJobExecutionContext` をコンストラクタで受け取ることはできない (実行ごとに変わるため、`ExecuteAsync` の引数で渡される)。
- ハンドラのインスタンスは 1 回の実行につき 1 つ。フィールドに状態を持ってよい (§7 の `_session` のように)。

---

## 10. テストする

`Netsoft.Jobs.TestKit` を参照する。

### 正常系

```csharp
var ctx = new FakeJobExecutionContext();
await new ImportHandler(fakeReader, fakeMaster).ExecuteAsync(parameters, ctx);

Assert.Equal(1.0, ctx.LastProgress.Ratio);
Assert.Contains(ctx.Logs, l => l.Level == JobLogLevel.Information);
```

### キャンセル時に後始末が走るか

```csharp
var ctx = new FakeJobExecutionContext();
ctx.CancelAtCheckpoint(3);           // 3 回目の Checkpoint でキャンセルさせる

await Assert.ThrowsAsync<OperationCanceledException>(
    () => handler.ExecuteAsync(parameters, ctx));

Assert.True(fakeDevice.Closed);      // finally / await using が効いたか
Assert.Equal(3, ctx.CheckpointCount);
```

### Checkpoint の粒度が十分か

```csharp
var ctx = new FakeJobExecutionContext();
await handler.ExecuteAsync(largeInput, ctx);

// 処理単位ごとに Checkpoint を通過しているか
Assert.True(ctx.CheckpointCount >= largeInput.Rows,
    "Checkpoint が粗すぎます。キャンセルが即座に効きません。");
```

### 一時停止 → 再開

```csharp
var ctx = new FakeJobExecutionContext();
ctx.PauseAtCheckpoint(2, resumeAfter: true);

await handler.ExecuteAsync(parameters, ctx);

Assert.Equal(1, ctx.PauseCount);
Assert.True(fakeDevice.Reopened);    // OnResumingAsync が効いたか
```

---

## 11. チェックリスト

Job を追加したら、マージ前に確認する。

- [ ] `Capabilities` の宣言が実際の性質と一致している (迷ったら宣言しない)
- [ ] ループの先頭に `await ctx.CheckpointAsync()` がある
- [ ] Checkpoint が 1 秒以内に 1 回は通過する粒度になっている
- [ ] 長い I/O に `ctx.CancellationToken` を渡している
- [ ] `catch (OperationCanceledException) { throw; }` を書いている (全捕捉している場合)
- [ ] 後始末が `finally` / `await using` にある
- [ ] `ReportProgress` の `text` が、進捗を知らない人にも意味が通る
- [ ] `ratio` が出せないときに嘘の値を入れていない
- [ ] ログをループ内で毎回出していない
- [ ] ログ・エラーメッセージに認証情報や個人情報が含まれない
- [ ] `Retry` を宣言したなら、二重実行しても壊れないことを確認した
- [ ] `Pause` を宣言したなら、任意時間の停止に耐えることを確認した
- [ ] TestKit で正常系・キャンセル系のテストを書いた
