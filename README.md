#开发者留言

这个项目完全由ChatGPT生成，没有一行代码通过人工完成，设计之初就是为了自己使用，同时也免费开源给大家使用，具体功能包含了做出的pdf阅读，以及deepseek的翻译（apikey自费，存储在windows凭据中不会被setting泄露），其他一些常规功能就不做过多介绍了，更详细的项目细节都由gpt在下方介绍，由于代码全程在本机环境测试不能确保直接可用，遇到任何问题发我qq邮箱197971519@qq.com，任何人都可以拿去使用和修改但是需要标注出处同时不能收费，一切的费用都在openai公司重置额度的时候付过了。后续应该还会有其他改进，不定期更新。如果有用点个星，谢谢。

#使用方式

将packages的V1.0版本压缩包下载，本地解压后启动LanyuePDF.exe即可使用

翻译设置
API地址：https://api.deepseek.com
模型：deepseek-v4-flash
目标语言（默认中文兼容多个语言）：zh-CN
超时时间：60
APIkey（在windows凭据中以LocalPdfReader.DeepSeek.ApiKey名称存储）：自行在deepseek官网获取https://platform.deepseek.com
缓存上线（0-10000）：500

# 澜阅 PDF

澜阅 PDF 是一个面向 Windows 11 的本地 PDF 阅读器项目，仓库当前整理到 V1.0。项目重点放在本地阅读、独立 PDF Worker、阅读状态恢复、全文搜索、标准 PDF 批注、翻译辅助和发布验证，适合作为 WPF 桌面应用、PDFium 集成、进程隔离和本地数据持久化的开源参考。

## 仓库内容

```text
.
├─ src/                         产品源码
├─ tests/                       自动化测试
├─ scripts/                     发布脚本
├─ release/                     发布检查清单
├─ packages/                    分版本发布包
├─ Directory.Build.props        统一版本与产品元数据
└─ LocalPdfReader.sln           Visual Studio / dotnet 解决方案入口
```

`packages/` 中保留了已经生成好的发布包：

- `v0.1.0/LocalPdfReader-V0.1-source-for-web.zip`
- `v0.1.0/LocalPdfReader-win-x64.zip`
- `v0.5.0/LocalPdfReader-0.5.0-win-x64.zip`
- `v1.0.0/LanyuePDF-1.0.0-win-x64.zip`

V0.5 和 V1.0 发布包附带 `.sha256` 校验文件，V1.0 还包含 `release-manifest.json`。

## 技术栈

- 桌面框架：WPF
- 运行时：.NET 10，Windows x64
- PDF 引擎：PDFiumCore，放在独立 `LocalPdfReader.PdfWorker` 进程中运行
- 主进程与 Worker 通信：命名管道协议，协议定义在 `LocalPdfReader.PdfProtocol`
- 本地数据：SQLite，封装在 `LocalPdfReader.Infrastructure.Persistence`
- 配置与凭据：JSON 设置文件和 Windows Credential Store
- 翻译能力：DeepSeek 兼容接口，本地翻译缓存、术语表和历史记录
- 测试：xUnit，覆盖领域模型、应用服务、协议和集成流程

## 项目分层

- `LocalPdfReader.App`：WPF 窗口、ViewModel、用户交互和启动注册。
- `LocalPdfReader.Application`：阅读、选择、批注、翻译、历史记录等业务规则和接口。
- `LocalPdfReader.Domain`：稳定数据模型、枚举和值对象，不依赖 WPF、SQLite、HTTP 或 PDFium。
- `LocalPdfReader.Infrastructure`：SQLite、文件指纹、设置、凭据、日志、翻译提供方和 Worker 客户端。
- `LocalPdfReader.PdfProtocol`：主程序与 Worker 之间的消息契约、握手和序列化。
- `LocalPdfReader.PdfWorker`：独立 PDF 工作进程，负责打开文档、渲染页面、读取文本和读写标准 PDF 批注。
- `LocalPdfReader.Shared`：跨项目共享的轻量内容。

## 重要数据模型

领域模型集中在 `src/LocalPdfReader.Domain/`：

- `DocumentId`：文档会话中的稳定标识。
- `PdfDocumentInfo`：PDF 文件名、页数、加密状态、文本层、标题和作者。
- `PdfOutlineItem`：PDF 目录树节点。
- `PageRenderRequest` / `RenderedPageDescriptor`：页面渲染请求和共享内存渲染结果描述。
- `PdfTextPage`、`PdfTextLine`、`PdfTextSpan`：文本层抽取和搜索定位相关模型。
- `PdfSearchRequest`、`PdfSearchResult`：全文搜索请求与结果。
- `PdfStandardAnnotation`、`PdfAnnotationWriteOperation`、`PdfAnnotationSaveResult`：标准 PDF 批注读写模型。
- `DocumentRecord`、`RecentDocument`、`ReadingState`、`DocumentSessionSnapshot`：本地历史、最近文件、阅读位置和标签页会话。
- `TextHighlightAnnotation`：本地文本高亮批注记录。
- `TranslationRequest`、`TranslationChunk`、`TranslationCacheEntry`、`TranslationHistoryEntry`、`TranslationGlossaryEntry`：翻译请求、流式结果、缓存、历史和术语表。
- `PdfWorkerResourceLimits`：Worker 资源限制，用于约束打开文档数、共享位图和单页渲染尺寸。

## 构建与运行

开发环境建议使用 Windows 11、Visual Studio 2026 或支持 .NET 10 的 SDK。

```powershell
dotnet restore LocalPdfReader.sln
dotnet build LocalPdfReader.sln -c Release
dotnet test LocalPdfReader.sln -c Release
```

发布 Windows x64 自包含包：

```powershell
.\scripts\Publish-WindowsX64.ps1
```

发布脚本会读取 `Directory.Build.props` 中的版本和产品名。V1.0 正式显示名为“澜阅 PDF”，发布包名为 `LanyuePDF-1.0.0-win-x64.zip`。

## 使用发布包

1. 进入 `packages/` 中对应版本目录。
2. 解压 `*-win-x64.zip`。
3. 运行解压后的 `LanyuePDF.exe` 或 `LocalPdfReader.App.exe`。
4. 如需校验完整性，使用同目录 `.sha256` 文件比对压缩包哈希。

## 说明

本目录是从原项目整理出的软件发布仓库副本。除本 README 外，其余源码、脚本、测试、清单和发布包均按现有文件复制整理，未改动原始内容。
