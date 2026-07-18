using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn;
using Tomlyn.Model;

namespace AzureOpenAI.Vsix;

internal static class ConfigTomlLoader
{
    public static async Task<(AzureOpenAIConfig Config, string LoadedPath)> LoadAsync(string? solutionDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetPath = GetCurrentUserCodexConfigPath();
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException(
                "未找到 config.toml。请在当前用户目录下创建文件：" + targetPath,
                targetPath);
        }

        string content;
        using (var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            content = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var model = Toml.ToModel(content);
        if (!(model is TomlTable root))
        {
            throw new InvalidDataException("config.toml 格式错误: " + targetPath);
        }

        AzureOpenAIConfig config;

        if (TryGetTable(root, "azure_openai", out var legacySection))
        {
            config = BuildLegacyConfig(root, legacySection, targetPath);
        }
        else if (TryGetModelProvidersAzure(root, out var providerSection))
        {
            config = BuildCodexConfig(root, providerSection, targetPath);
        }
        else
        {
            throw new InvalidDataException(
                "config.toml 既没有 [azure_openai]，也没有 [model_providers.azure]。\n" +
                "请在 " + targetPath + " 中配置其一。"
            );
        }

        return (config, targetPath);
    }

    private static string GetCurrentUserCodexConfigPath()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
        {
            throw new InvalidOperationException("无法获取当前用户目录（UserProfile）。");
        }

