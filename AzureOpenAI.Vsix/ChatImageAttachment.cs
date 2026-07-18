using System;

namespace AzureOpenAI.Vsix;

internal sealed class ChatImageAttachment
{
    public ChatImageAttachment(byte[] contentBytes, string mediaType, int width, int height)
    {
        ContentBytes = contentBytes ?? throw new ArgumentNullException(nameof(contentBytes));
        MediaType = string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
    }

    public byte[] ContentBytes { get; }
    public string MediaType { get; }
    public int Width { get; }
    public int Height { get; }

    public string ToDataUrl()
    {
        return "data:" + MediaType + ";base64," + Convert.ToBase64String(ContentBytes);
    }

    public static ChatImageAttachment FromPngBytes(byte[] pngBytes, int width, int height)
    {
        return new ChatImageAttachment(pngBytes, "image/png", width, height);
    }
}
