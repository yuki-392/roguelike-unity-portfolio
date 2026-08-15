# DESIGN.md

> Project: カード式ローグライト
>
> このファイルは、Open Design と AI コーディングエージェントが新しい画面・UI部品・画像方向性を設計するときに参照するための、実装根拠付きデザインシステムである。
>
> 最終確認日: 2026-08-14
>
> 位置づけ: Unity実装の正本を置き換えない、Open Design向けのデザインシステム要約。

## Purpose

- 既存のUnity UIと同じ視覚言語で、新しい画面やオーバーレイを設計する。
- 色、タイポグラフィ、余白、角丸、状態表現、情報密度を既存ルールから引き継ぐ。
- Unity UI Toolkitへ実装しやすい構造と、ゲーム内の操作目的を保ったままプロトタイプを作る。
- 一般的なWebサイト、SaaSダッシュボード、ランディングページのデザインへ置き換えない。

このファイルは実行時設定でも、Unityの画面仕様全体でもない。ゲームルールやPresenterの責務は既存の仕様書・コードを参照し、ここでは見た目と画面体験に必要なルールだけを定義する。

## Design Principles

1. **暗いゲーム面を基準にする** — 深い紺色の背景と面、暖色の文字、金色の重要状態でカードバトルの視認性を作る。
2. **情報の優先順位を明確にする** — 画面の目的、次に行う操作、補助情報の順で視線を導く。
3. **操作できるものを状態で伝える** — hover、focus、selected、disabled、locked を色・枠・不透明度で表現する。
4. **触っている感覚を残す** — 控えめな角丸、右・下の暗い境界、押下時の1px移動で、カードやボタンをゲーム部品として見せる。
5. **画面の種類に合わせて密度を変える** — 通常画面はHeader / Scrollable Body / Action Footer、戦闘はHUD、宝箱・イベント・結果はFocus Panelを使う。
6. **既存トークンを再利用する** — 新しい画面固有の色、余白、角丸、影を追加しない。
7. **事実と提案を分ける** — 実装で確認できたルール、スクリーンショットから読み取った傾向、未確定の提案を混同しない。

## Source of Truth and Evidence Policy

### 優先順位

正確な値や実装可否を判断するときは、次の順で確認する。

1. [UI_GUIDELINES.md](UnityProject/Assets/UI/UI_GUIDELINES.md) — 全画面の共通UI規約
2. [UiDesignSystem.cs](UnityProject/Assets/Scripts/Presentation/UiDesignSystem.cs) — UIコードとテストが共有するトークン
3. [GameTheme.uss](UnityProject/Assets/UI/GameTheme.uss) — 実際のUI Toolkitスタイルと画面別例外
4. [GameUiDocument.cs](UnityProject/Assets/Scripts/Presentation/GameUiDocument.cs) と [GameScreenCoordinator.cs](UnityProject/Assets/Scripts/Application/Presenters/GameScreenCoordinator.cs) — 実行時の構造・画面遷移・入力
5. [docs/ui-qa/README.md](docs/ui-qa/README.md) とPresentationテスト — 検証対象・対応解像度・合格条件
6. [SCREEN_DESIGN.md](docs/unity-screen-design/SCREEN_DESIGN.md)、監査資料、スクリーンショット — 画面の意図や視覚的な参考

資料同士に差がある場合は、推測で統合しない。`Confirmed`、`Reference`、`Target`、`Unknown` のいずれかを付け、Unity実装を変更する作業とは分離する。

このファイルを更新しても `UI_GUIDELINES.md`、`UiDesignSystem.cs`、`GameTheme.uss` は自動更新されない。トークンを変更する場合は、Unity側の正本を同じ変更で更新し、関連テストとUI QAを実行する。

## Existing Screen Inventory

現行の `ScreenId` と `GameUiDocument` の実装に基づく一覧。Prefabを前提にせず、C#から生成されるVisualElementツリーとして扱う。

