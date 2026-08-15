# Unity UI QA運用

## 目的

UI変更後に、UI Toolkitの構造検査、実画面のスクリーンショット、視覚レビュー、コードレビューを同じ順序で実行する。画像を撮影できなかった変更は合格扱いにしない。

## 前提

- Unityは `6000.5.6f1` を使用する。
- 初期の実画像撮影はmacOSスタンドアロンQAビルドで行う。
- CIではEditMode／PlayModeの構造テストとログ保存を行う。
- WebGLは初期段階では手動スモークとし、ブラウザ画像を取得できなかった場合は未検証とする。
- 現在のUIはUI Toolkitで構成されているため、uGUIのCanvas ScalerではなくPanelSettings、USS、`worldBound`、`resolvedStyle`を検査する。

## 実行

Unity Editorを閉じた状態で、リポジトリルートから実行する。

```bash
node Tools/UiQa/run-ui-qa.mjs
```

上記は次を実行する。

1. EditModeテスト
2. PlayModeテスト
3. Mac Development QAビルド
4. 基準解像度でのSmoke撮影
5. PNGとメタデータの検証

ストレス解像度を含める場合：

```bash
node Tools/UiQa/run-ui-qa.mjs --include-stress
```

特定画面だけを確認する場合：

```bash
node Tools/UiQa/run-ui-qa.mjs --scenario battle.default --viewport 960x540
```

既存QAビルドを再利用する場合は `--skip-build`、Unityテストを再利用する場合は `--skip-tests` を付ける。ただし、最終判定では新しい実装に対するテスト結果と撮影結果を使う。

## シナリオと解像度

初期Smoke対象は次の19シナリオである。

- `title.default`
- `workshop.default`
- `achievements.default`
- `runsetup.default`
- `runsetup.developer`
- `runsetup.character`
- `runsetup.level-menu`
- `map.default`
- `map.bottom`
- `battle.default`
- `battle.hand-overflow`
- `battle.map-reference`
- `forge.menu`
- `forge.orb`
- `forge.orb-confirmation`
- `forge.upgrade`
- `forge.upgrade-confirmation`
- `forge.orb-empty`
- `forge.upgrade-empty`

基準解像度は `960x540` と `1920x1080`。ストレス条件は `1280x800`、`1024x768`、`800x450` である。

UI変更時は変更画面の通常状態と該当するEdge状態を確認する。リリース前は全Smokeシナリオとストレス解像度を実行する。

## 成果物

```text
qa-artifacts/
  builds/
  logs/
  test-results/
  screenshots/
    Phase5UI__battle.default__ja__960x540__mac.png
    Phase5UI__battle.default__ja__960x540__mac.json
```

JSONにはシーン、シナリオ、状態、言語、プラットフォーム、解像度、Unityバージョン、画像パス、構造検査結果を保存する。

## 標準ワークフロー

1. メインエージェントが実装する。
2. EditMode／PlayModeテストを実行する。
3. Mac QAビルドを作成して対象シナリオを撮影する。
4. PNG、JSON、解像度、構造検査結果を確認する。
5. `qa-artifacts/review-packet.json` の `captureId` を確認し、`prompts/visual-review.md` に従う読み取り専用のUI視覚レビューを実行する。
6. 同じ `captureId` を使い、`prompts/code-review.md` に従う読み取り専用のコードレビューを実行する。
7. 2つのJSONを `qa-artifacts/reviews/visual-review.json` と `qa-artifacts/reviews/code-review.json` に保存する。
8. 撮影をやり直さず、レビューゲートだけを検証する。

```bash
node Tools/UiQa/verify-review-gate.mjs
```

9. メインエージェントが指摘を再現して修正する。
10. テスト、撮影、両レビューを再実行する。
11. P0／P1指摘と未検証状態がなくなったら完了とする。

`run-ui-qa.mjs --require-reviews` は撮影と同じ実行内でレビューJSONを検証する互換オプションである。通常は、レビュー担当が撮影後の `captureId` を確認してから `verify-review-gate.mjs` を実行する。

レビュー用サブエージェントの実行中は、メインエージェントもファイルを変更しない。サブエージェントは修正、コミット、ベースライン更新を行わない。

## 合格条件

- 現行Unityバージョンで関連テストが成功している。
- 対象シナリオごとに実際のPNGとJSONが存在する。
- 撮影失敗、画像欠落、画像を確認できない状態が `pass` になっていない。
- 要素の重なり、画面外へのはみ出し、文字切れ、Safe Area違反がない。
- P0／P1の未解決指摘がない。
- P2以下を残す場合は、対象画像、理由、対応予定をレビュー記録に残す。
- コードレビューに根拠のない指摘がなく、未解決の高重要度指摘がない。

レビューJSONの `packetId` が最新の `review-packet.json` の `captureId` と一致しない場合は、画像やテストが存在していても未検証として扱う。

## 撮影できない場合

Unityの起動、ビルド、シナリオ再現、描画、PNG保存のいずれかに失敗した場合は `unverified` と報告する。手動スクリーンショットを使う場合も、画像のシーン、状態、解像度、言語、取得方法を記録する。画像を確認できなければUI変更を完了扱いにしない。
