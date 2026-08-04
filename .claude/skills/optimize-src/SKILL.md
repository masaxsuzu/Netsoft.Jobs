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

### 畳むべきでない・意図的（再提案禁止）

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

### 効率: 実測・計数済み（再計測不要）

- 読み 15〜30µs / 書き 789µs〜2ms（WAL fsync 支配）。書き込み回数は状態機械が上限
- `ListAsync` の LIMIT 無し・通知ごと再取得・プリレンダー二重クエリ — この規模では問題なし
- 合図駆動の空スキャンは Job 1 件あたり 2 回 ~54µs（許容）。アイドル時の起床はゼロ
- SSE の常時接続は Ui→Web の 1 本のみ（タブが増えても増えない）
- **CI の `install-deps` 分割は却下**。ブラウザキャッシュのヒットと、まっさらなランナーに
  システムライブラリが有るかは無関係。スキップすると CI でだけ E2E が壊れる