| ScreenId | 目的 | 現行レンダラー | 構造上の特徴 |
| --- | --- | --- | --- |
| `Title` | ゲーム開始・入口 | `RenderTitle` | 背景画像、ロゴ、縦方向のアクション群 |
| `Workshop` | カード合成・保管・オーブ装着 | `RenderWorkshop` | 合成／保管庫タブ、カードグリッド、インスペクター、確認ダイアログ |
| `Achievements` | 実績の閲覧 | `RenderAchievements` | スクロール可能なリスト行、解放／未解放状態 |
| `RunSetup` | 開始デッキ・オリジナルカード選択 | `RenderSetup` | キャラクター情報、カード選択、開始操作 |
| `Map` | ランの次ノード選択 | `RenderMap` | 縦方向のScrollView、ノード、接続線、選択可能状態 |
| `Battle` | 敵とのカードバトル | `RenderBattle` | HUD、敵ゾーン、エナジー、手札、山札・捨て札、ターン終了 |
| `Reward` | 戦闘後の報酬選択 | `RenderReward` | カード報酬と追加報酬の選択 |
| `Rest` | 回復またはカード強化 | `RenderRest` | 2択、強化対象カード一覧、確認状態 |
| `Forge` | カード鍛冶・遺物変換・通常強化 | `RenderForge` | 選択肢、タグ、スクロールする候補、結果確認 |
| `Shop` | 商品購入・カード削除 | `RenderShop` | 商品区画、価格、購入／削除ダイアログ |
| `Treasure` | 遺物の受け取り | `RenderTreasure` | 中央寄せのFocus Panel、単一の主要操作 |
| `Event` | イベント選択肢と結果 | `RenderEvent` | 情景文、条件付き選択肢、結果表示 |
| `Result` | GAME CLEAR / GAME OVER | `RenderResult` | 中央寄せのFocus Panel、結果情報、タイトル帰還 |

### Overlays

`OverlayId` は現在画面の上に表示する共通オーバーレイである。

| OverlayId | 内容 | 視覚ルール |
| --- | --- | --- |
| `Codex` | エネミー図鑑 | 背景を暗転し、一覧をスクロール表示。呼び出し元へ戻る |
| `Map` | バトル中のマップ確認 | 閲覧専用。現在位置と経路を主役にする |
| `Deck` | 山札確認 | カード一覧と詳細を表示 |
| `Discard` | 捨て札確認 | カード一覧と詳細を表示 |
| `Options` | 入力方式、セーブ、中断 | 背面を操作不可にし、閉じる操作を常時表示 |
| `BattleLog` | 戦闘ログ | 新しいログを下側に置き、時系列を崩さない |
| `Help` | 戦闘ヘルプ | ページ切替と選択中タブを明確にする |

## Visual Direction

- 基本の雰囲気は、暗い洞窟や夜のゲーム盤を思わせる、落ち着いたカードバトルUI。
- 背景は主役ではなく、暗色スクリーンを重ねて文字と操作部品のコントラストを確保する。
- 面は暗い紺色を重ね、情報のまとまりをSurfaceとSurface Raisedで区別する。
- 重要な選択・現在地・フォーカス・通知には暖色の金を使う。色を増やして派手さを出さない。
- 本文は暖色寄りの白、補助情報は低彩度のベージュにする。
- フォントはピクセル感のある `DotGothic16 Regular` を基準にし、タイトル・重要操作・カード名はBoldを使う。
- アートやロゴはゲーム固有の画像を使い、画像内に新しいUIテキストを焼き込まない。画像ボタンには別のテキストラベルまたはツールチップを付ける。
- グラデーション、グラスモーフィズム、過度な発光、Web用のヒーローセクション、不要なカードの入れ子は使わない。

## Design Tokens

### Color

以下は `UiDesignSystem.cs` と `UI_GUIDELINES.md` に定義された正規トークンである。

