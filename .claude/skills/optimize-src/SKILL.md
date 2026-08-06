---
name: optimize-src
description: src/ を 4 観点（再利用・単純化・効率・深さ）で並列レビューし、確定した改善だけを適用する。過去の判定を蓄積して再提案を防ぐ。品質改善用で、バグ探しには使わない。
---

# src/ の最適化

4 観点の読み取り専用レビューを並列で走らせ、突き合わせてから適用する。

## 手順

1. **下の「判定済み」を各エージェントのブリーフに丸ごと含める。** 再提案の禁止が
   このスキルの要。リストに無い指摘だけが収穫になる
2. **4 エージェントを並列起動**（読み取り専用・編集禁止）。全員に共通の縛り:
   - CLAUDE.md と docs/ を先に読む。この repo はコメントに「なぜ」を書く規約なので、
     **コメントを読んでから判断する**。意図が明記されているものは「意図的」と分類
   - コメントを削って行数を減らす提案は禁止。behaviour を変える提案も禁止
   - 偽陽性を絞り出さない。「無い」も正解
3. 観点ごとの追加の縛り:
   - **再利用**: 重複の行数と「変更時に直す箇所数」をコストとして数える
   - **単純化**: 死んだコードは grep で参照を数えて確かめる。新構文は読みやすさを
     落とすなら報告しない
   - **効率**: **測っていない推測を「遅い」と言わない**。数えられる事実か実測
     （ベンチはスクラッチパッドに書く）。用途はワークステーション 1 台・単一利用者・
     同時実行 1。この規模で意味の無い最適化は報告しない
   - **深さ**: 層の責務とのずれ、同じ知識の散在。大きな設計変更は提案しない
4. **突き合わせて適用**。適用するのは behaviour を変えない確定的なものだけ。
   設計判断を含むものは適用せず PR の「レビューで見てほしい点」へ。
   **巡をまたいで矛盾する提案は却下する**（前巡の判定と突き合わせること）
5. **ゲート**: build / `dotnet test -m:1 -p:CollectCoverage=true`（90/80）/ format。
   動きが変わる項目を含むなら E2E 込みで 3 回連続
6. **PR**: 設計判断を含むなら needs-review。出したら subscribe_pr_activity を張る
7. **このファイルの判定済みを更新する。** 怠ると次回が同じ議論を繰り返す

## 判定済み

### 適用済み（再提案不要）

- `JobId.From`→`TryFrom` 委譲 / `SqliteJobStore.ReadOneAsync` / `JobStatusText` 集約
- `JobTransitionResult.Previous` / `IsAllowed`・`IsSuccess` の導出化 / `Failure(params)` 削除
- `JobNotFoundException` の Domain 移動 / Contracts 切り出し / `Job.Apply` の Failed 統合
- `Path.Combine` の分岐削除 / `!IsFinite` / CI の NuGet・Playwright キャッシュ / `-m:1` 直列化
- **3 巡目**: trace context の SQL を Infrastructure へ（Web は委譲アダプタのみ）/
  接続イディオムを `SqliteConnections` に集約 / 観測の機構を
  `JobExecutionInstrumentation` へ移動 / traceparent 保存を `Recorded` でガード /
  ポーリングを keep-alive 認知型に / `JobEventsEndpoint` を `ReadAsync` 1 回に /
  UI 分離後に古くなったコメントの修正
- **4 巡目**: 観測の 3 か所（メトリクスのタグ・スパン属性）を `JobStatusText.ToText` へ通す /
  「個数 秒数」の案内文を `SubTaskParameters.Expectation` へ集約 /
  `JobsApiClient` の 400 本文読み取りを `ReadValidationErrorsAsync` へ /
  `EditJobHandler` の到達不能な `parameters is null` を削除 /
  Ui の `ListSubTasksAsync` と `JobApiRoutes.SubTasksFor` を削除（画面から呼ばれない）

### 畳むべきでない・意図的（再提案禁止）

- **定義の形式的な重複はむしろ推奨**（利用者の方針。2026-08 に明示）。型・結果型・応答型・
  ハンドラが「見た目が同じ」ことは畳む理由にならない。用途ごとに独立して動けるほうが、
  片方を変えたい日にもう片方まで動くより良い。**以下は判定済みで再提案禁止**:
  `PauseJobHandler` / `ResumeJobHandler`（差はトリガーとログ文言のみ）、
  `JobControlResult` / `CancelJobResult`（差はコメントのみ）、
  Ui の `JobControlResponse` / `CancelJobResponse`（差は型名のみ）、
  エンドポイントの 200/404/409 写し 3 箇所、`JobDto` と `JobListItemDto` の項目重複。
  なお「行数を減らす」以外の理由（引数が実際に使っていない値を要求している等）で
  署名を狭めるのは重複の畳み込みではないので、この禁止の対象外