        return Path.GetFullPath(Path.Combine(userHome, ".codex", "config.toml"));
    }

    private static AzureOpenAIConfig BuildLegacyConfig(TomlTable root, TomlTable section, string sourcePath)
    {
        var endpoint = GetRequiredString(section, "endpoint", sourcePath);
        var apiKey = ResolveApiKey(section, sourcePath);
        var apiVersion = FirstNonEmpty(
            GetOptionalString(section, "api_version"),
            GetOptionalString(root, "api_version"),
            "2025-01-01-preview")!;

        var wireApi = NormalizeWireApi(
            FirstNonEmpty(
                GetOptionalString(section, "wire_api"),
                GetOptionalString(root, "wire_api"),
                InferDefaultWireApi(endpoint))
        );

        var deployment = FirstNonEmpty(
            GetOptionalString(section, "deployment"),
            GetOptionalString(root, "deployment"),
            GetOptionalString(root, "model"));

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidDataException(Path.GetFileName(sourcePath) + " 缺少字段 azure_openai.deployment（或根级 deployment/model）");
        }

        var systemPrompt = FirstNonEmpty(GetOptionalString(section, "system_prompt"), GetOptionalString(root, "system_prompt"));
        var temperature = FirstNonNull(GetOptionalDouble(section, "temperature"), GetOptionalDouble(root, "temperature")) ?? 0.2;
        var maxTokens = FirstNonNull(GetOptionalInt(section, "max_tokens"), GetOptionalInt(root, "max_tokens"));
        var timeoutSeconds = FirstNonNull(GetOptionalInt(section, "timeout_seconds"), GetOptionalInt(root, "timeout_seconds")) ?? 120;

        if (timeoutSeconds <= 0)
        {
            timeoutSeconds = 120;
        }

        return new AzureOpenAIConfig
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            ApiVersion = apiVersion,
            Deployment = deployment.Trim(),
            WireApi = wireApi,
            SystemPrompt = systemPrompt,
            Temperature = temperature,
            MaxTokens = maxTokens,
            TimeoutSeconds = timeoutSeconds
        };
    }

    private static AzureOpenAIConfig BuildCodexConfig(TomlTable root, TomlTable providerSection, string sourcePath)
    {
        var modelProvider = GetOptionalString(root, "model_provider");
        if (!string.IsNullOrWhiteSpace(modelProvider)
            && !modelProvider.Trim().Equals("azure", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("config.toml 的 model_provider 当前为 '" + modelProvider + "'，不是 azure。请改为 azure。" );
        }

        var endpoint = FirstNonEmpty(
            GetOptionalString(providerSection, "endpoint"),
            GetOptionalString(providerSection, "base_url"));

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidDataException(Path.GetFileName(sourcePath) + " 缺少字段 model_providers.azure.base_url（或 endpoint）");
        }

        var apiKey = ResolveApiKey(providerSection, sourcePath);

        var apiVersion = FirstNonEmpty(
            GetOptionalString(providerSection, "api_version"),
            GetOptionalString(root, "api_version"),
            "2025-01-01-preview")!;

        var wireApi = NormalizeWireApi(
            FirstNonEmpty(
                GetOptionalString(providerSection, "wire_api"),
                GetOptionalString(root, "wire_api"),
                InferDefaultWireApi(endpoint))
        );

        var deployment = FirstNonEmpty(
            GetOptionalString(providerSection, "deployment"),
            GetOptionalString(providerSection, "azure_deployment"),
            GetOptionalString(root, "deployment"),
            GetOptionalString(root, "model"));

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidDataException("config.toml 缺少部署名，请设置 model_providers.azure.deployment（或根级 model）。");
        }

        var systemPrompt = FirstNonEmpty(GetOptionalString(providerSection, "system_prompt"), GetOptionalString(root, "system_prompt"));
        var temperature = FirstNonNull(GetOptionalDouble(providerSection, "temperature"), GetOptionalDouble(root, "temperature")) ?? 0.2;
        var maxTokens = FirstNonNull(GetOptionalInt(providerSection, "max_tokens"), GetOptionalInt(root, "max_tokens"));
        var timeoutSeconds = FirstNonNull(GetOptionalInt(providerSection, "timeout_seconds"), GetOptionalInt(root, "timeout_seconds")) ?? 120;

        if (timeoutSeconds <= 0)
        {
            timeoutSeconds = 120;
        }

        return new AzureOpenAIConfig
        {
            Endpoint = endpoint.Trim(),
            ApiKey = apiKey,
            ApiVersion = apiVersion,
            Deployment = deployment.Trim(),
            WireApi = wireApi,
            SystemPrompt = systemPrompt,
            Temperature = temperature,
            MaxTokens = maxTokens,
            TimeoutSeconds = timeoutSeconds
        };
    }


    private static string NormalizeWireApi(string? wireApi)
    {
        var normalized = (wireApi ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "chat_completions";
        }

        if (normalized == "responses" || normalized == "response")
        {
            return "responses";
        }

        if (normalized == "chat" || normalized == "chat_completions" || normalized == "chat-completions")
        {
            return "chat_completions";
        }

        return normalized;
    }

    private static string InferDefaultWireApi(string endpoint)
    {
        var e = (endpoint ?? string.Empty).Trim().ToLowerInvariant();
        if (e.EndsWith("/openai/v1") || e.Contains("/openai/v1/"))
        {
            return "responses";
        }

        return "chat_completions";
    }
    private static string ResolveApiKey(TomlTable section, string sourcePath)
    {
        var directKey = GetOptionalString(section, "api_key");
        if (!string.IsNullOrWhiteSpace(directKey))
        {
            return directKey.Trim();
        }

        var envKeyName = FirstNonEmpty(GetOptionalString(section, "env_key"), "AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKeyName))
        {
            var envValue = Environment.GetEnvironmentVariable(envKeyName.Trim());
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return envValue.Trim();
            }
        }

        throw new InvalidDataException(Path.GetFileName(sourcePath) + " 未提供 api_key，且环境变量 " + envKeyName + " 为空。");
    }

    private static bool TryGetModelProvidersAzure(TomlTable root, out TomlTable providerSection)
    {
        providerSection = null!;

        if (!TryGetTable(root, "model_providers", out var providers))
        {
            return false;
        }

        return TryGetTable(providers, "azure", out providerSection);
    }

    private static bool TryGetTable(TomlTable table, string key, out TomlTable child)
    {
        child = null!;

        if (!table.TryGetValue(key, out var sectionObj) || sectionObj == null)
        {
            return false;
        }

        if (!(sectionObj is TomlTable section))
        {
            return false;
        }

        child = section;
        return true;
    }

    private static string GetRequiredString(TomlTable section, string key, string sourcePath)
    {
        var value = GetOptionalString(section, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(Path.GetFileName(sourcePath) + " 缺少字段 " + key);
        }

        return value.Trim();
    }

    private static string? GetOptionalString(TomlTable section, string key)
    {
        if (!section.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static int? GetOptionalInt(TomlTable section, string key)
    {
        if (!section.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (value is long l)
        {
            return checked((int)l);
        }

        if (value is int i)
        {
            return i;
        }

        if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? GetOptionalDouble(TomlTable section, string key)
    {
        if (!section.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (value is double d)
        {
            return d;
        }

        if (value is float f)
        {
            return f;
        }

        if (value is decimal m)
        {
            return (double)m;
        }

        if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static T? FirstNonNull<T>(params T?[] values) where T : struct
    {
        foreach (var value in values)
        {
            if (value.HasValue)
            {
                return value.Value;
            }
        }

        return null;
    }
}