| Token | Value | 用途 |
| --- | --- | --- |
| `Background` | `#10141C` | 全体背景、暗い画面面 |
| `Surface` | `#1A2230` | パネル、セクション |
| `Surface Raised` | `#242F40` | ボタン、カード周辺、統計ボックス |
| `Text Primary` | `#F2EBD9` | 本文、見出し、主要ラベル |
| `Text Secondary` | `#B8B1A1` | 説明、注釈、補助情報 |
| `Accent / Focus` | `#E8B85A` | 主要操作、現在地、選択、通知 |
| `Danger` | `#D96363` | 削除など不可逆・危険操作 |
| `Success` | `#65B879` | 成功・完了状態 |
| `Border` | `#43536B` | 通常の境界 |
| `Shadow` | `#090C11` | 右・下側の暗い境界、深度 |

`GameTheme.uss` には既存状態表現として `#8ED8F8`、`#F3C969`、`#FBBF24` なども現れる。これらは既存実装の例外または画面固有状態であり、新しい画面の正規トークンとして追加してはならない。新規画面では上表のトークンを優先し、既存例外を再利用する必要がある場合はUnity側のレビュー対象として扱う。

### Typography

| Role | Size | 用途 |
| --- | ---: | --- |
| Annotation | 14px | 注釈、バージョン、統計ラベル |
| Body | 16px | 本文、説明、補助情報 |
| Action / Card Name | 18px | ボタン、カード名、一覧タイトル |
| Section Heading | 24px | セクション見出し |
| Screen Title | 32px | 画面タイトル |

- フォントリソースは `Fonts/DotGothic16-Regular`。
- タイトルと重要操作はBold、本文は通常ウェイトを基本とする。
- 背景画像上の文字アウトラインは可読性が必要な場合だけ使用する。
- 長い日本語は折り返しを許可し、タイトル・説明・ボタンの最小高を確保する。
- カード名など固定領域の文字は、実装にある切り詰め・自動調整の方針を尊重し、文字を重ねない。

### Spacing and Layout

| Token | Value | 基本用途 |
| --- | ---: | --- |
| `space-1` | 4px | 小さなラベル間、微調整 |
| `space-2` | 8px | 部品間、ボタン内側、カード外側 |
| `space-3` | 12px | 補助情報、入力欄内側 |
| `space-4` | 16px | パネル内側、主要セクション間 |
| `space-5` | 24px | 画面外周、Focus Panel内側、ページ導入部 |
| `space-6` | 32px | 画面タイトル周辺、スクロール末尾 |

- 画面外周は24px、パネル内側は16px、部品間は8pxを基本にする。
- 通常画面は `Header → Scrollable Body → Action Footer` の3領域に分ける。
- Bodyの内容は `Page Intro → Section / Grid` の順にする。FooterをBody内へ入れない。
- `screen-content` の最大幅は1180px。狭い画面では横並びを折り返し、Bodyだけをスクロールさせる。
- 戦闘は通常画面の文書レイアウトではなく、固定クロームとカード操作を持つHUDとして扱う。

### Shape, Border, and Shadow

| Element | Rule |
| --- | --- |
| Small label / tag | 角丸4px |
| Button / TextField | 角丸8px、最小高さ48px |
| Card / Panel / Overlay | 角丸12px |
| Default card | 幅190px、最小高さ270px、内側16px、外側8px |
| Compact card context | 工房などでは既存USSの128×182pxを使用する場合がある |
| Image button | 最小150×150px、主要画像は100pxまで |
| Focus Panel | 最大幅680px、画面中央に配置 |
| Overlay | 最大幅760px、画面高の88%を基準にする |
| Button interaction | 通常はSurface Raised、focus/hoverで枠を強調、押下時に1px下げる |

実装上は、通常の左・上境界に `Border`、右・下の2px境界に `Shadow` を使う部品がある。この不均衡な境界はカードやボタンの触覚的な見え方を保つためのルールであり、均一なCSSボーダーへ置き換えない。

## Components