- エンドポイント 3 本 / `*ServiceCollectionExtensions` / CAS 再試行ループ 3 箇所
- `JobChangeFeed` の Web・Ui 二重化 / API ルート定数の Ui 側写し
- `Job.Create` と `RegisterJobHandler.Validate` の検証二重化（層の方向が壊れる）
- `SqliteJobStore` の接続オープン 2 行 / `Home.razor` の入力 3 連 / `TimeProvider` の TryAdd
- **`RegisterJobCommand` の Contracts 移動**。Command はハンドラの入力であって線の契約では
  ない。移すとハンドラの入力変更と公開 API の契約変更が強制連動する。Ui は応答側に
  `RegisterJobResponse` / `CancelJobResponse` の写しを意図的に持っており、要求側だけ
  型共有すると非対称が増える。ずれは `JobsApiClientTests`（実エンドポイント直結）が検出
- 容量 1 Channel の合図 2 例（相互参照コメント済み。**3 例目が出たら**汎用型に括る）
- SSE の `data: changed` 直書き（実効契約は 1 bit。結合テストが検出網。
  **2 種類目のイベントが出たら** Contracts へ）
- 「観測の失敗で本務を失敗させない」catch 2 箇所 / `JobParameterHints` のキー写し
- `RunAsync` 先頭の `EnsureRecoveredAsync`（削ると復旧失敗の挙動が変わる）
- `FinishAsync` のログ if/else（三項にすると CA2254）
- **`transition.Rejection ?? throw` 3 箇所**（Cancel / Pause / Resume）。型の上では到達不能だが、
  `CancelJobHandler` に「拒否には必ず理由が付く。無いなら状態機械側の不整合なので、
  既定値で埋めずに落とす」と意図が明記されている。消すには Domain に非 null の読み口を
  足すことになり設計判断を含む。4 巡目で 2 エージェントの判定が割れ、コメントを採用した
- **`JobExecutionEngine` と `JobExecutionEngineFactory` の依存 7 個**（2 ファイルに 11 回）。
  畳むには依存を束ねる新しい型が要り設計判断。コンパイラが同期を強制するので黙って
  食い違う経路は無い。4 巡目で計数のうえ見送り
- **`JobBoard.CancelAsync` と `ControlAsync` の統合**（26 行・直す箇所 2）。手続きは逐語で
  同じだが、畳むには `CancelJobResponse` と `JobControlResponse` の間に変換を挟むことになり、
  「用途ごとに独立して動ける方が良い」という利用者の方針に触れる。4 巡目は適用せず PR で相談

### 効率: 実測・計数済み（再計測不要）

- 読み 15〜30µs / 書き 789µs〜2ms（WAL fsync 支配）。書き込み回数は状態機械が上限
- `ListAsync` の LIMIT 無し・通知ごと再取得・プリレンダー二重クエリ — この規模では問題なし
- 合図駆動の空スキャンは Job 1 件あたり 2 回 ~54µs（許容）。アイドル時の起床はゼロ
- SSE の常時接続は Ui→Web の 1 本のみ（タブが増えても増えない）
- **CI の `install-deps` 分割は却下**。ブラウザキャッシュのヒットと、まっさらなランナーに
  システムライブラリが有るかは無関係。スキップすると CI でだけ E2E が壊れる
- keep-alive 認知型ポーリングの**検出遅延 最悪 2×interval** と**時計の逆行時の抑止**は
  受容済みのトレードオフ（敵対的検証で確認。ワークステーション用途では実害微小）
- **一覧の N+1 は解消済み**（#36）。画面が Job ごとに `/subtasks` を呼んでいた頃は
  3 分半で 377 往復・うち 230 回（61%）がサブタスク取得だった。一覧の応答に進捗を載せ、
  集計を `CountByJobAsync` の 1 クエリ（GROUP BY）にして、変更通知 1 回あたり 1 往復に固定。
  **一覧の応答へ「Job ごとに引く」項目を足すときは、必ず集計側も 1 回で取れる形にする**
- **`SqliteSubTaskStore.AddRangeAsync` の command 使い回しは却下**（4 巡目に実測）。
  N=1000 で 17.8 ms 速くなるが、サブタスク 1 個は最低 1 秒かかる仕様なので実行時間の 0.002%。
  現実的な N（"3 5"）では 14 µs。`EditJobHandler` の全行読み・境界の線形走査・SSE の CTS 生成・
  画面の 2 つの GET の直列も同巡で計数のうえ却下
- 既知のフレーク: `TemporaryJobStore.Dispose` 等のプロセス全域 `ClearAllPools()` が
  並列テストの `InitializeAsync` と稀に競合する（tests 4 箇所）。直すなら性能でなく
  フレーク解消として。fixture 設計の判断を含むため未着手
