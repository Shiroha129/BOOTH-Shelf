# Changelog

## 1.0.7 — 2026-11-17

### 中文

- 对齐下载文件列表中的“已下载”文字。
- 本地库右键菜单在“跳转网页”下方新增“复制URLs”。

### 日本語

- ダウンロード一覧の「ダウンロード済み」の文字位置を調整。
- ローカルの右クリックメニューで「商品ページを開く」の下に「URLをコピー」を追加。

### English

- Align the “Downloaded” label with the filename and size.
- Add “Copy URLs” below “Open webpage” in the local library context menu.

## 1.0.6 — 2026-11-17

### 中文

- 合并购买选项和商品简介中的适配型号，统一模型别名并去重。
- 下载文件列表新增“已下载”标志。

### 日本語

- 購入バリエーションと商品説明の対応アバターをまとめ、別名と重複を整理。
- ダウンロードするファイルの一覧に「ダウンロード済み」を表示。

### English

- Merge supported avatars from purchase options and descriptions, normalize aliases and remove duplicates.
- Mark downloaded files in the download selection list.

## 1.0.5 — 2026-11-17

### 中文

- 商品大图和缩略图右键新增“复制图片”。
- 本地库资产右键新增“打开文件目录”。
- 点击资料库的“已下载”可定位到本地库对应资产。
- 适配型号清除折扣、价格、版本和附加说明，仅显示模型名称。

### 日本語

- 商品の大きな画像・サムネイルの右クリックに「画像をコピー」を追加。
- ローカルアセットの右クリックに「保存先フォルダーを開く」を追加。
- ライブラリの「ダウンロード済み」を押すと、ローカルの該当アセットを表示。
- 対応アバターから割引・価格・バージョン・補足説明を除き、モデル名だけを表示。

### English

- Add “Copy image” to the main image and thumbnail context menus.
- Add “Open file location” to the local asset context menu.
- Click “Downloaded” in the library to locate the corresponding local asset.
- Remove discounts, prices, versions and extra notes from supported avatar names.

## 1.0.4 — 2026-11-16

### 中文

- 商品大图和缩略图支持右键保存图片。
- 资料库右键菜单新增“跳转网页”和“复制URLs”。
- 本地库右键菜单新增“跳转资料库”，统一使用“跳转网页”。
- 资料库显示“已下载”标记，支持已下载／未下载筛选。

### 日本語

- 商品の大きな画像・サムネイルを右クリックして画像を保存。
- ライブラリの右クリックメニューに「商品ページを開く」「URLをコピー」を追加。
- ローカルの右クリックメニューに「ライブラリで表示」を追加し、商品ページの操作名を統一。
- 「ダウンロード済み」を表示し、ダウンロード済み／未ダウンロードで絞り込み。

### English

- Save product images from the main image or thumbnail context menu.
- Add “Open webpage” and “Copy URLs” to the library context menu.
- Add “Go to library” to the local menu and use “Open webpage” consistently.
- Mark downloaded assets and filter by downloaded or not downloaded.

## 1.0.3 — 2026-11-16

### 中文

- 下载文件名旁用红字显示大小，小于 1 GB 显示 MB，达到 1 GB 显示 GB。
- 本地资产详情新增“下载其他文件”按钮，便于补充下载。
- 下载记录的“选择文件”改为“打开目录”，定位该文件实际保存的目录。
- 资料库点击商品图片或标题，在软件内显示图片、原简介和适配型号；识别并排除全套及赞助选项。
- 点击本地资产详情的头像，跳回资料库并定位对应分栏、分页中的资产。

### 日本語

- ファイル名の横にサイズを赤字で表示。1 GB 未満は MB、1 GB 以上は GB を使用。
- ローカル詳細に「ほかのファイルをダウンロード」を追加。
- ダウンロード履歴の操作を「フォルダーを開く」に変更し、実際の保存先を表示。
- ライブラリの商品画像・タイトルから、画像、元の説明、対応アバターをアプリ内で表示。全セットと支援版を対応一覧から除外。
- ローカル詳細の画像から、ライブラリの該当タブ・ページに移動してアセットを表示。

### English

- Show file sizes beside filenames in red, using MB below 1 GB and GB from 1 GB onward.
- Add “Download other files” to local asset details.
- Replace “Choose files” in download history with “Open folder” for that file's actual saved directory.
- Open images, the original description and supported avatars inside the app when clicking a library item. Exclude full bundles and support editions from compatibility.
- Click a local asset's detail image to locate it on the correct library tab and page.

## 1.0.2 — 2026-11-12

- Quickly refresh the library automatically on startup.
- Add a Start with Windows option in settings.

## 1.0.1 — 2026-11-10

- Simplify notifications and settings.
- Separate Windows alerts from update checks, with a Never option to stop checking.

## 1.0.0 — 2026-11-10

- Pace BOOTH requests and pause automatic access when the site restricts it.
- Limit update alerts to downloaded assets, with an adjustable check interval.

## 0.5.13 — 2026-11-10

- Check GitHub for new versions and download, verify and install updates from the app.

## 0.5.12 — 2026-11-10

- Click outside the download or local files dialog to close it.

## 0.5.11 — 2026-11-10

- Add easy WebView2 setup when the runtime is missing.
- Refresh the Chinese, Japanese and English introductions with screenshots.

## 0.5.10 — 2026-11-10

First public release with library sync, tag filters, downloads, local file management and update alerts.