| Component | Unity実装上の対応 | 用途 |
| --- | --- | --- |
| Game Root | `.game-root`, `GameRoot.uxml` | UI Toolkitのルート。フォントと背景の基準を持つ |
| Screen Shell | `.screen`, `.screen-body`, `.screen-scroll`, `.screen-content` | 画面の余白、最大幅、スクロール領域 |
| Header | `.header`, `.header-location`, `.header-stats`, `.header-actions` | ラン中の場所、HP、所持金、階層、共通操作 |
| Page Intro | `.page-intro`, `.screen-title`, `.screen-subtitle` | 画面の目的と説明 |
| Panel / Section | `.panel`, `.section` | 関連情報をまとめる面 |
| Action Footer | `.action-footer` | 次工程、戻る、閉じる、スキップなどの操作 |
| Action Button | `.action-button` と各variant | 主要操作・補助操作・静かな操作・危険操作 |
| Card | `.card` | カード画像、コスト、名前、分類、説明 |
| Image Button | `.image-button`, `.character-button`, `.enemy-button`, `.node-button` | 画像を伴う選択操作 |
| List Row | `.list-row`, `.codex-row` | 実績、図鑑、ログなどの一覧 |
| Empty State | `.empty-state` | 空データや利用不能状態を説明する |
| Focus Panel | `.focus-panel`, `.focus-image` | 宝箱、イベント、結果など目的が1つの画面 |
| Overlay | `.overlay-backdrop`, `.overlay` | 現在画面を保ったまま閲覧・確認する |
| Input Field | `TextField` | 工房のカード名などユーザー入力 |
| Battle HUD | `.screen-battle`, `.battle-stage`, `.battle-command-bar`, `.hand-strip` | 戦闘専用の固定クロームとカード操作 |

現行の画面は、主に `GameUiDocument.cs` が `UIDocument.rootVisualElement` にVisualElement、Button、ScrollViewを動的に追加し、`GameTheme.uss` でスタイルを適用する。新規設計でも、画面をWebページのDOMやUnity Prefabの階層としてだけ考えず、これらの共通クラスと状態を対応づける。

## Component States

| State | 現行表現 | 適用ルール |
| --- | --- | --- |
| Default | Surface Raised、通常境界 | 操作可能な通常状態 |
| Hover | 既存USSでは水色系の境界を使用 | マウス・ポインターの候補提示。情報の意味を変えない |
| Focus | 明るい境界、既存USSでは3px枠と明るい背景 | キーボード・ゲームパッド・決定対象を常に識別可能にする |
| Active / Pressed | `translate: 0 1px` | 押下の短いフィードバック。レイアウトを変えない |
| Selected | Accent系の枠・背景 | 現在選択中のカード、デッキ、タブ、ノード |
| Disabled | 不透明度45% | 条件不足や使用不可。理由を別の説明で伝える |
| Locked | 不透明度64% | 未解放の実績など、存在は示すが詳細を制限する状態 |
| Unavailable Combination | 不透明度42%と muted border | 工房で合成できない素材。選択を受理しない |
| Danger | `Danger`背景 | 削除、破棄、タイトル帰還など不可逆操作に限定 |
| Empty | 枠付きの共通Empty State | 無表示にせず、次に取れる行動または理由を示す |

状態表現は、色だけに依存しない。枠、ラベル、無効化、説明文、選択位置の変化を組み合わせ、背景画像が変わっても意味が伝わるようにする。

## Screen and Navigation Rules

