# Azure OpenAI Chat VSIX (Visual Studio 2026/2022)

这是一个 VSIX 插件示例（C#），功能：

- 在 **Tools** 菜单增加 `Azure OpenAI Chat`
- 打开 Tool Window 进行提问
- 调用 Azure OpenAI Chat Completions 终结点
- 读取本地 `config.toml`

## 1. config.toml

在解决方案根目录（推荐）放置 `config.toml`：

```toml
[azure_openai]
endpoint = "https://<your-resource>.openai.azure.com"
api_key = "<your-api-key>"
api_version = "2025-01-01-preview"
deployment = "gpt-4o-mini"
system_prompt = "你是一个资深 .NET 编程助手"
temperature = 0.2
max_tokens = 1200
timeout_seconds = 120
```

### 查找顺序

1) `AZURE_OPENAI_CONFIG_PATH` 环境变量指向的路径
2) `<solution_dir>/config.toml`
3) `<solution_dir>/.azure-openai/config.toml`
4) `%USERPROFILE%/config.toml`
5) `%USERPROFILE%/.azure-openai/config.toml`

## 2. 构建

在 `AzureOpenAI.Vsix` 项目目录执行：

```bash
dotnet restore
dotnet build -c Debug
```

当前项目会在构建后输出：`AzureOpenAI.Vsix/bin/Debug/AzureOpenAIChat.vsix`（Release 同理）。

## 3. 在 Visual Studio 运行调试

1. 用 Visual Studio 打开 `AzureOpenAI.Vsix.sln`
2. 设 `AzureOpenAI.Vsix` 为启动项目
3. F5 启动 Experimental Instance
4. Tools -> Azure OpenAI Chat

## 4. 说明

- endpoint 支持两种写法：
  - `https://xxx.openai.azure.com`
  - `https://xxx.openai.azure.com/openai`
- API Key 走 `api-key` 请求头。
- 当前示例走 `chat/completions` 接口。

