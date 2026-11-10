# BOOTH Shelf

Windows 上的 BOOTH 资产资料库。同步已购、礼物和免费获取的商品，用标签和店铺整理资产，管理下载，并在文件更新时提醒。

**[下载 Windows 版](https://github.com/Shiroha129/BOOTH-Shelf/releases)** · [使用说明](docs/USAGE.zh-CN.md) · [构建说明](docs/DEVELOPMENT.md) · [更新记录](CHANGELOG.md)

当前版本为 **v0.5.10 预览版**，适用于 Windows 10/11 x64。本项目是独立的第三方工具，与 BOOTH、pixiv 及 BOOTH Library Manager 无隶属关系。

## 开始使用

1. 从 Releases 下载 `BOOTH-Shelf-v0.5.10-win-x64.zip`，解压后运行 `BOOTH-Shelf.exe`。
2. 需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。程序已包含 .NET 运行时，无需另装 .NET。
3. 点击「登录 BOOTH」，在官方网页完成登录，然后执行一次「全局同步」。以后使用「快速更新」。

WebView2 使用本软件独立的登录会话，不会直接继承 Edge 浏览器的 Cookie。开启「保留登录」后，软件会在本机加密保存会话备份，升级时通常可以继续使用；网站主动使会话失效时仍需重新登录。

## 功能

- 同步已购、礼物、免费三个来源，支持缓存、断点续传与增量更新；同步时收集标签和订单金额。
- 标签数量排序、收藏置顶、包含／任一／排除组合筛选，以及可保存的筛选预设。
- 按店铺浏览，关键词、类别、年龄限制等筛选。
- 已购和礼物资产可选择文件下载，调整下载目录；材质文件与偏好文件名优先显示。免费来源通过商品网页下载。
- 本地库查看已下载资产；打开文件、定位目录、删除单文件，或右键资产删除整个资产文件夹。删除使用 Windows 回收站。
- 无需下载资产即可检查已同步商品的文件更新；左侧「通知」与托盘气泡提示新变化。
- 订单金额统计、11 种显示货币、启动与本地零点自动更新汇率。
- 简体中文、繁體中文、日本語、English、한국어，默认识别系统语言；支持托盘运行和关闭方式设置。

## 数据与限制

登录会话、资料库、设置和下载索引保存在 `%LOCALAPPDATA%\BOOTH-Shelf`；下载文件保存在设置中选择的目录。会话备份使用 Windows 当前用户的加密保护。软件没有自己的云同步服务；联网访问 BOOTH/pixiv 完成登录和读取数据，汇率来自 Frankfurter / ECB。

更新通知是约 **15 分钟一次的增量检查**，不是 BOOTH 服务器的实时推送。软件退出后不再检查；网络故障、限流、下架商品、无法识别的版本或网站接口变化可能影响结果。首次启用主要记录之后的新变化。

当前数据目录尚未按 BOOTH 账号隔离；切换账号请使用独立数据目录，见使用说明。金额折算仅作参考，不代表银行卡当时实际扣款。EXE 暂未进行代码签名，Windows 可能显示未知发布者。

请只访问自己有权获取的内容，遵守 BOOTH 和商品作者的使用条款。MIT 许可证仅适用于本项目代码，不授予任何商品素材的再分发权。

## 开发与贡献

源码采用 C# / .NET 8 WinForms + Microsoft Edge WebView2，界面为本地 HTML、CSS、JavaScript。见 [构建与验证](docs/DEVELOPMENT.md)。问题反馈请附版本、操作步骤和脱敏后的错误信息；不要上传 Cookie、浏览器配置、完整账号资料库或私有下载链接。

代码使用 [MIT License](LICENSE)。第三方运行时与组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## English

BOOTH Shelf is an independent, open-source Windows desktop organizer for your BOOTH library. It supports purchased, gifted and free items, tag filters, shop browsing, selected downloads, local file management, and periodic asset update notifications without downloading every asset. The UI supports Simplified Chinese, Traditional Chinese, Japanese, English and Korean.

Download the Windows x64 ZIP from [Releases](https://github.com/Shiroha129/BOOTH-Shelf/releases), extract it, and run `BOOTH-Shelf.exe`. Microsoft Edge WebView2 Runtime is required. Sign in through BOOTH's official website inside the app; the app keeps its own local session rather than importing Edge cookies. This is an unsigned pre-release and is not affiliated with BOOTH, pixiv, or BOOTH Library Manager.