- 画面状態は `GameScreenCoordinator` が所有し、`GameUiDocument` は状態を読み取って表示を再構築する。
- 通常の画面遷移はTitleからRunSetup、Workshop、Achievementsへ進み、RunSetupからMapへ進む。MapからBattle、Rest、Forge、Shop、Treasure、Eventへ進み、BattleからRewardまたはResultへ進む。
- Title、Workshop、Map、BattleからCodexを開ける。BattleではMap、Deck、Discard、Options、BattleLog、Helpもオーバーレイとして開く。
- オーバーレイは背面を黒または暗いShadow系で遮り、最前面のパネルと常時表示の閉じる操作を持つ。背面の操作を誤って受理しない。
- Map、Battle、Shopでは共通Headerを使う。Titleは背景と入口操作を主役にし、戦闘は独立したHUDとして扱う。
- Treasure、Event、Resultは一つの判断に集中するFocus Panelを使い、主要操作は1つを最も強く表示する。
- 確認、購入、削除、合成、タイトル帰還など不可逆操作には、結果とキャンセルを明示したダイアログを使う。
- 画面遷移や報酬取得を多重入力で二重実行しない。状態が完了した操作は再選択できないようにする。

## Input and Focus Rules

- すべての操作部品は、画像だけでなく意味の分かるラベルまたはツールチップを持つ。
- ボタンとカードの操作領域は48px未満にしない。狭いレイアウトでは部品を縮小する前に折り返し・スクロール・段組みを検討する。
- `Button` のfocus表示を消さない。focusは選択中・決定対象として明確に見える必要がある。
- Battleの入力方式は `Touch` と `Drag` を区別する。単体対象カードは敵へ、全体効果・防御などはActivation Lineへ操作を導く。
- ドラッグ中にカード順を変更せず、Pointer Captureとinline translateで操作中のカードだけを視覚的に動かす。
- ScrollViewは対象領域だけをスクロールさせ、固定Header・固定Footer・閉じる操作をスクロールの外に置く。
- UI Toolkit Buttonの決定イベントは既存テストで検証されている。キーボード／ゲームパッドの完全なfocus順序は未確定のため、新規画面では実機またはQAで確認する。

## Motion and Feedback

| Timing | 用途 |
| ---: | --- |
| 120ms | hover、focus、押下、短い状態変化 |
| 200ms | パネル・オーバーレイの表示、不透明度変化 |

- 色、不透明度、枠、位置だけを変える短いフィードバックを基本とする。
- プレイヤーの決定を待たせる長い演出や、操作不能になる装飾アニメーションは追加しない。
- 押下時の移動は1px程度に留め、レイアウト全体やカード内テキストを動かさない。
- 無効操作は状態を変更せず、必要に応じて通知・説明・SEで理由を伝える。

## Responsive and Safe Area Rules

- Unityの基準解像度は960×540。`GamePanelSettings.asset` は `Scale With Screen Size`、Reference Resolution `960×540`、Screen Match Mode `Shrink` を使用する。
- UI QAの基準ビューポートは960×540と1920×1080。ストレス条件は1280×800、1024×768、800×450である。
- 16:9を論理領域の基準とし、広いアスペクト比では背景を広げてもゲームUIの論理領域を無理に横へ伸ばさない。
- `screen-content` の最大幅は1180px。狭い画面では横並びを折り返し、本文領域だけを縦スクロールする。
- 画面固有の絶対配置を追加する場合は、960×540とストレス条件で重なり・切れ・操作不能がないことを確認する。
- Safe Area違反はUI QAの不合格条件である。ただし、現行コードで端末の `Screen.safeArea` へ追従する具体的な処理は、この文書作成時点では確認できていない。新規画面で対応済みと断定せず、実機確認を行う。
- uGUIのCanvas、Canvas Scaler、ScrollRect、ContentSizeFitterを前提にしない。現在の実装はUI ToolkitのPanelSettings、USS、VisualElement、ScrollViewを使う。

## Localization and Accessibility

- 現行UIの主要言語は日本語。英語の内部IDや技術用語を画面へ露出させない。
- DotGothic16の実際の字面が要素高からはみ出さないよう、見出し・説明・ボタンに十分な最小高と間隔を持たせる。
- 長い日本語、数値の桁増加、複数行ボタン、空状態、未解放状態を設計時から確認する。
- 文字と背景は高コントラストにし、色だけで選択・危険・無効を伝えない。
- 主要操作は48px以上のタッチ領域を確保する。
- 装飾画像は入力を受け取らない。入力可能な画像にはラベル、状態、フォーカス表現を付ける。
- スクリーンリーダー、完全なゲームパッドfocus順、端末Safe Areaの実機挙動は未検証として扱う。

## Unity Implementation Mapping

| Design concept | Unity source |
| --- | --- |
| UI tokens | `UnityProject/Assets/Scripts/Presentation/UiDesignSystem.cs` |
| Written UI contract | `UnityProject/Assets/UI/UI_GUIDELINES.md` |
| Runtime visual styles | `UnityProject/Assets/UI/GameTheme.uss` and `GameBaseTheme.uss` |
| Root UXML | `UnityProject/Assets/UI/GameRoot.uxml` |
| Panel scaling | `UnityProject/Assets/UI/GamePanelSettings.asset` |
| Runtime document and VisualElement tree | `UnityProject/Assets/Scripts/Presentation/GameUiDocument.cs` |
| Screen / overlay state | `UnityProject/Assets/Scripts/Application/Presenters/GameScreenCoordinator.cs` |
| Presentation tests | `UnityProject/Assets/Scripts/Presentation/Tests/` |
| UI QA scenarios | `Tools/UiQa/qaMatrix.mjs` and `UnityProject/Assets/Scripts/Presentation/UiQaScenarioCatalog.cs` |
| QA procedure and acceptance | `docs/ui-qa/README.md` |
| Reference screen descriptions | `docs/unity-screen-design/SCREEN_DESIGN.md` |
| Reference screenshots | `docs/unity-screen-design/screenshots/` |

現行の `UnityProject/Assets` では、画面を分割したUI Prefabは確認できていない。新しい設計でPrefab、ScriptableObject、uGUIの構造を提案する場合は、現行実装への移行案であることを明示し、既存構造として記述しない。

## Do

- 既存の暗色背景、紺色面、暖色の本文、金色の重要状態を保つ。
- 新しい画面を、既存のScreen、Overlay、HUD、Focus Panelのどれに近いか最初に決める。
- `UI_GUIDELINES.md` のトークン、共通クラス、状態表現を再利用する。
- 画面目的、主要操作、補助情報、空状態、無効状態、確認状態まで設計する。
- Unity UI Toolkitで再現できるVisualElement、Button、ScrollView、USSクラスへ対応づける。
- 960×540を基準に、UI QAのストレス解像度でも成立する余白と折り返しを設計する。
- 既存スクリーンショットを視覚的な参考にし、コピーではなく同じデザイン言語で新しい画面を作る。
- 実装確認済み、参照資料由来、提案、未確認を明記する。

## Do Not

- Webサイト風のヒーロー、ナビゲーションバー、料金表、ダッシュボード、グラスモーフィズムを持ち込まない。
- 新しい色、余白、角丸、影、フォントを画面固有ルールとして勝手に追加しない。
- uGUIのCanvas ScalerやPrefab階層を、現行Unity実装の事実として記述しない。
- `SCREEN_DESIGN.md` のPrefab、ScriptableObject、ScrollRect前提の記述を、現在の実装として無条件に採用しない。
- `COMMON_UI_AUDIT.md` の古い基準解像度1200×800を、現在のPanelSettingsの値として扱わない。
- 一つの画面に同じ強さのPrimary操作を大量に置かない。
- 文字を画像へ焼き込み、長文を固定高のカードやボタンへ押し込まない。
- スクリーンショットだけで確認できない入力、Safe Area、フォーカス順、実機挙動を推測で確定しない。
- DESIGN.mdだけを書き換えてUnity側の正本と一致したとみなさない。

## New Screen Checklist

新しい画面やオーバーレイを設計する前に、次を確認する。

- [ ] Screen、Overlay、Battle HUD、Focus Panelのどれかを決めた。
- [ ] 画面の目的と、ユーザーが次に行う主要操作を一文で説明できる。
- [ ] 共通Header、Body、Footer、Overlayのどこに属するか決めた。
- [ ] 既存トークンだけで色、文字、余白、角丸、影を定義した。
- [ ] Default、Hover、Focus、Active、Selected、Disabled、Locked、Emptyを設計した。
- [ ] 48pxの操作領域とキーボード／ゲームパッドfocusの扱いを確認した。
- [ ] 長い日本語、複数行、空データ、条件不足、桁増加を確認した。
- [ ] 960×540、1920×1080、1280×800、1024×768、800×450でレイアウト方針を決めた。
- [ ] 必要な画像、アイコン、ラベル、ツールチップのパスを確認した。
- [ ] `GameUiDocument.cs`、Coordinator、USSクラス、UI QAシナリオへの対応を記録した。
- [ ] 実装後に構造テスト、PNG撮影、視覚レビュー、コードレビューを行う計画がある。
- [ ] 確認できていない項目を `Unknown` として残した。

## Evidence and Unknowns

| Item | Status | 根拠・扱い |
| --- | --- | --- |
| Unity version | Confirmed | `UnityProject/ProjectSettings/ProjectVersion.txt` の `6000.5.6f1` |
| UI framework | Confirmed | `UIDocument`、`PanelSettings`、VisualElement、USSを使用 |
| Reference resolution | Confirmed | `GamePanelSettings.asset` の960×540 |
| Scale policy | Confirmed | `Scale With Screen Size` と `Shrink` の設定、およびUIガイドライン |
| Canonical tokens | Confirmed | `UI_GUIDELINES.md`、`UiDesignSystem.cs`、`GameTheme.uss` |
| Runtime screens | Confirmed | 13個の`ScreenId`と`GameUiDocument`のレンダラー |
| Runtime overlays | Confirmed | 7個の`OverlayId`と`RenderReferenceOverlay` |
| Reference screenshots | Reference | `docs/unity-screen-design/screenshots/` の18枚。現行ランタイムの全状態を保証するものではない |
| Safe Area implementation | Unknown | QAの合格条件にはあるが、`Screen.safeArea`への具体的な追従処理は未確認 |
| Full keyboard/gamepad focus order | Unknown | Buttonの決定イベントはテストされているが、全画面のfocus順は未文書化 |
| Mobile device behavior | Unknown | ストレス解像度のQA定義はあるが、実機のノッチ・Safe Area・タッチ密度は未検証 |
| Legacy screen design values | Reference / conflict | `SCREEN_DESIGN.md` の1200×720・Prefab記述、監査資料の1200×800は現行PanelSettingsと異なるため、目標または過去資料として扱う |

## Reference Files

- [UI_GUIDELINES.md](UnityProject/Assets/UI/UI_GUIDELINES.md)
- [UiDesignSystem.cs](UnityProject/Assets/Scripts/Presentation/UiDesignSystem.cs)
- [GameTheme.uss](UnityProject/Assets/UI/GameTheme.uss)
- [GameBaseTheme.uss](UnityProject/Assets/UI/GameBaseTheme.uss)
- [GamePanelSettings.asset](UnityProject/Assets/UI/GamePanelSettings.asset)
- [GameRoot.uxml](UnityProject/Assets/UI/GameRoot.uxml)
- [GameUiDocument.cs](UnityProject/Assets/Scripts/Presentation/GameUiDocument.cs)
- [GameScreenCoordinator.cs](UnityProject/Assets/Scripts/Application/Presenters/GameScreenCoordinator.cs)
- [SCREEN_DESIGN.md](docs/unity-screen-design/SCREEN_DESIGN.md)
- [COMMON_UI_AUDIT.md](UnityProject/Assets/UI/COMMON_UI_AUDIT.md)
- [UI QA README](docs/ui-qa/README.md)
- [Reference screenshots](docs/unity-screen-design/screenshots/)
